#version 460

#include "common_vk13.glsl"

layout(location = 0) in vec3 v_Position;
layout(location = 1) in vec3 v_Normal;
layout(location = 10) in vec2 v_TexCoord;

layout(std140, set = 0, binding = 0) uniform source_matrices {
    mat4 viewMatrix;
    mat4 projectionMatrix;
    mat4 modelMatrix;
};

layout(std140, set = 0, binding = 5) uniform source_vs_constants {
    vec4 vs_const[256];
};

const int VERTEX_SHADER_CAMERA_POS = 2;
const int VERTEX_SHADER_BASE_TEXCOORD_TRANSFORM = 48; // SHADER_SPECIFIC_CONST_0

layout(location = 0) out vec2 vs_TexCoord;
layout(location = 1) out vec3 vs_WorldNormal;
layout(location = 2) out vec3 vs_WorldVertToEye;
layout(location = 3) out vec3 vs_WorldPos;

void main()
{
    vec4 worldPos = modelMatrix * vec4(v_Position, 1.0);
    gl_Position = projectionMatrix * viewMatrix * worldPos;

    vec4 texCoordInput = vec4(v_TexCoord, 0.0, 1.0);
    vs_TexCoord.x = dot(texCoordInput, vs_const[VERTEX_SHADER_BASE_TEXCOORD_TRANSFORM + 0]);
    vs_TexCoord.y = dot(texCoordInput, vs_const[VERTEX_SHADER_BASE_TEXCOORD_TRANSFORM + 1]);

    vs_WorldNormal = mat3(modelMatrix) * v_Normal;
    vs_WorldVertToEye = vs_const[VERTEX_SHADER_CAMERA_POS].xyz - worldPos.xyz;
    vs_WorldPos = worldPos.xyz;
}
