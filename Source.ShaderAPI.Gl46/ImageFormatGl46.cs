using Source.Common.Bitmap;

namespace Source.ShaderAPI.Gl46;

/// <summary>
/// Translates the driver-neutral <see cref="ImageFormat"/> enum into GL format enums.
/// Each backend owns its own translation table; this is the GL 4.6 one.
/// </summary>
public static class ImageFormatGl46
{
	public static int GetGLImageUploadFormat(ImageFormat format) => format switch {
		ImageFormat.RGBA8888 => (int)GL_RGBA,
		ImageFormat.RGB888 => (int)GL_RGB,
		ImageFormat.BGR888 => (int)GL_BGR,
		ImageFormat.RGB888_Bluescreen => (int)GL_RGB,
		ImageFormat.BGR888_Bluescreen => (int)GL_BGR, // TODO: what does bluescreen mean here
		ImageFormat.ARGB8888 => (int)GL_BGRA,
		ImageFormat.BGRA8888 => (int)GL_BGRA,
		ImageFormat.BGRX8888 => (int)GL_BGRA,
		ImageFormat.RGBA16161616 => (int)GL_RGBA,
		ImageFormat.RGBA16161616F => (int)GL_RGBA,
		ImageFormat.R32F => (int)GL_RED,
		ImageFormat.RGB323232F => (int)GL_RGB,
		ImageFormat.RGBA32323232F => (int)GL_RGBA,
		ImageFormat.I8 => (int)GL_RED,
	};
	public static int GetGLImageInternalFormat(ImageFormat format) => format switch {
		// Uncompressed color formats
		ImageFormat.RGBA8888 => (int)GL_RGBA8,
		ImageFormat.ABGR8888 => 0x8000, // Supposedly GL_ABGR_EXT?
		ImageFormat.RGB888 => (int)GL_RGB8,
		ImageFormat.BGR888 => (int)GL_RGB8,
		ImageFormat.RGB565 => (int)GL_RGB565,
		// ImageFormat.BGR565 => Gl46.GL_BGR565, No GL_BGR565 - :(
		ImageFormat.I8 => (int)GL_R8,
		ImageFormat.IA88 => (int)GL_RG8,
		ImageFormat.A8 => (int)GL_RGBA8,
		ImageFormat.RGB888_Bluescreen => (int)GL_RGB8,
		ImageFormat.BGR888_Bluescreen => (int)GL_RGB8, // TODO: what does bluescreen mean here
		ImageFormat.ARGB8888 => (int)GL_RGBA8,
		ImageFormat.BGRA8888 => (int)GL_RGBA8,
		ImageFormat.BGRX8888 => (int)GL_RGBA8,
		// ImageFormat.BGRX5551 => GL_RGBA8,
		// ImageFormat.BGRA4444 => GL_RGBA4,
		// ImageFormat.BGRA5551 => GL_RGB5_A1,
		ImageFormat.RGBA16161616 => (int)GL_RGBA16,
		ImageFormat.RGBA16161616F => (int)GL_RGBA16F,
		ImageFormat.R32F => (int)GL_R32F,
		ImageFormat.RGB323232F => (int)GL_RGB32F,
		ImageFormat.RGBA32323232F => (int)GL_RGBA32F,

		// Compressed DXT formats
		ImageFormat.DXT1 => (int)GL_COMPRESSED_RGBA_S3TC_DXT1_EXT,
		ImageFormat.DXT3 => (int)GL_COMPRESSED_RGBA_S3TC_DXT3_EXT,
		ImageFormat.DXT5 => (int)GL_COMPRESSED_RGBA_S3TC_DXT5_EXT,
		ImageFormat.DXT1_OneBitAlpha => (int)GL_COMPRESSED_RGBA_S3TC_DXT1_EXT,
		ImageFormat.DXT1_Runtime => (int)GL_COMPRESSED_RGBA_S3TC_DXT1_EXT,
		ImageFormat.DXT5_Runtime => (int)GL_COMPRESSED_RGBA_S3TC_DXT5_EXT,

		// ATI compressed normal maps
		ImageFormat.ATI2N => (int)GL_COMPRESSED_RG_RGTC2,
		ImageFormat.ATI1N => (int)GL_COMPRESSED_RED_RGTC1,

		// Depth-stencil
		ImageFormat.NV_DST16 => (int)GL_DEPTH_COMPONENT16,
		ImageFormat.NV_DST24 => (int)GL_DEPTH_COMPONENT24,
		ImageFormat.NV_IntZ => (int)GL_DEPTH_COMPONENT24,
		ImageFormat.NV_RawZ => (int)GL_DEPTH_COMPONENT32,
		ImageFormat.ATI_DST16 => (int)GL_DEPTH_COMPONENT16,
		ImageFormat.ATI_DST24 => (int)GL_DEPTH_COMPONENT24,
		ImageFormat.NV_NULL => 0,      // dummy, no storage

		_ => throw new NotSupportedException($"GetGLImageFormat: unexpected format '{format}'"),
	};
}
