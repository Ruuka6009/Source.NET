using Silk.NET.Vulkan;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Swapchain plus its image views. Recreated on resize/alt-tab (the first thing that breaks,
/// per VULKAN_TODO.md - keep all of that logic here).
/// </summary>
public unsafe class VulkanSwapchain : IDisposable
{
	readonly VulkanCore core;

	public SwapchainKHR Swapchain { get; private set; }
	public Image[] Images { get; private set; } = [];
	public ImageView[] ImageViews { get; private set; } = [];
	public Format ImageFormat { get; private set; }
	public Extent2D Extent { get; private set; }

	public VulkanSwapchain(VulkanCore core) {
		this.core = core;
	}

	public bool Create(uint width, uint height, bool vsync) {
		Vk vk = core.Vk;

		core.KhrSurface.GetPhysicalDeviceSurfaceCapabilities(core.PhysicalDevice, core.Surface, out SurfaceCapabilitiesKHR caps);

		SurfaceFormatKHR surfaceFormat = ChooseSurfaceFormat();
		PresentModeKHR presentMode = ChoosePresentMode(vsync);
		Extent2D extent = ChooseExtent(in caps, width, height);

		uint imageCount = caps.MinImageCount + 1;
		if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
			imageCount = caps.MaxImageCount;

		SwapchainCreateInfoKHR createInfo = new() {
			SType = StructureType.SwapchainCreateInfoKhr,
			Surface = core.Surface,
			MinImageCount = imageCount,
			ImageFormat = surfaceFormat.Format,
			ImageColorSpace = surfaceFormat.ColorSpace,
			ImageExtent = extent,
			ImageArrayLayers = 1,
			ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
			PreTransform = caps.CurrentTransform,
			CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
			PresentMode = presentMode,
			Clipped = true,
			OldSwapchain = Swapchain
		};

		uint* familyIndices = stackalloc uint[] { core.GraphicsQueueFamily, core.PresentQueueFamily };
		if (core.GraphicsQueueFamily != core.PresentQueueFamily) {
			createInfo.ImageSharingMode = SharingMode.Concurrent;
			createInfo.QueueFamilyIndexCount = 2;
			createInfo.PQueueFamilyIndices = familyIndices;
		}
		else
			createInfo.ImageSharingMode = SharingMode.Exclusive;

		Result result = core.KhrSwapchain.CreateSwapchain(core.Device, &createInfo, null, out SwapchainKHR newSwapchain);
		if (result != Result.Success) {
			Warning($"Vulkan: vkCreateSwapchainKHR failed ({result})\n");
			return false;
		}

		DestroyViewsAndSwapchain();
		Swapchain = newSwapchain;
		ImageFormat = surfaceFormat.Format;
		Extent = extent;

		uint actualCount = 0;
		core.KhrSwapchain.GetSwapchainImages(core.Device, Swapchain, &actualCount, null);
		Images = new Image[actualCount];
		fixed (Image* imagesPtr = Images)
			core.KhrSwapchain.GetSwapchainImages(core.Device, Swapchain, &actualCount, imagesPtr);

		ImageViews = new ImageView[actualCount];
		for (int i = 0; i < actualCount; i++) {
			ImageViewCreateInfo viewInfo = new() {
				SType = StructureType.ImageViewCreateInfo,
				Image = Images[i],
				ViewType = ImageViewType.Type2D,
				Format = ImageFormat,
				SubresourceRange = new ImageSubresourceRange {
					AspectMask = ImageAspectFlags.ColorBit,
					BaseMipLevel = 0,
					LevelCount = 1,
					BaseArrayLayer = 0,
					LayerCount = 1
				}
			};
			if (vk.CreateImageView(core.Device, &viewInfo, null, out ImageViews[i]) != Result.Success) {
				Warning("Vulkan: image view creation failed\n");
				return false;
			}
		}
		return true;
	}

	/// <summary>Recreate after resize or an out-of-date result from acquire/present.</summary>
	public bool Recreate(uint width, uint height, bool vsync) {
		core.Vk.DeviceWaitIdle(core.Device);
		return Create(width, height, vsync);
	}

	SurfaceFormatKHR ChooseSurfaceFormat() {
		uint count = 0;
		core.KhrSurface.GetPhysicalDeviceSurfaceFormats(core.PhysicalDevice, core.Surface, &count, null);
		SurfaceFormatKHR[] formats = new SurfaceFormatKHR[count];
		fixed (SurfaceFormatKHR* formatsPtr = formats)
			core.KhrSurface.GetPhysicalDeviceSurfaceFormats(core.PhysicalDevice, core.Surface, &count, formatsPtr);

		foreach (SurfaceFormatKHR format in formats) {
			if (format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
				return format;
		}
		return formats[0];
	}

	PresentModeKHR ChoosePresentMode(bool vsync) {
		if (vsync)
			return PresentModeKHR.FifoKhr; // always available

		uint count = 0;
		core.KhrSurface.GetPhysicalDeviceSurfacePresentModes(core.PhysicalDevice, core.Surface, &count, null);
		PresentModeKHR[] modes = new PresentModeKHR[count];
		fixed (PresentModeKHR* modesPtr = modes)
			core.KhrSurface.GetPhysicalDeviceSurfacePresentModes(core.PhysicalDevice, core.Surface, &count, modesPtr);

		foreach (PresentModeKHR mode in modes)
			if (mode == PresentModeKHR.MailboxKhr)
				return mode;
		foreach (PresentModeKHR mode in modes)
			if (mode == PresentModeKHR.ImmediateKhr)
				return mode;
		return PresentModeKHR.FifoKhr;
	}

	static Extent2D ChooseExtent(in SurfaceCapabilitiesKHR caps, uint width, uint height) {
		if (caps.CurrentExtent.Width != uint.MaxValue)
			return caps.CurrentExtent;
		return new Extent2D(
			Math.Clamp(width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
			Math.Clamp(height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height)
		);
	}

	void DestroyViewsAndSwapchain() {
		foreach (ImageView view in ImageViews)
			core.Vk.DestroyImageView(core.Device, view, null);
		ImageViews = [];
		if (Swapchain.Handle != 0) {
			core.KhrSwapchain.DestroySwapchain(core.Device, Swapchain, null);
			Swapchain = default;
		}
	}

	public void Dispose() => DestroyViewsAndSwapchain();
}
