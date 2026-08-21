#version 460



layout(location = 0) in vec3 v_Position;
layout(location = 10) in vec2 v_TexCoord;

layout(std140, binding = 0) uniform source_matrices {
    mat4 viewMatrix;
    mat4 projectionMatrix;
    mat4 modelMatrix;
};

out vec2 vs_TexCoord;

void main()
{
    // DrawScreenSpaceRectangle hands us clip-space positions with identity matrices loaded, so
    // this is a pass-through in practice - but going through the matrices keeps the depth remap
    // and the Y flip consistent with every other shader.
    gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(v_Position, 1.0);
    vs_TexCoord = v_TexCoord;
}
