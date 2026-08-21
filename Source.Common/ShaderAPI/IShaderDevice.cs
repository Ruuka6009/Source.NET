using Source.Common.Bitmap;
using Source.Common.MaterialSystem;

namespace Source.Common.ShaderAPI;

[Flags]
public enum GraphicsDriver : ulong
{
	OpenGL = 1 << 62,
	// If we even try to do something with these one day (extremely unlikely)
	DirectX = 1 << 61,
	Vulkan = 1 << 60,
	Metal = 1 << 59,

	/// <summary>
	/// Extracts the version
	/// </summary>
	VersionMask = ~DriverMask,
	/// <summary>
	/// Extracts the driver type
	/// </summary>
	DriverMask = 0xffff_ffff_ffff_0000,

	OpenGL46 = OpenGL | 460,
	/// <summary>
	/// Vulkan 1.3 - the minimum we target (dynamic rendering, synchronization2).
	/// </summary>
	Vulkan13 = Vulkan | 13
}
public enum ShaderType
{
	Unknown,

	Vertex,
	Pixel,
	Geometry
}
public static class GraphicsAPIVersion_Exts
{
	public static GraphicsDriver GetDriver(this GraphicsDriver version) {
		return version & GraphicsDriver.DriverMask;
	}
	public static bool IsDriver(this GraphicsDriver version, GraphicsDriver driver) {
		return driver == (version & GraphicsDriver.DriverMask);
	}
	/// <summary>
	/// Generate a file extension, consiting of (DRIVER)(VERSION).(SHADERTYPEEXT).
	/// <br/>
	/// <br/>
	/// For OpenGL, this returns <code>gl(version).(vs vertex, fs pixel, gs geometry)</code> <i>(ex. gl460.fs)</i>
	/// <br/>
	/// For Vulkan, this returns <code>vk(version).(vs/fs/gs).spv</code> <i>(ex. vk13.fs.spv)</i> - precompiled SPIR-V.
	/// </summary>
	/// <param name="version"></param>
	/// <param name="type"></param>
	/// <returns></returns>
	/// <exception cref="NotImplementedException"></exception>
	public static ReadOnlySpan<char> Extension(this GraphicsDriver version, ShaderType type) {
		switch (version & GraphicsDriver.DriverMask) {
			case GraphicsDriver.OpenGL:
				return $"gl{(int)(version & ~GraphicsDriver.OpenGL)}.{type switch {
					ShaderType.Vertex => "vs",
					ShaderType.Pixel => "fs",
					ShaderType.Geometry => "gs",
					_ => throw new NotImplementedException("Please implement Extension for this OpenGL version")
				}}";
			case GraphicsDriver.Vulkan:
				return $"vk{(int)(version & ~GraphicsDriver.Vulkan)}.{type switch {
					ShaderType.Vertex => "vs",
					ShaderType.Pixel => "fs",
					ShaderType.Geometry => "gs",
					_ => throw new NotImplementedException("Please implement Extension for this Vulkan version")
				}}.spv";
			default:
				throw new NotImplementedException("Please implement Extension for this GraphicsAPI driver type");
		}
	}
}
public interface IShaderDevice
{
	IMesh CreateStaticMesh(VertexFormat format, ReadOnlySpan<char> textureGroup, IMaterial? material);
	void DestroyStaticMesh(IMesh mesh);
	bool IsDeactivated();
	bool IsUsingGraphics();
	int GetCurrentAdapter();
	void Present();
	void ReacquireResources();
	void ReleaseResources();
	int GetModeCount(int adapter);
	void GetModeInfo(int adapter, int mode, out ShaderDisplayMode info);
	/// <summary>
	/// Human-readable driver/API version string (ex. what GL_VERSION reports), for display in UI.
	/// </summary>
	ReadOnlySpan<char> GetDriverVersionString();
}
public struct ShaderDisplayMode
{
	public int Width;
	public int Height;
	public ImageFormat Format;
	public int RefreshRateNumerator;
	public int RefreshRateDenominator;

	public readonly double RefreshRate => (double)RefreshRateNumerator / RefreshRateDenominator;
}

public struct ShaderDeviceInfo
{
	public ShaderDisplayMode DisplayMode;
	public int BackBufferCount;
	public int AASamples;
	public int AAQuality;
	public GraphicsDriver Driver;
	public int WindowedSizeLimitWidth;
	public int WindowedSizeLimitHeight;

	public bool Windowed;
	public bool Borderless;
	public bool Resizing;
	public bool UseStencil;
	public bool LimitWindowedSize;
	public bool WaitForVSync;
	public bool ScaleToOutputResolution;
	public bool Progressive;
	public bool UsingMultipleWindows;
}
