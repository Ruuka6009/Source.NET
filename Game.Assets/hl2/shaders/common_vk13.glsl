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
//   push constants = per-draw data ("flags" replaces GL's loose uniform int,
//   "combos" replaces GL's preprocessor combo variants).
// GL's negative-Y clip difference is handled with a negative viewport at
// pipeline level, not in the shaders.
// ---------------------------------------------------------------------------

layout(push_constant) uniform source_push {
    int flags;
    int combos;
} pc;
#define flags pc.flags

const int VertexColor = 16;
const int VertexAlpha = 32;

// Combo bits. The GL backend compiles one shader variant per combination; here
// a single variant branches on these instead. The backend packs the same bits
// from the combo names the shader classes set, so the layout must match
// VulkanShaderCombos.ComboBits on the C# side.
#define COMBO_VERTEXCOLOR               (1 << 0)
#define COMBO_CUBEMAP                   (1 << 1)
#define COMBO_HALFLAMBERT               (1 << 2)
#define COMBO_SELFILLUM                 (1 << 3)
#define COMBO_ENVMAPMASK                (1 << 4)
#define COMBO_BASEALPHAENVMAPMASK       (1 << 5)
#define COMBO_NORMALMAPALPHAENVMAPMASK  (1 << 6)
#define COMBO_DYNAMIC_LIGHT             (1 << 7)
#define COMBO_STATIC_LIGHT              (1 << 8)
#define COMBO_NUM_LIGHTS_SHIFT          9
#define COMBO_NUM_LIGHTS_MASK           7

bool Combo(int bit) { return (pc.combos & bit) != 0; }
int ComboNumLights() { return (pc.combos >> COMBO_NUM_LIGHTS_SHIFT) & COMBO_NUM_LIGHTS_MASK; }

vec3 LinearToGamma(vec3 f3linear)
{
    return pow(f3linear, vec3(1.0 / 2.2));
}

vec3 GammaToLinear(vec3 gamma)
{
    return pow(gamma, vec3(2.2));
}

#endif // COMMON_VK13_GLSL
