#ifndef COMMON_VK13_GLSL
#define COMMON_VK13_GLSL

// ---------------------------------------------------------------------------
// Vulkan binding convention (see VULKAN_TODO.md):
//   set 0 = UBOs, same binding numbers as the GL backend:
//     binding 0: source_matrices        (view/projection/model)
//     binding 2: source_base_vertex     (numBones)
//     binding 3: source_pixel_sharedUBO (alpha test state)
//     binding 4: source_bone_matrices
//     binding 5: source_vs_constants    (vs_const[256])
//     binding 6: source_ps_constants    (ps_const[256])
//   set 1 = material textures:
//     binding 0: basetexture      binding 1: envmap        binding 2: envmapmask
//     binding 3: lightmaptexture  binding 4: bumpmap       binding 5: basetexture2
//   push constants = per-draw data ("flags" replaces GL's loose uniform int).
// GL's negative-Y clip difference is handled with a negative viewport at
// pipeline level, not in the shaders.
// ---------------------------------------------------------------------------

layout(push_constant) uniform source_push {
    int flags;
} pc;
#define flags pc.flags

const int VertexColor = 16;
const int VertexAlpha = 32;

vec3 LinearToGamma(vec3 f3linear)
{
    return pow(f3linear, vec3(1.0 / 2.2));
}

vec3 GammaToLinear(vec3 gamma)
{
    return pow(gamma, vec3(2.2));
}

#endif // COMMON_VK13_GLSL
