#version 460

#include "common_vk13.glsl"

layout(location = 0) in vec2 vs_TexCoord;
layout(location = 1) in vec3 vs_WorldNormal;
layout(location = 2) in vec3 vs_WorldVertToEye;

layout(std140, set = 0, binding = 6) uniform source_ps_constants {
    vec4 ps_const[256];
};

// ps_const[0].rgb = $fogcolor, .a = $reflectamount
// ps_const[1].x   = $refractamount (used here as the strength of the normal perturbation)
const int PIXEL_SHADER_WATER_FOG = 0;
const int PIXEL_SHADER_WATER_PARAMS = 1;

layout(set = 1, binding = 1) uniform samplerCube envmap;
layout(set = 1, binding = 4) uniform sampler2D bumpmap;

layout(location = 0) out vec4 fragColor;

void main()
{
    vec3 fogColor = ps_const[PIXEL_SHADER_WATER_FOG].rgb;
    float reflectAmount = ps_const[PIXEL_SHADER_WATER_FOG].a;
    float bumpStrength = ps_const[PIXEL_SHADER_WATER_PARAMS].x;

    // Two scrolling samples of the normal map at different scales break up the tiling and give
    // the surface some motion without needing a time uniform: the offsets come from the surface
    // texcoords themselves, which vary across the water plane.
    vec3 bumpA = texture(bumpmap, vs_TexCoord * 1.0).xyz * 2.0 - 1.0;
    vec3 bumpB = texture(bumpmap, vs_TexCoord * 2.7 + vec2(0.37, 0.19)).xyz * 2.0 - 1.0;
    vec3 bump = normalize(bumpA + bumpB * 0.5);

    vec3 normal = normalize(vs_WorldNormal);
    // Perturb the surface normal sideways; the bump map is tangent space but water is a flat
    // plane, so its xy maps directly onto the world plane closely enough for ripples.
    normal = normalize(normal + bump * bumpStrength);

    vec3 vertToEye = normalize(vs_WorldVertToEye);

    // Schlick fresnel: looking straight down mostly shows the water body, grazing angles reflect.
    float NdotV = clamp(dot(normal, vertToEye), 0.0, 1.0);
    float fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);

    vec3 reflectVect = reflect(-vertToEye, normal);
    vec3 reflection = GammaToLinear(texture(envmap, reflectVect).rgb);

    vec3 waterBody = GammaToLinear(fogColor);
    vec3 linearColor = mix(waterBody, reflection, clamp(fresnel * reflectAmount, 0.0, 1.0));

    fragColor.rgb = LinearToGamma(linearColor);
    // More opaque at grazing angles, more see-through looking straight down.
    fragColor.a = clamp(0.55 + 0.45 * fresnel, 0.0, 1.0);
}
