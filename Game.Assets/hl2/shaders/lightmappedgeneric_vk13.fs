#version 460

#include "common_vk13.glsl"

layout(location = 0) in vec2 vs_TexCoord0;
layout(location = 1) in vec2 vs_TexCoord1;
layout(location = 2) in vec4 vs_Color;

layout(std140, set = 0, binding = 3) uniform source_pixel_sharedUBO {
    bool isAlphaTesting;
    int alphaTestFunc;
    float alphaTestRef;
};

layout(set = 1, binding = 0) uniform sampler2D basetexture;
layout(set = 1, binding = 3) uniform sampler2D lightmaptexture;

layout(location = 0) out vec4 fragColor;

void main()
{
    vec4 texelColor = texture(basetexture, vs_TexCoord0);
    vec4 lightmapColor = texture(lightmaptexture, vs_TexCoord1);
    if(isAlphaTesting){
        switch(alphaTestFunc){
            case 0: discard; break;
            case 1: if(texelColor.a >=  alphaTestRef){ discard; } break;
            case 2: if(texelColor.a != alphaTestRef){ discard; } break;
            case 3: if(texelColor.a > alphaTestRef){ discard; } break;
            case 4: if(texelColor.a <=  alphaTestRef){ discard; } break;
            case 5: if(texelColor.a == alphaTestRef){ discard; } break;
            case 6: if(texelColor.a < alphaTestRef){ discard; } break;
        }
    }

    vec4 vertexColor = vec4(1.0, 1.0, 1.0, 1.0);

    if((flags & VertexColor) != 0){
        vertexColor.r = vs_Color.r;
        vertexColor.g = vs_Color.g;
        vertexColor.b = vs_Color.b;
    }

    if((flags & VertexAlpha) != 0){
        vertexColor.a = vs_Color.a;
    }

    // Final product: texture color * vertex color if applicable
    fragColor.rgb = texelColor.rgb * vertexColor.rgb * lightmapColor.rgb * 2.2;
    fragColor.a = texelColor.a * vertexColor.a;
}
