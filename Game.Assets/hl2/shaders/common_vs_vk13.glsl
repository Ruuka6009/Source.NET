#ifndef COMMON_VK13_VS
#define COMMON_VK13_VS

#include "common_vk13.glsl"

// Port of common_gl460.vs. The maths is unchanged; the only difference is that
// the light count and half-lambert switch arrive as combo bits rather than as
// preprocessor defines, so the loops stay dynamic.

struct LightInfo
{
    vec4 color;
    vec4 dir;
    vec4 pos;
    vec4 spotParams;
    vec4 atten;
};

// Four lights x 5 constants each = 20 constants
LightInfo cLightInfo[4];

void InitLightInfo()
{
    for (int i = 0; i < 4; ++i)
    {
        cLightInfo[i].color      = vs_const[VERTEX_SHADER_LIGHT_INFO + i * 5 + 0];
        cLightInfo[i].dir        = vs_const[VERTEX_SHADER_LIGHT_INFO + i * 5 + 1];
        cLightInfo[i].pos        = vs_const[VERTEX_SHADER_LIGHT_INFO + i * 5 + 2];
        cLightInfo[i].spotParams = vs_const[VERTEX_SHADER_LIGHT_INFO + i * 5 + 3];
        cLightInfo[i].atten      = vs_const[VERTEX_SHADER_LIGHT_INFO + i * 5 + 4];
    }
}

vec3 AmbientLight(vec3 worldNormal)
{
    vec3 nSquared = worldNormal * worldNormal;
    ivec3 isNegative = ivec3(lessThan(worldNormal, vec3(0.0)));
    vec3 color;
    color  = nSquared.x * vs_const[VERTEX_SHADER_AMBIENT_LIGHT + isNegative.x].rgb;
    color += nSquared.y * vs_const[VERTEX_SHADER_AMBIENT_LIGHT + 2 + isNegative.y].rgb;
    color += nSquared.z * vs_const[VERTEX_SHADER_AMBIENT_LIGHT + 4 + isNegative.z].rgb;
    return color;
}

float VertexAttenInternal(vec3 worldPos, int lightNum)
{
    vec3 lightDir = cLightInfo[lightNum].pos.xyz - worldPos;
    float lightDistSquared = dot(lightDir, lightDir);
    float ooLightDist = inversesqrt(lightDistSquared);
    lightDir *= ooLightDist;

    vec3 vDist = vec3(1.0, lightDistSquared * ooLightDist, lightDistSquared);
    float flDistanceAtten = 1.0 / dot(cLightInfo[lightNum].atten.xyz, vDist);

    float flCosTheta = dot(cLightInfo[lightNum].dir.xyz, -lightDir);
    float flSpotAtten = (flCosTheta - cLightInfo[lightNum].spotParams.z) * cLightInfo[lightNum].spotParams.w;
    flSpotAtten = max(0.0001, flSpotAtten);
    flSpotAtten = pow(flSpotAtten, cLightInfo[lightNum].spotParams.x);
    flSpotAtten = clamp(flSpotAtten, 0.0, 1.0);

    // Select between point and spot, then between that and directional (no attenuation)
    float flAtten = mix(flDistanceAtten, flDistanceAtten * flSpotAtten, cLightInfo[lightNum].dir.w);
    return mix(flAtten, 1.0, cLightInfo[lightNum].color.w);
}

float CosineTermInternal(vec3 worldPos, vec3 worldNormal, int lightNum, bool bHalfLambert)
{
    vec3 lightDir = normalize(cLightInfo[lightNum].pos.xyz - worldPos);

    vec4 dirInv = -cLightInfo[lightNum].dir;
    vec4 color = cLightInfo[lightNum].color;

    // Select the above direction or the one in the structure, based upon light type
    lightDir = mix(lightDir, dirInv.xyz, color.www);

    float NDotL = dot(worldNormal, lightDir);

    if (!bHalfLambert)
    {
        NDotL = max(0.0, NDotL);
    }
    else
    {
        NDotL = NDotL * 0.5 + 0.5;
        NDotL = NDotL * NDotL;
    }
    return NDotL;
}

vec3 DoLightInternal(vec3 worldPos, vec3 worldNormal, int lightNum, bool bHalfLambert)
{
    return cLightInfo[lightNum].color.xyz *
        CosineTermInternal(worldPos, worldNormal, lightNum, bHalfLambert) *
        VertexAttenInternal(worldPos, lightNum);
}

vec3 DoLightingUnrolled(vec3 worldPos, vec3 worldNormal,
                        vec3 staticLightingColor, bool bStaticLight,
                        bool bDynamicLight, bool bHalfLambert, int nNumLights)
{
    vec3 linearColor = vec3(0.0, 0.0, 0.0);

    if (bStaticLight)
        linearColor += GammaToLinear(staticLightingColor * cOverbright);

    if (bDynamicLight)
    {
        if (nNumLights >= 1)
            linearColor += DoLightInternal(worldPos, worldNormal, 0, bHalfLambert);
        if (nNumLights >= 2)
            linearColor += DoLightInternal(worldPos, worldNormal, 1, bHalfLambert);
        if (nNumLights >= 3)
            linearColor += DoLightInternal(worldPos, worldNormal, 2, bHalfLambert);
        if (nNumLights >= 4)
            linearColor += DoLightInternal(worldPos, worldNormal, 3, bHalfLambert);

        linearColor += AmbientLight(worldNormal); // already remapped
    }

    return linearColor;
}

#endif // COMMON_VK13_VS
