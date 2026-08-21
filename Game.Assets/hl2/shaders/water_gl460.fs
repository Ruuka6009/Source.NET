#version 460

#include "common_gl460.fs"

in vec2 vs_TexCoord;
in vec3 vs_WorldNormal;
in vec3 vs_WorldVertToEye;
in vec3 vs_WorldPos;

layout(std140, binding = 6) uniform source_ps_constants {
    vec4 ps_const[256];
};

// ps_const[0] = $fogcolor.rgb, reflect amount
// ps_const[1] = time, wave scale, wave strength, has-cubemap
// ps_const[2] = sun direction, specular intensity
const int WATER_FOG = 0;
const int WATER_WAVE = 1;
const int WATER_SUN = 2;

uniform samplerCube envmap;

out vec4 fragColor;

// Ripples are procedural rather than sampled: water $normalmap is a dU/dV map in UV88, a format
// the texture path does not support, and summed waves animate for free anyway.
//
// Two things keep this from looking like corrugated iron. Each octave is rotated by an angle that
// does not divide the circle evenly, so crests never line up into a plaid; and the sample point is
// warped by a slow low-frequency wobble, so the pattern never repeats against itself. Each octave
// contributes its analytic height gradient, so the normal needs no differencing.
vec3 WaveNormal(vec2 p, float t, float strength, float dist, float scale)
{
    // Slow warp of the domain - the cheapest way to break up regularity.
    vec2 warp = vec2(sin(p.y * 0.081 + t * 0.35), cos(p.x * 0.073 - t * 0.28)) * 1.7;
    vec2 q = p + warp;

    vec2 grad = vec2(0.0);
    vec2 dir = normalize(vec2(1.0, 0.25));
    const mat2 rot = mat2(0.80, -0.60, 0.60, 0.80);   // ~37 degrees per octave

    float amp = 1.0;
    float freq = 1.0;

    for (int i = 0; i < 6; i++) {
        // Fade each octave out as its wavelength approaches a pixel. Without this the fine
        // detail aliases into moire bands across the distance, which is the single most
        // artificial thing about procedural water.
        float lod = exp(-freq * scale * dist * 0.02);

        float phase = dot(dir, q) * freq + t * (0.90 + float(i) * 0.35);
        grad += amp * freq * cos(phase) * dir * lod;

        amp *= 0.62;      // amplitude falls a little slower than frequency climbs, so the fine
        freq *= 1.75;     // detail still registers without drowning the swell
        dir = rot * dir;
    }

    // Large slow swell so some stretches are glassy and others choppy, rather than one uniform
    // chop everywhere.
    float swell = 0.45 + 0.55 * (0.5 + 0.5 * sin(q.x * 0.013 + t * 0.21) * cos(q.y * 0.011 - t * 0.17));
    grad *= swell;

    return normalize(vec3(-grad.x * strength, -grad.y * strength, 1.0));
}

// Stand-in sky for materials with no env_cubemap. Reflecting the 1x1 white fallback washes the
// surface out; a horizon-to-zenith ramp reads far closer to real water.
vec3 SkyGradient(vec3 dir)
{
    float up = clamp(dir.z * 0.5 + 0.5, 0.0, 1.0);
    vec3 horizon = vec3(0.70, 0.80, 0.92);
    vec3 zenith  = vec3(0.16, 0.40, 0.80);
    return mix(horizon, zenith, pow(up, 1.5));
}

void main()
{
    vec3 waterColor  = GammaToLinear(ps_const[WATER_FOG].rgb);
    float reflectAmt = ps_const[WATER_FOG].a;

    float time       = ps_const[WATER_WAVE].x;
    float waveScale  = ps_const[WATER_WAVE].y;
    float waveAmount = ps_const[WATER_WAVE].z;
    float hasEnvmap  = ps_const[WATER_WAVE].w;

    vec3  sunDir     = normalize(ps_const[WATER_SUN].xyz);
    float sunAmount  = ps_const[WATER_SUN].w;

    float eyeDist = length(vs_WorldVertToEye);
    vec3 vertToEye = vs_WorldVertToEye / max(eyeDist, 0.0001);
    vec3 geoNormal = normalize(vs_WorldNormal);

    // Looking at the surface from beneath - the eye is on the far side of the plane.
    bool underwater = dot(geoNormal, vertToEye) < 0.0;

    vec3 normal = WaveNormal(vs_WorldPos.xy * waveScale, time, waveAmount, eyeDist, waveScale);
    if (underwater)
        normal.z = -normal.z;

    float NdotV = clamp(dot(normal, vertToEye), 0.0, 1.0);
    float fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);

    vec3 linearColor;

    if (underwater) {
        // From below there is no sky to mirror: the surface mostly bounces the murk back down,
        // and everything is denser and darker than it looks from above.
        vec3 murk = waterColor * 0.40;
        vec3 body = waterColor * 0.55;
        linearColor = mix(body, murk, clamp(fresnel * 0.6, 0.0, 0.6));
    }
    else {
        vec3 reflectVect = reflect(-vertToEye, normal);
        vec3 reflection = hasEnvmap > 0.5
            ? GammaToLinear(texture(envmap, reflectVect).rgb)
            : SkyGradient(reflectVect);

        // Deeper looking straight down, lighter at glancing angles.
        vec3 body = mix(waterColor * 0.55, waterColor * 1.25, 1.0 - NdotV);

        // Capped so the water keeps its own colour at grazing angles, where an uncapped Fresnel
        // blend hands the whole surface to the reflection and washes the tint out.
        linearColor = mix(body, reflection, clamp(fresnel * reflectAmt, 0.0, 0.62));

        // Sun glitter riding the crests - the strongest cue that the water is moving. Tight
        // exponent so it stays sparkles rather than a broad sheen.
        vec3 halfVec = normalize(sunDir + vertToEye);
        float spec = pow(max(dot(normal, halfVec), 0.0), 180.0);
        linearColor += vec3(1.0, 0.97, 0.88) * spec * sunAmount;
    }

    fragColor.rgb = LinearToGamma(linearColor);
    fragColor.a = underwater
        ? clamp(0.80 + 0.20 * fresnel, 0.0, 1.0)
        : clamp(0.60 + 0.40 * fresnel, 0.0, 1.0);
}
