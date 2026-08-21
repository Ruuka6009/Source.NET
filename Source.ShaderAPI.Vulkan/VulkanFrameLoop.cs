using Silk.NET.Vulkan;

using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Frames-in-flight, command pools/buffers, the depth buffer and the
/// acquire -> record -> submit -> present cycle. Rendering passes are opened per render target by
/// the shader API, not here, so it can switch targets within a frame.
/// </summary>
public unsafe class VulkanFrameLoop : IDisposable
{
	public const int FramesInFlight = 2;
	public const Format DepthFormat = Format.D32Sfloat;

	readonly VulkanCore core;
	readonly VulkanSwapchain swapchain;
	readonly VulkanMemoryAllocator allocator;

	CommandPool commandPool;
	readonly CommandBuffer[] commandBuffers = new CommandBuffer[FramesInFlight];
	readonly VkSemaphore[] imageAvailable = new VkSemaphore[FramesInFlight];
	// Signaled-by-present semaphores must be per swapchain image, not per frame in flight:
	// a present may still be reading one when the same frame index comes around again.
	VkSemaphore[] renderFinished = [];
	readonly Fence[] inFlight = new Fence[FramesInFlight];
	int currentFrame;
	uint currentImageIndex;

	Image depthImage;
	ImageView depthImageView;
	VulkanMemoryAllocator.Allocation depthAlloc;

	/// <summary>Set when acquire/present reported out-of-date; owner must Recreate the swapchain.</summary>
	public bool NeedsRecreate { get; private set; }

	/// <summary>True between a successful BeginFrame and EndFrameAndPresent.</summary>
	public bool FrameActive { get; private set; }

	public CommandBuffer Cmd => commandBuffers[currentFrame];
	public int FrameIndex => currentFrame;

	public VulkanFrameLoop(VulkanCore core, VulkanSwapchain swapchain, VulkanMemoryAllocator allocator) {
		this.core = core;
		this.swapchain = swapchain;
		this.allocator = allocator;
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
				vk.CreateFence(core.Device, &fenceInfo, null, out inFlight[i]) != Result.Success) {
				Warning("Vulkan: sync object creation failed\n");
				return false;
			}
		}
		return OnSwapchainRecreated();
	}

	/// <summary>Recreates the per-image present semaphores and the depth buffer. Call after swapchain (re)creation.</summary>
	public bool OnSwapchainRecreated() {
		Vk vk = core.Vk;
		vk.DeviceWaitIdle(core.Device);

		foreach (VkSemaphore semaphore in renderFinished)
			if (semaphore.Handle != 0)
				vk.DestroySemaphore(core.Device, semaphore, null);

		SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
		renderFinished = new VkSemaphore[swapchain.Images.Length];
		for (int i = 0; i < renderFinished.Length; i++) {
			if (vk.CreateSemaphore(core.Device, &semaphoreInfo, null, out renderFinished[i]) != Result.Success) {
				Warning("Vulkan: present semaphore creation failed\n");
				return false;
			}
		}

		return CreateDepthBuffer();
	}

	bool CreateDepthBuffer() {
		Vk vk = core.Vk;

		DestroyDepthBuffer();

		ImageCreateInfo imageInfo = new() {
			SType = StructureType.ImageCreateInfo,
			ImageType = ImageType.Type2D,
			Format = DepthFormat,
			Extent = new Extent3D(swapchain.Extent.Width, swapchain.Extent.Height, 1),
			MipLevels = 1,
			ArrayLayers = 1,
			Samples = SampleCountFlags.Count1Bit,
			Tiling = ImageTiling.Optimal,
			Usage = ImageUsageFlags.DepthStencilAttachmentBit,
			SharingMode = SharingMode.Exclusive,
			InitialLayout = ImageLayout.Undefined
		};
		if (vk.CreateImage(core.Device, &imageInfo, null, out depthImage) != Result.Success) {
			Warning("Vulkan: depth image creation failed\n");
			return false;
		}

		vk.GetImageMemoryRequirements(core.Device, depthImage, out MemoryRequirements reqs);
		depthAlloc = allocator.Allocate(in reqs, MemoryPropertyFlags.DeviceLocalBit);
		vk.BindImageMemory(core.Device, depthImage, depthAlloc.Memory, depthAlloc.Offset);

		ImageViewCreateInfo viewInfo = new() {
			SType = StructureType.ImageViewCreateInfo,
			Image = depthImage,
			ViewType = ImageViewType.Type2D,
			Format = DepthFormat,
			SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1)
		};
		if (vk.CreateImageView(core.Device, &viewInfo, null, out depthImageView) != Result.Success) {
			Warning("Vulkan: depth image view creation failed\n");
			return false;
		}
		return true;
	}

	void DestroyDepthBuffer() {
		Vk vk = core.Vk;
		if (depthImageView.Handle != 0) {
			vk.DestroyImageView(core.Device, depthImageView, null);
			depthImageView = default;
		}
		if (depthImage.Handle != 0) {
			vk.DestroyImage(core.Device, depthImage, null);
			allocator.Free(in depthAlloc);
			depthImage = default;
			depthAlloc = default;
		}
	}

	/// <summary>
	/// Acquires the next swapchain image and opens the frame's command buffer. No rendering pass
	/// is started here - the shader API opens one per render target through
	/// <see cref="BeginRendering"/>, so it can switch targets mid-frame.
	/// Returns false when the swapchain needs recreation (NeedsRecreate) or on failure.
	/// </summary>
	public bool BeginFrame() {
		if (FrameActive)
			return true;

		Vk vk = core.Vk;
		NeedsRecreate = false;

		vk.WaitForFences(core.Device, 1, in inFlight[currentFrame], true, ulong.MaxValue);

		uint imageIndex = 0;
		Result acquire = core.KhrSwapchain.AcquireNextImage(core.Device, swapchain.Swapchain, ulong.MaxValue, imageAvailable[currentFrame], default, &imageIndex);
		if (acquire == Result.ErrorOutOfDateKhr) {
			NeedsRecreate = true;
			return false;
		}
		if (acquire != Result.Success && acquire != Result.SuboptimalKhr) {
			Warning($"Vulkan: vkAcquireNextImageKHR failed ({acquire})\n");
			return false;
		}
		currentImageIndex = imageIndex;

		vk.ResetFences(core.Device, 1, in inFlight[currentFrame]);

		CommandBuffer cmd = commandBuffers[currentFrame];
		vk.ResetCommandBuffer(cmd, 0);

		CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
		vk.BeginCommandBuffer(cmd, &beginInfo);

		TransitionImage(cmd, swapchain.Images[imageIndex], ImageAspectFlags.ColorBit,
			ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
			PipelineStageFlags2.TopOfPipeBit, 0,
			PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);

		TransitionImage(cmd, depthImage, ImageAspectFlags.DepthBit,
			ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal,
			PipelineStageFlags2.TopOfPipeBit, 0,
			PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
			AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit);

		FrameActive = true;
		return true;
	}

	/// <summary>The attachments one rendering pass draws into.</summary>
	public struct RenderPassTarget
	{
		public ImageView ColorView;
		public Format ColorFormat;
		/// <summary>Zero when the pass has no depth attachment.</summary>
		public ImageView DepthView;
		public Format DepthFormat;
		public Extent2D Extent;
	}

	/// <summary>True while a rendering pass is open (draws are only legal here).</summary>
	public bool RenderingActive { get; private set; }

	public RenderPassTarget SwapchainTarget => new() {
		ColorView = swapchain.ImageViews[currentImageIndex],
		ColorFormat = swapchain.ImageFormat,
		DepthView = depthImageView,
		DepthFormat = DepthFormat,
		Extent = swapchain.Extent
	};

	/// <summary>Shared depth buffer, used by any target that does not bring its own.</summary>
	public ImageView SharedDepthView => depthImageView;
	public Extent2D SharedDepthExtent => swapchain.Extent;

	/// <summary>
	/// Opens a rendering pass on the given attachments. <paramref name="clearColor"/> is used for
	/// the frame's first pass on the backbuffer; everything else loads what is already there
	/// (targets whose contents are undefined pass <paramref name="discardExisting"/>).
	/// </summary>
	public void BeginRendering(in RenderPassTarget target, bool clearColor, float r, float g, float b, bool discardExisting) {
		if (!FrameActive || RenderingActive)
			return;

		AttachmentLoadOp colorLoad = clearColor ? AttachmentLoadOp.Clear
			: discardExisting ? AttachmentLoadOp.DontCare
			: AttachmentLoadOp.Load;

		RenderingAttachmentInfo colorAttachment = new() {
			SType = StructureType.RenderingAttachmentInfo,
			ImageView = target.ColorView,
			ImageLayout = ImageLayout.ColorAttachmentOptimal,
			LoadOp = colorLoad,
			StoreOp = AttachmentStoreOp.Store,
			ClearValue = new ClearValue(new ClearColorValue(r, g, b, 1.0f))
		};
		RenderingAttachmentInfo depthAttachment = new() {
			SType = StructureType.RenderingAttachmentInfo,
			ImageView = target.DepthView,
			ImageLayout = ImageLayout.DepthAttachmentOptimal,
			// Depth is never preserved across passes (the engine clears it when it matters), and
			// StoreOp.DontCare lets tilers drop it entirely.
			LoadOp = clearColor ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
			StoreOp = AttachmentStoreOp.Store,
			ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(1.0f, 0))
		};

		RenderingInfo renderingInfo = new() {
			SType = StructureType.RenderingInfo,
			RenderArea = new Rect2D(new Offset2D(0, 0), target.Extent),
			LayerCount = 1,
			ColorAttachmentCount = 1,
			PColorAttachments = &colorAttachment,
			PDepthAttachment = target.DepthView.Handle != 0 ? &depthAttachment : null
		};
		core.Vk.CmdBeginRendering(commandBuffers[currentFrame], &renderingInfo);
		RenderingActive = true;
	}

	public void EndRendering() {
		if (!RenderingActive)
			return;
		core.Vk.CmdEndRendering(commandBuffers[currentFrame]);
		RenderingActive = false;
	}

	/// <summary>Layout transition on an arbitrary image, for render-target switches.</summary>
	public void TransitionImage(CommandBuffer cmd, Image image, ImageAspectFlags aspect,
		ImageLayout oldLayout, ImageLayout newLayout,
		PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
		PipelineStageFlags2 dstStage, AccessFlags2 dstAccess, int mips = 1, int layers = 1) {
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
			SubresourceRange = new ImageSubresourceRange(aspect, 0, (uint)mips, 0, (uint)layers)
		};
		DependencyInfo dependencyInfo = new() {
			SType = StructureType.DependencyInfo,
			ImageMemoryBarrierCount = 1,
			PImageMemoryBarriers = &barrier
		};
		core.Vk.CmdPipelineBarrier2(cmd, &dependencyInfo);
	}

	/// <summary>Mid-pass clear (the engine's explicit ClearBuffers) inside the open rendering pass.</summary>
	public void ClearAttachments(bool clearColor, bool clearDepth, float r, float g, float b, Rect2D rect) {
		if (!RenderingActive)
			return;

		Vk vk = core.Vk;
		ClearAttachment* clears = stackalloc ClearAttachment[2];
		int count = 0;
		if (clearColor) {
			clears[count++] = new ClearAttachment {
				AspectMask = ImageAspectFlags.ColorBit,
				ColorAttachment = 0,
				ClearValue = new ClearValue(new ClearColorValue(r, g, b, 1.0f))
			};
		}
		if (clearDepth) {
			clears[count++] = new ClearAttachment {
				AspectMask = ImageAspectFlags.DepthBit,
				ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(1.0f, 0))
			};
		}
		if (count == 0)
			return;

		if (rect.Extent.Width == 0 || rect.Extent.Height == 0)
			rect = new Rect2D(new Offset2D(0, 0), swapchain.Extent);

		ClearRect clearRect = new() { Rect = rect, BaseArrayLayer = 0, LayerCount = 1 };
		vk.CmdClearAttachments(Cmd, (uint)count, clears, 1, &clearRect);
	}

	/// <summary>Closes the frame's rendering pass, submits, presents and advances the frame index.</summary>
	public void EndFrameAndPresent() {
		if (!FrameActive)
			return;

		Vk vk = core.Vk;
		CommandBuffer cmd = commandBuffers[currentFrame];
		uint imageIndex = currentImageIndex;

		EndRendering();

		TransitionImage(cmd, swapchain.Images[imageIndex], ImageAspectFlags.ColorBit,
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
			Semaphore = renderFinished[imageIndex],
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
		VkSemaphore renderFinishedHandle = renderFinished[imageIndex];
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
		FrameActive = false;
	}

	public void Dispose() {
		Vk vk = core.Vk;
		vk.DeviceWaitIdle(core.Device);
		DestroyDepthBuffer();
		for (int i = 0; i < FramesInFlight; i++) {
			if (imageAvailable[i].Handle != 0) vk.DestroySemaphore(core.Device, imageAvailable[i], null);
			if (inFlight[i].Handle != 0) vk.DestroyFence(core.Device, inFlight[i], null);
		}
		foreach (VkSemaphore semaphore in renderFinished)
			if (semaphore.Handle != 0)
				vk.DestroySemaphore(core.Device, semaphore, null);
		if (commandPool.Handle != 0)
			vk.DestroyCommandPool(core.Device, commandPool, null);
	}
}
