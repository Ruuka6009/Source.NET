#version 460

// Combos are resolved at runtime from push constants rather than compiled into
// variants, so these declare what the shader class may set - not preprocessor
// defines. The backend uses the ranges to decode the packed index it is handed.
//  STATIC:  "VERTEXCOLOR"     "0..1"
//  STATIC:  "CUBEMAP"         "0..1"
//  STATIC:  "HALFLAMBERT"     "0..1"
//  DYNAMIC: "DYNAMIC_LIGHT"   "0..1"
//  DYNAMIC: "STATIC_LIGHT"    "0..1"
//  DYNAMIC: "NUM_LIGHTS"      "0..4"

layout(location = 0) in vec3 v_Position;
layout(location = 1) in vec3 v_Normal;
layout(location = 2) in vec4 v_Color;
layout(location = 3) in vec4 v_Specular;
layout(location = 7) in uvec4 v_BoneIndex;
layout(location = 8) in vec2 v_BoneWeights;
layout(location = 10) in vec2 v_TexCoord;

layout(std140, set = 0, binding = 0) uniform source_matrices {
    mat4 viewMatrix;
    mat4 projectionMatrix;
    mat4 modelMatrix;
};

layout(std140, set = 0, binding = 2) uniform source_base_vertex {
    int numBones;
};

layout(std140, set = 0, binding = 4) uniform source_bone_matrices {
    mat4 bones[256];
};

layout(std140, set = 0, binding = 5) uniform source_vs_constants {
    vec4 vs_const[256];
};

const int VERTEX_SHADER_CAMERA_POS = 2;
const int VERTEX_SHADER_AMBIENT_LIGHT = 21;
const int VERTEX_SHADER_LIGHT_INFO = 27;
const int VERTEX_SHADER_BASE_TEXCOORD_TRANSFORM = 48; // SHADER_SPECIFIC_CONST_0
const float cOverbright = 2.0;

layout(location = 0) out vec2 vs_TexCoord;
layout(location = 1) out vec4 vs_Color;
layout(location = 2) out vec4 vs_VertexColor;
layout(location = 3) out vec3 vs_WorldNormal;
layout(location = 4) out vec3 vs_WorldVertToEye;

#include "common_vs_vk13.glsl"

void main()
{
    vec4 localPos = vec4(0.0);
    vec3 worldNormal = vec3(0.0);
    vec3 worldPos = vec3(0.0);
    mat4 mvp;

    if (numBones == 0) {
        mvp = projectionMatrix * viewMatrix * modelMatrix;
        gl_Position = mvp * vec4(v_Position, 1.0);
        worldNormal = mat3(modelMatrix) * v_Normal;
        worldPos = (modelMatrix * vec4(v_Position, 1.0)).xyz;
    }
    else {
        if (numBones >= 1) {
            localPos += (bones[v_BoneIndex.x] * vec4(v_Position, 1.0)) * v_BoneWeights.x;
            worldNormal += (mat3(bones[v_BoneIndex.x]) * v_Normal) * v_BoneWeights.x;
        }

        if (numBones >= 2) {
            localPos += (bones[v_BoneIndex.y] * vec4(v_Position, 1.0)) * v_BoneWeights.y;
            worldNormal += (mat3(bones[v_BoneIndex.y]) * v_Normal) * v_BoneWeights.y;
        }
        mvp = projectionMatrix * viewMatrix;
        gl_Position = mvp * localPos;
        worldPos = localPos.xyz;
    }

    vec4 texCoordInput = vec4(v_TexCoord, 0.0, 1.0);
    vs_TexCoord.x = dot(texCoordInput, vs_const[VERTEX_SHADER_BASE_TEXCOORD_TRANSFORM + 0]);
    vs_TexCoord.y = dot(texCoordInput, vs_const[VERTEX_SHADER_BASE_TEXCOORD_TRANSFORM + 1]);

    vs_VertexColor = Combo(COMBO_VERTEXCOLOR) ? v_Color : vec4(1.0);

    vs_WorldNormal = worldNormal;
    vs_WorldVertToEye = vs_const[VERTEX_SHADER_CAMERA_POS].xyz - worldPos;

    InitLightInfo();
    vs_Color.xyz = DoLightingUnrolled(worldPos, normalize(worldNormal), v_Specular.rgb,
                                      Combo(COMBO_STATIC_LIGHT), Combo(COMBO_DYNAMIC_LIGHT),
                                      Combo(COMBO_HALFLAMBERT), ComboNumLights());
    vs_Color.w = 1.0;
}
