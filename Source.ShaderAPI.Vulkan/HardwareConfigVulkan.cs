using Source.Common.Bitmap;
using Source.Common.MaterialSystem;

namespace Source.ShaderAPI.Vulkan;

/// <summary>
/// Hardware caps for the Vulkan backend. Fixed conservative values for now; wire these up to
/// VkPhysicalDeviceProperties/Limits once the device is queried at startup.
/// </summary>
public class HardwareConfigVulkan : IMaterialSystemHardwareConfig
{
	public bool SupportsShadowDepthTexturesCap = true;
	public ImageFormat ShadowDepthTextureFormat = ImageFormat.NV_DST24;
	public ImageFormat NullTextureFormat = ImageFormat.NV_NULL;

	public bool ActuallySupportsPixelShaders_2_b() => true;
	public bool CanDoSRGBReadFromRTs() => true;
	public bool FakeSRGBWrite() => false;
	public int GetDXSupportLevel() => 0;
	public int GetFrameBufferColorDepth() => 32;
	public HDRType GetHardwareHDRType() => HDRType.None;
	public bool GetHDREnabled() => false;
	public HDRType GetHDRType() => HDRType.None;
	public int GetMaxDXSupportLevel() => 0;
	public int GetMaxVertexTextureDimension() => 16384;
	public int GetSamplerCount() => 16;
	public ReadOnlySpan<char> GetShaderDLLName() => "vulkan";
	public int GetShadowFilterMode() => 0;
	public int GetTextureStageCount() => GetSamplerCount();
	public int GetVertexTextureCount() => 16;
	public bool HasDestAlphaBuffer() => true;
	public bool HasFastVertexTextures() => false;
	public bool HasProjectedBumpEnv() => false;
	public bool HasSetDeviceGammaRamp() => false;
	public bool HasStencilBuffer() => true;
	public bool IsAAEnabled() => false;
	public int MaxBlendMatrices() => 4;
	public int MaxBlendMatrixIndices() => 4;
	public int MaxHWMorphBatchCount() => 0;
	public int MaximumAnisotropicLevel() => 16;
	public int MaxNumLights() => 4;
	public int MaxTextureAspectRatio() => int.MaxValue;
	public int MaxTextureDepth() => 2048;
	public int MaxTextureHeight() => 16384;
	public int MaxTextureWidth() => 16384;
	public int MaxUserClipPlanes() => 8;
	public int MaxVertexShaderBlendMatrices() => 4;
	public int MaxViewports() => 16;
	public bool NeedsAAClamp() => false;
	public bool NeedsATICentroidHack() => false;
	public int NeedsShaderSRGBConversion() => 0;
	public int NumPixelShaderConstants() => 256;
	public int NumVertexShaderConstants() => 256;
	public void OverrideStreamOffsetSupport(bool bOverrideEnabled, bool bEnableSupport) { }
	public bool PreferDynamicTextures() => false;
	public bool PreferReducedFillrate() => false;
	public bool ReadPixelsFromFrontBuffer() => false;
	public void SetHDREnabled(bool bEnable) { }
	public bool SpecifiesFogColorInLinearSpace() => false;
	public int StencilBufferBits() => 8;
	public bool SupportsBorderColor() => true;
	public bool SupportsColorOnSecondStream() => true;
	public bool SupportsCompressedTextures() => true;
	public VertexCompressionType SupportsCompressedVertices() => VertexCompressionType.None;
	public bool SupportsCubeMaps() => true;
	public bool SupportsFetch4() => false;
	public bool SupportsGLMixedSizeTargets() => true;
	public bool SupportsHardwareLighting() => true;
	public bool SupportsHDR() => false;
	public bool SupportsHDRMode(HDRType nHDRMode) => nHDRMode == HDRType.None;
	public bool SupportsMipmappedCubemaps() => false;
	public bool SupportsNonPow2Textures() => true;
	public bool SupportsOverbright() => true;
	public bool SupportsPixelShaders_1_4() => true;
	public bool SupportsPixelShaders_2_0() => true;
	public bool SupportsPixelShaders_2_b() => true;
	public bool SupportsShaderModel_3_0() => true;
	public bool SupportsSpheremapping() => true;
	public bool SupportsSRGB() => true;
	public bool SupportsStaticControlFlow() => true;
	public bool SupportsStaticPlusDynamicLighting() => true;
	public bool SupportsStreamOffset() => true;
	public bool SupportsVertexAndPixelShaders() => true;
	public bool SupportsVertexShaders_2_0() => true;
	public nint TextureMemorySize() => 512 * 1024 * 1024;
	public bool UseFastClipping() => false;
	public bool UsesSRGBCorrectBlending() => true;
}
