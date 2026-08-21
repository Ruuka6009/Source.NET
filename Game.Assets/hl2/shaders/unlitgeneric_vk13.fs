#version 460

#include "common_vk13.glsl"

layout(location = 0) in vec2 vs_TexCoord;
layout(location = 1) in vec4 vs_Color;

layout(std140, set = 0, binding = 3) uniform source_pixel_sharedUBO {
    bool isAlphaTesting;
    int alphaTestFunc;
    float alphaTestRef;
};

layout(set = 1, binding = 0) uniform sampler2D basetexture;

layout(location = 0) out vec4 fragColor;

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

    fragColor = texelColor * vs_Color;
}
