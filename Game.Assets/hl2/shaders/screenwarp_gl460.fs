#version 460

#include "common_gl460.fs"

in vec2 vs_TexCoord;

layout(std140, binding = 6) uniform source_ps_constants {
    vec4 ps_const[256];
};

// ps_const[0] = time, warp amount, tint amount, edge darkening
// ps_const[1] = tint colour
const int WARP_PARAMS = 0;
const int WARP_TINT = 1;

uniform sampler2D basetexture;

out vec4 fragColor;

void main()
{
    float time     = ps_const[WARP_PARAMS].x;
    float amount   = ps_const[WARP_PARAMS].y;
    float tintAmt  = ps_const[WARP_PARAMS].z;
    float edgeDark = ps_const[WARP_PARAMS].w;
    vec3  tint     = ps_const[WARP_TINT].rgb;

    // Two crossed ripple trains at different rates. Sampling the frame through this offset is
    // what actually bends the scene - the same trick Source's refraction uses, just driven by a
    // procedural offset instead of a dU/dV texture.
    vec2 uv = vs_TexCoord;
    vec2 warp;
    warp.x = sin(uv.y * 18.0 + time * 1.70) * 0.0060
           + sin(uv.y * 41.0 - time * 2.30) * 0.0025;
    warp.y = cos(uv.x * 22.0 - time * 1.40) * 0.0060
           + cos(uv.x * 37.0 + time * 2.90) * 0.0020;

    vec2 warped = clamp(uv + warp * amount, vec2(0.001), vec2(0.999));

    vec3 scene = GammaToLinear(texture(basetexture, warped).rgb);

    // Water swallows the warm end of the spectrum first, so tinting toward the water colour with
    // depth is what makes it read as looking through water rather than through coloured glass.
    scene = mix(scene, scene * GammaToLinear(tint) * 2.0, tintAmt);

    // Light falls off toward the edges of vision underwater.
    vec2 fromCentre = uv - vec2(0.5);
    float vignette = 1.0 - edgeDark * dot(fromCentre, fromCentre) * 2.0;
    scene *= clamp(vignette, 0.0, 1.0);

    fragColor = vec4(LinearToGamma(scene), 1.0);
}
