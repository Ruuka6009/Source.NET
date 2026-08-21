using Source.Common.ShaderAPI;

namespace Source.Common.Launcher;

public interface IGraphicsProvider {
	bool PrepareContext(GraphicsDriver driver);
	IGraphicsContext? CreateContext(in ShaderDeviceInfo driver, IWindow window = null);

	unsafe delegate* unmanaged[Cdecl]<byte*, void*> GL_LoadExtensionsPtr();

	// Vulkan hooks. The window system owns the window, so it also owns instance extension
	// enumeration and surface creation. Handles are nint so Source.Common stays Vulkan-free.
	/// <summary>
	/// Instance extensions the window system requires (ex. VK_KHR_surface + platform surface).
	/// Empty when the provider can't do Vulkan.
	/// </summary>
	string[] GetVulkanInstanceExtensions() => [];
	/// <summary>
	/// Creates a VkSurfaceKHR for the window on the given VkInstance. 0 on failure.
	/// </summary>
	nint CreateVulkanSurface(nint vkInstance, IWindow window) => 0;
	void DestroyVulkanSurface(nint vkInstance, nint vkSurface) { }
	bool GetVulkanPresentationSupport(nint vkInstance, nint vkPhysicalDevice, uint queueFamilyIndex) => false;
}
