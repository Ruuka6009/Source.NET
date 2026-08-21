using Silk.NET.Vulkan;

using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Frames-in-flight, command pools/buffers and the acquire -> record -> submit -> present cycle.
/// Phase 2 goal: clear the screen to a colour. Nothing else.
/// </summary>
public unsafe class VulkanFrameLoop : IDisposable
{
	public const int FramesInFlight = 2;

	readonly VulkanCore core;
	readonly VulkanSwapchain swapchain;

	CommandPool commandPool;
	readonly CommandBuffer[] commandBuffers = new CommandBuffer[FramesInFlight];
	readonly VkSemaphore[] imageAvailable = new VkSemaphore[FramesInFlight];
	readonly VkSemaphore[] renderFinished = new VkSemaphore[FramesInFlight];
	readonly Fence[] inFlight = new Fence[FramesInFlight];
	int currentFrame;

	/// <summary>Set when acquire/present reported out-of-date; owner must Recreate the swapchain.</summary>
	public bool NeedsRecreate { get; private set; }

	public VulkanFrameLoop(VulkanCore core, VulkanSwapchain swapchain) {
		this.core = core;
		this.swapchain = swapchain;
	}

	public bool Init() {
		Vk vk = core.Vk;

		CommandPoolCreateInfo poolInfo = new() {
			SType = StructureType.CommandPoolCreateInfo,
			Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
			QueueFamilyIndex = core.GraphicsQueueFamily
		};
		if (vk.CreateCommandPool(core.Device, &poolInfo, null, out commandPool) != Result.Success) {
			Warning("Vulkan: command pool creation failed\n");
			return false;
		}

		CommandBufferAllocateInfo allocInfo = new() {
			SType = StructureType.CommandBufferAllocateInfo,
			CommandPool = commandPool,
			Level = CommandBufferLevel.Primary,
			CommandBufferCount = FramesInFlight
		};
		fixed (CommandBuffer* buffersPtr = commandBuffers) {
			if (vk.AllocateCommandBuffers(core.Device, &allocInfo, buffersPtr) != Result.Success) {
				Warning("Vulkan: command buffer allocation failed\n");
				return false;
			}
		}

		SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
		FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
		for (int i = 0; i < FramesInFlight; i++) {
			if (vk.CreateSemaphore(core.Device, &semaphoreInfo, null, out imageAvailable[i]) != Result.Success ||
				vk.CreateSemaphore(core.Device, &semaphoreInfo, null, out renderFinished[i]) != Result.Success ||
				vk.CreateFence(core.Device, &fenceInfo, null, out inFlight[i]) != Result.Success) {
				Warning("Vulkan: sync object creation failed\n");
				return false;
			}
		}
		return true;
	}

	/// <summary>Clears the current swapchain image to the given colour and presents it.</summary>
	public void DrawClearFrame(float r, float g, float b) {
		Vk vk = core.Vk;
		NeedsRecreate = false;

		vk.WaitForFences(core.Device, 1, in inFlight[currentFrame], true, ulong.MaxValue);

		uint imageIndex = 0;
		Result acquire = core.KhrSwapchain.AcquireNextImage(core.Device, swapchain.Swapchain, ulong.MaxValue, imageAvailable[currentFrame], default, &imageIndex);
		if (acquire == Result.ErrorOutOfDateKhr) {
			NeedsRecreate = true;
			return;
		}
		if (acquire != Result.Success && acquire != Result.SuboptimalKhr) {
			Warning($"Vulkan: vkAcquireNextImageKHR failed ({acquire})\n");
			return;
		}

		vk.ResetFences(core.Device, 1, in inFlight[currentFrame]);

		CommandBuffer cmd = commandBuffers[currentFrame];
		vk.ResetCommandBuffer(cmd, 0);

		CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
		vk.BeginCommandBuffer(cmd, &beginInfo);

		TransitionImage(vk, cmd, swapchain.Images[imageIndex],
			ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
			PipelineStageFlags2.TopOfPipeBit, 0,
			PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);

		RenderingAttachmentInfo colorAttachment = new() {
			SType = StructureType.RenderingAttachmentInfo,
			ImageView = swapchain.ImageViews[imageIndex],
			ImageLayout = ImageLayout.ColorAttachmentOptimal,
			LoadOp = AttachmentLoadOp.Clear,
			StoreOp = AttachmentStoreOp.Store,
			ClearValue = new ClearValue(new ClearColorValue(r, g, b, 1.0f))
		};
		RenderingInfo renderingInfo = new() {
			SType = StructureType.RenderingInfo,
			RenderArea = new Rect2D(new Offset2D(0, 0), swapchain.Extent),
			LayerCount = 1,
			ColorAttachmentCount = 1,
			PColorAttachments = &colorAttachment
		};
		vk.CmdBeginRendering(cmd, &renderingInfo);
		vk.CmdEndRendering(cmd);

		TransitionImage(vk, cmd, swapchain.Images[imageIndex],
			ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr,
			PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
			PipelineStageFlags2.BottomOfPipeBit, 0);

		vk.EndCommandBuffer(cmd);

		SemaphoreSubmitInfo waitInfo = new() {
			SType = StructureType.SemaphoreSubmitInfo,
			Semaphore = imageAvailable[currentFrame],
			StageMask = PipelineStageFlags2.ColorAttachmentOutputBit
		};
		SemaphoreSubmitInfo signalInfo = new() {
			SType = StructureType.SemaphoreSubmitInfo,
			Semaphore = renderFinished[currentFrame],
			StageMask = PipelineStageFlags2.AllCommandsBit
		};
		CommandBufferSubmitInfo cmdInfo = new() {
			SType = StructureType.CommandBufferSubmitInfo,
			CommandBuffer = cmd
		};
		SubmitInfo2 submitInfo = new() {
			SType = StructureType.SubmitInfo2,
			WaitSemaphoreInfoCount = 1,
			PWaitSemaphoreInfos = &waitInfo,
			CommandBufferInfoCount = 1,
			PCommandBufferInfos = &cmdInfo,
			SignalSemaphoreInfoCount = 1,
			PSignalSemaphoreInfos = &signalInfo
		};
		vk.QueueSubmit2(core.GraphicsQueue, 1, &submitInfo, inFlight[currentFrame]);

		SwapchainKHR swapchainHandle = swapchain.Swapchain;
		VkSemaphore renderFinishedHandle = renderFinished[currentFrame];
		PresentInfoKHR presentInfo = new() {
			SType = StructureType.PresentInfoKhr,
			WaitSemaphoreCount = 1,
			PWaitSemaphores = &renderFinishedHandle,
			SwapchainCount = 1,
			PSwapchains = &swapchainHandle,
			PImageIndices = &imageIndex
		};
		Result present = core.KhrSwapchain.QueuePresent(core.PresentQueue, &presentInfo);
		if (present == Result.ErrorOutOfDateKhr || present == Result.SuboptimalKhr)
			NeedsRecreate = true;
		else if (present != Result.Success)
			Warning($"Vulkan: vkQueuePresentKHR failed ({present})\n");

		currentFrame = (currentFrame + 1) % FramesInFlight;
	}

	static void TransitionImage(Vk vk, CommandBuffer cmd, Image image,
		ImageLayout oldLayout, ImageLayout newLayout,
		PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
		PipelineStageFlags2 dstStage, AccessFlags2 dstAccess) {
		ImageMemoryBarrier2 barrier = new() {
			SType = StructureType.ImageMemoryBarrier2,
			SrcStageMask = srcStage,
			SrcAccessMask = srcAccess,
			DstStageMask = dstStage,
			DstAccessMask = dstAccess,
			OldLayout = oldLayout,
			NewLayout = newLayout,
			SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
			DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
			Image = image,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
		};
		DependencyInfo dependencyInfo = new() {
			SType = StructureType.DependencyInfo,
			ImageMemoryBarrierCount = 1,
			PImageMemoryBarriers = &barrier
		};
		vk.CmdPipelineBarrier2(cmd, &dependencyInfo);
	}

	public void Dispose() {
		Vk vk = core.Vk;
		vk.DeviceWaitIdle(core.Device);
		for (int i = 0; i < FramesInFlight; i++) {
			if (imageAvailable[i].Handle != 0) vk.DestroySemaphore(core.Device, imageAvailable[i], null);
			if (renderFinished[i].Handle != 0) vk.DestroySemaphore(core.Device, renderFinished[i], null);
			if (inFlight[i].Handle != 0) vk.DestroyFence(core.Device, inFlight[i], null);
		}
		if (commandPool.Handle != 0)
			vk.DestroyCommandPool(core.Device, commandPool, null);
	}
}
