using Source.Common.Launcher;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// The Vulkan sibling of SDL3_OpenGL46_Context. In Vulkan the GL concepts map as follows:
/// MakeCurrent is a no-op (no thread-bound context), SwapBuffers means "present" (done by the
/// frame loop at queue submit), and the swap interval is the present mode picked at swapchain
/// creation - so changing it schedules a swapchain recreate.
/// </summary>
public class VulkanGraphicsContext(VulkanCore core, VulkanSwapchain swapchain) : IGraphicsContext
{
	public nint HardwareHandle => unchecked((nint)core.Device.Handle);

	public bool VSync { get; private set; } = true;
	public bool VSyncChanged { get; private set; }

	public void MakeCurrent() { }

	public void SetSwapInterval(float swapInterval) {
		bool vsync = swapInterval != 0;
		if (vsync != VSync) {
			VSync = vsync;
			VSyncChanged = true; // owner recreates the swapchain with the new present mode
		}
	}

	public void SwapBuffers() {
		// Present happens in VulkanFrameLoop at queue submit; nothing to do here.
	}

	public void AcknowledgeVSyncChange() => VSyncChanged = false;
}
