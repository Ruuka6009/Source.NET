#version 460

in vec2 vs_TexCoord;
in vec3 vs_WorldNormal;
in vec3 vs_WorldVertToEye;

layout(std140, binding = 6) uniform source_ps_constants {
    vec4 ps_const[256];
};

// ps_const[0].rgb = $fogcolor, .a = $reflectamount
// ps_const[1].x   = $refractamount (used here as the strength of the normal perturbation)
const int PIXEL_SHADER_WATER_FOG = 0;
const int PIXEL_SHADER_WATER_PARAMS = 1;

uniform samplerCube envmap;
uniform sampler2D bumpmap;

out vec4 fragColor;

#include "common_gl460.fs"

void main()
{
    vec3 fogColor = ps_const[PIXEL_SHADER_WATER_FOG].rgb;
    float reflectAmount = ps_const[PIXEL_SHADER_WATER_FOG].a;
    float bumpStrength = ps_const[PIXEL_SHADER_WATER_PARAMS].x;

    vec3 bumpA = texture(bumpmap, vs_TexCoord * 1.0).xyz * 2.0 - 1.0;
    vec3 bumpB = texture(bumpmap, vs_TexCoord * 2.7 + vec2(0.37, 0.19)).xyz * 2.0 - 1.0;
    vec3 bump = normalize(bumpA + bumpB * 0.5);

    vec3 normal = normalize(vs_WorldNormal);
    normal = normalize(normal + bump * bumpStrength);

    vec3 vertToEye = normalize(vs_WorldVertToEye);

    float NdotV = clamp(dot(normal, vertToEye), 0.0, 1.0);
    float fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);

    vec3 reflectVect = reflect(-vertToEye, normal);
    vec3 reflection = GammaToLinear(texture(envmap, reflectVect).rgb);

    vec3 waterBody = GammaToLinear(fogColor);
    vec3 linearColor = mix(waterBody, reflection, clamp(fresnel * reflectAmount, 0.0, 1.0));

    fragColor.rgb = LinearToGamma(linearColor);
    fragColor.a = clamp(0.55 + 0.45 * fresnel, 0.0, 1.0);
}
