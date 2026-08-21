#version 460

//  STATIC: "CUBEMAP"                   "0..1"
//  STATIC: "ENVMAPMASK"                "0..1"
//  STATIC: "BASEALPHAENVMAPMASK"       "0..1"
//  STATIC: "NORMALMAPALPHAENVMAPMASK"  "0..1"
//  STATIC: "SELFILLUM"                 "0..1"
//  STATIC: "VERTEXCOLOR"               "0..1"

layout(location = 0) in vec2 vs_TexCoord;
layout(location = 1) in vec4 vs_Color;
layout(location = 2) in vec4 vs_VertexColor;
layout(location = 3) in vec3 vs_WorldNormal;
layout(location = 4) in vec3 vs_WorldVertToEye;

layout(std140, set = 0, binding = 3) uniform source_pixel_sharedUBO {
    bool isAlphaTesting;
    int alphaTestFunc;
    float alphaTestRef;
};

layout(std140, set = 0, binding = 6) uniform source_ps_constants {
    vec4 ps_const[256];
};

const int PIXEL_SHADER_SELFILLUM_TINT = 1;
const int PIXEL_SHADER_ENVMAP_TINT = 2;
const int PIXEL_SHADER_MODULATION = 3;
const int PIXEL_SHADER_ENVMAP_CONTRAST = 4;
const int PIXEL_SHADER_ENVMAP_SATURATION = 5;

layout(set = 1, binding = 0) uniform sampler2D basetexture;
layout(set = 1, binding = 1) uniform samplerCube envmap;
layout(set = 1, binding = 2) uniform sampler2D envmapmask;
layout(set = 1, binding = 4) uniform sampler2D bumpmap;

layout(location = 0) out vec4 fragColor;

#include "common_vk13.glsl"

void main()
{
    vec4 texelColor = texture(basetexture, vs_TexCoord);
    if(isAlphaTesting){
        switch(alphaTestFunc){
            case 1: if(texelColor.a >=  alphaTestRef){ discard; } break;
            case 2: if(texelColor.a != alphaTestRef){ discard; } break;
            case 3: if(texelColor.a > alphaTestRef){ discard; } break;
            case 4: if(texelColor.a <=  alphaTestRef){ discard; } break;
            case 5: if(texelColor.a == alphaTestRef){ discard; } break;
            case 6: if(texelColor.a < alphaTestRef){ discard; } break;
            case 7: discard; break;
        }
    }

    vec3 albedo = GammaToLinear(texelColor.rgb) * ps_const[PIXEL_SHADER_MODULATION].rgb;

    if (Combo(COMBO_VERTEXCOLOR))
        albedo *= GammaToLinear(vs_VertexColor.rgb);

    vec3 linearColor = albedo * vs_Color.rgb;

    if (Combo(COMBO_SELFILLUM)) {
        vec3 selfIllumComponent = albedo * ps_const[PIXEL_SHADER_SELFILLUM_TINT].rgb;
        linearColor = mix(linearColor, selfIllumComponent, texelColor.a);
    }

    if (Combo(COMBO_CUBEMAP)) {
        vec3 specularFactor = vec3(1.0);
        if (Combo(COMBO_ENVMAPMASK))
            specularFactor *= texture(envmapmask, vs_TexCoord).rgb;
        if (Combo(COMBO_BASEALPHAENVMAPMASK))
            specularFactor *= 1.0 - texelColor.a;
        if (Combo(COMBO_NORMALMAPALPHAENVMAPMASK))
            specularFactor *= texture(bumpmap, vs_TexCoord).a;

        vec3 reflectVect = 2.0 * vs_WorldNormal * dot(vs_WorldNormal, vs_WorldVertToEye)
                         - vs_WorldVertToEye * dot(vs_WorldNormal, vs_WorldNormal);
        vec3 specularLighting = GammaToLinear(texture(envmap, reflectVect).rgb);
        specularLighting *= specularFactor;
        specularLighting *= ps_const[PIXEL_SHADER_ENVMAP_TINT].rgb;
        vec3 specularLightingSquared = specularLighting * specularLighting;
        specularLighting = mix(specularLighting, specularLightingSquared, ps_const[PIXEL_SHADER_ENVMAP_CONTRAST].rgb);
        vec3 greyScale = vec3(dot(specularLighting, vec3(0.299, 0.587, 0.114)));
        specularLighting = mix(greyScale, specularLighting, ps_const[PIXEL_SHADER_ENVMAP_SATURATION].rgb);
        linearColor += specularLighting;
    }

    fragColor.rgb = LinearToGamma(linearColor);
    fragColor.a = texelColor.a * ps_const[PIXEL_SHADER_MODULATION].a;
}
