using Silk.NET.Vulkan;

using Source.Bitmap;
using Source.Common.Bitmap;

using VkFormat = Silk.NET.Vulkan.Format;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Translates the driver-neutral <see cref="ImageFormat"/> enum into Vulkan formats - the sibling
/// of <c>ImageFormatGl46</c>. Three-byte and packed-16-bit layouts are not reliably sampleable in
/// Vulkan, so this also owns the CPU-side conversion to one that is: <see cref="GetStorageFormat"/>
/// says what the pixels are stored as, and anything that differs from its source format goes
/// through <see cref="Convert"/> on upload.
/// </summary>
public static class ImageFormatVulkan
{
	/// <summary>The format the GPU copy is kept in - equals the input unless a conversion is required.</summary>
	public static ImageFormat GetStorageFormat(ImageFormat format) => format switch {
		// Layouts not reliably supported for sampled images become plain RGBA8888.
		ImageFormat.RGB888 or ImageFormat.BGR888 or
		ImageFormat.RGB888_Bluescreen or ImageFormat.BGR888_Bluescreen or
		ImageFormat.ARGB8888 or ImageFormat.ABGR8888 or ImageFormat.BGRX8888 or
		ImageFormat.RGB565 or ImageFormat.BGR565 or
		ImageFormat.BGRX5551 or ImageFormat.BGRA5551 or ImageFormat.BGRA4444 or
		ImageFormat.A8 => ImageFormat.RGBA8888,

		_ => format
	};

	public static bool RequiresConversion(ImageFormat format) => GetStorageFormat(format) != format;

	/// <summary>VkFormat for a storage format (i.e. the output of <see cref="GetStorageFormat"/>).</summary>
	public static VkFormat ToVkFormat(ImageFormat format, bool srgb = false) => format switch {
		ImageFormat.RGBA8888 => srgb ? VkFormat.R8G8B8A8Srgb : VkFormat.R8G8B8A8Unorm,
		ImageFormat.BGRA8888 => srgb ? VkFormat.B8G8R8A8Srgb : VkFormat.B8G8R8A8Unorm,
		ImageFormat.UVWQ8888 or ImageFormat.UVLX8888 => VkFormat.R8G8B8A8Unorm,

		// GL maps I8 to GL_R8 (samples as r,0,0,1); keep the same behaviour.
		ImageFormat.I8 => VkFormat.R8Unorm,
		ImageFormat.IA88 or ImageFormat.UV88 => VkFormat.R8G8Unorm,

		ImageFormat.RGBA16161616 => VkFormat.R16G16B16A16Unorm,
		ImageFormat.RGBA16161616F => VkFormat.R16G16B16A16Sfloat,
		ImageFormat.R32F => VkFormat.R32Sfloat,
		ImageFormat.RGB323232F => VkFormat.R32G32B32Sfloat,
		ImageFormat.RGBA32323232F => VkFormat.R32G32B32A32Sfloat,

		// Block-compressed: DXT -> BC1/2/3, ATI1N/2N -> BC4/BC5.
		ImageFormat.DXT1 or ImageFormat.DXT1_OneBitAlpha or ImageFormat.DXT1_Runtime
			=> srgb ? VkFormat.BC1RgbaSrgbBlock : VkFormat.BC1RgbaUnormBlock,
		ImageFormat.DXT3 => srgb ? VkFormat.BC2SrgbBlock : VkFormat.BC2UnormBlock,
		ImageFormat.DXT5 or ImageFormat.DXT5_Runtime => srgb ? VkFormat.BC3SrgbBlock : VkFormat.BC3UnormBlock,
		ImageFormat.ATI1N => VkFormat.BC4UnormBlock,
		ImageFormat.ATI2N => VkFormat.BC5UnormBlock,

		// Depth / shadow-map formats
		ImageFormat.NV_DST16 or ImageFormat.ATI_DST16 => VkFormat.D16Unorm,
		ImageFormat.NV_DST24 or ImageFormat.ATI_DST24 => VkFormat.D24UnormS8Uint,
		ImageFormat.NV_IntZ or ImageFormat.NV_RawZ => VkFormat.D32Sfloat,

		_ => VkFormat.Undefined
	};

	public static bool IsDepthFormat(ImageFormat format) => format switch {
		ImageFormat.NV_DST16 or ImageFormat.NV_DST24 or ImageFormat.NV_IntZ or
		ImageFormat.NV_RawZ or ImageFormat.ATI_DST16 or ImageFormat.ATI_DST24 => true,
		_ => false
	};

	/// <summary>Bytes one row of <paramref name="width"/> pixels occupies (a row of blocks for BC formats).</summary>
	public static int RowPitch(ImageFormat storageFormat, int width) {
		if (storageFormat.IsCompressed()) {
			int blocksWide = Math.Max(1, (width + 3) / 4);
			int blockBytes = storageFormat switch {
				ImageFormat.DXT1 or ImageFormat.DXT1_OneBitAlpha or ImageFormat.DXT1_Runtime or ImageFormat.ATI1N => 8,
				_ => 16
			};
			return blocksWide * blockBytes;
		}
		return width * storageFormat.SizeInBytes();
	}

	/// <summary>
	/// Converts pixels from <paramref name="srcFormat"/> into its storage format. Only called
	/// where <see cref="RequiresConversion"/> is true, all of which target RGBA8888.
	/// </summary>
	public static void Convert(ImageFormat srcFormat, ReadOnlySpan<byte> src, Span<byte> dst) {
		int pixels = Math.Min(src.Length / Math.Max(srcFormat.SizeInBytes(), 1), dst.Length / 4);

		switch (srcFormat) {
			case ImageFormat.RGB888:
			case ImageFormat.RGB888_Bluescreen:
				for (int i = 0; i < pixels; i++) {
					dst[i * 4 + 0] = src[i * 3 + 0];
					dst[i * 4 + 1] = src[i * 3 + 1];
					dst[i * 4 + 2] = src[i * 3 + 2];
					dst[i * 4 + 3] = 255;
				}
				break;

			case ImageFormat.BGR888:
			case ImageFormat.BGR888_Bluescreen:
				for (int i = 0; i < pixels; i++) {
					dst[i * 4 + 0] = src[i * 3 + 2];
					dst[i * 4 + 1] = src[i * 3 + 1];
					dst[i * 4 + 2] = src[i * 3 + 0];
					dst[i * 4 + 3] = 255;
				}
				break;

			case ImageFormat.ARGB8888:
				for (int i = 0; i < pixels; i++) {
					dst[i * 4 + 0] = src[i * 4 + 1];
					dst[i * 4 + 1] = src[i * 4 + 2];
					dst[i * 4 + 2] = src[i * 4 + 3];
					dst[i * 4 + 3] = src[i * 4 + 0];
				}
				break;

			case ImageFormat.ABGR8888:
				for (int i = 0; i < pixels; i++) {
					dst[i * 4 + 0] = src[i * 4 + 3];
					dst[i * 4 + 1] = src[i * 4 + 2];
					dst[i * 4 + 2] = src[i * 4 + 1];
					dst[i * 4 + 3] = src[i * 4 + 0];
				}
				break;

			case ImageFormat.BGRX8888:
				// X carries no alpha; force opaque rather than sampling the pad byte.
				for (int i = 0; i < pixels; i++) {
					dst[i * 4 + 0] = src[i * 4 + 2];
					dst[i * 4 + 1] = src[i * 4 + 1];
					dst[i * 4 + 2] = src[i * 4 + 0];
					dst[i * 4 + 3] = 255;
				}
				break;

			case ImageFormat.A8:
				for (int i = 0; i < pixels; i++) {
					dst[i * 4 + 0] = 255;
					dst[i * 4 + 1] = 255;
					dst[i * 4 + 2] = 255;
					dst[i * 4 + 3] = src[i];
				}
				break;

			case ImageFormat.RGB565:
			case ImageFormat.BGR565:
				for (int i = 0; i < pixels; i++) {
					ushort p = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8));
					byte r = Expand5((p >> 11) & 0x1F);
					byte g = Expand6((p >> 5) & 0x3F);
					byte b = Expand5(p & 0x1F);
					bool bgr = srcFormat == ImageFormat.BGR565;
					dst[i * 4 + 0] = bgr ? b : r;
					dst[i * 4 + 1] = g;
					dst[i * 4 + 2] = bgr ? r : b;
					dst[i * 4 + 3] = 255;
				}
				break;

			case ImageFormat.BGRX5551:
			case ImageFormat.BGRA5551:
				for (int i = 0; i < pixels; i++) {
					ushort p = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8));
					dst[i * 4 + 0] = Expand5((p >> 10) & 0x1F);
					dst[i * 4 + 1] = Expand5((p >> 5) & 0x1F);
					dst[i * 4 + 2] = Expand5(p & 0x1F);
					dst[i * 4 + 3] = srcFormat == ImageFormat.BGRA5551 ? ((p & 0x8000) != 0 ? (byte)255 : (byte)0) : (byte)255;
				}
				break;

			case ImageFormat.BGRA4444:
				for (int i = 0; i < pixels; i++) {
					ushort p = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8));
					dst[i * 4 + 0] = Expand4((p >> 8) & 0xF);
					dst[i * 4 + 1] = Expand4((p >> 4) & 0xF);
					dst[i * 4 + 2] = Expand4(p & 0xF);
					dst[i * 4 + 3] = Expand4((p >> 12) & 0xF);
				}
				break;

			default:
				Warning($"Vulkan: no conversion for image format {srcFormat}; uploading opaque magenta\n");
				for (int i = 0; i < dst.Length / 4; i++) {
					dst[i * 4 + 0] = 255;
					dst[i * 4 + 1] = 0;
					dst[i * 4 + 2] = 255;
					dst[i * 4 + 3] = 255;
				}
				break;
		}
	}

	static byte Expand4(int v) => (byte)((v << 4) | v);
	static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
	static byte Expand6(int v) => (byte)((v << 2) | (v >> 4));
}
