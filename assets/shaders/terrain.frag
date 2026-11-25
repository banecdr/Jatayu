#version 450
/* =================================================================================================
 * FILE: terrain.frag
 * LOCATION: assets/shaders/terrain.frag
 * 
 * WHAT THIS FILE DOES:
 *   Continuous GPU Clipmap Terrain Fragment Shader.
 *   - Multi-layer procedural splat blending (Grass, Dirt, Rock).
 *   - Visualizes concentric clipmap LOD rings when debug ring color mode is active.
 *   - Directional sunlight diffuse lighting with hemispherical ambient.
 *   - 4-Cascade CSM directional shadows through shadow_common.glsl (normal-offset + receiver-plane
 *     bias, cascade blending, contact-hardening soft shadows, distance fade).
 *   - Distance-faded procedural micro-relief normal and macro albedo variation, evaluated in stable
 *     absolute world coordinates (inTexCoord) so lighting detail never swims with the camera.
 * ================================================================================================= */

layout(location = 0) in vec3 inWorldPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inTexCoord;   // absolute world XZ (metres)
layout(location = 3) in float inMorphFactor;
layout(location = 4) in float inRingIndex;
layout(location = 5) in vec4 inClipPos;

// Light environment UBO (binding 0), shadow samplers (bindings 1 / 11) and the shared shadow filter.
#include "shadow_common.glsl"

layout(push_constant) uniform PushConstants
{
    mat4 viewProj;
    vec4 originScale;
    vec4 params;
} pc;

layout(location = 0) out vec4 outColor;

vec3 getRingColor(int r)
{
    if (r == 0) return vec3(0.10, 0.90, 0.25); // Ring 0: Emerald Green
    if (r == 1) return vec3(0.10, 0.80, 0.95); // Ring 1: Cyan
    if (r == 2) return vec3(0.95, 0.90, 0.10); // Ring 2: Bright Yellow
    if (r == 3) return vec3(0.95, 0.50, 0.10); // Ring 3: Orange
    if (r == 4) return vec3(0.95, 0.15, 0.15); // Ring 4: Crimson Red
    if (r == 5) return vec3(0.70, 0.10, 0.90); // Ring 5: Deep Purple
    return vec3(0.30, 0.70, 0.90);              // Ring 6: Light Blue
}

// Dave Hoskins hash: well behaved for large coordinates (no sin() precision problems).
float TerrainHash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float TerrainValueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = TerrainHash12(i);
    float b = TerrainHash12(i + vec2(1.0, 0.0));
    float c = TerrainHash12(i + vec2(0.0, 1.0));
    float d = TerrainHash12(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// Height-field gradient (metres of rise per metre of run) of amplitude * noise(world / wavelength).
vec2 TerrainNoiseSlope(vec2 world, float wavelength, float amplitude)
{
    vec2 p = world / wavelength;
    const float h = 0.25;
    float h0 = TerrainValueNoise(p);
    float dx = TerrainValueNoise(p + vec2(h, 0.0)) - h0;
    float dz = TerrainValueNoise(p + vec2(0.0, h)) - h0;
    return vec2(dx, dz) * (amplitude / (h * wavelength));
}

// Two-octave micro relief perturbation, faded with view distance to avoid aliasing and shimmer.
vec3 TerrainDetailNormal(vec3 N, vec2 world, float viewDepth)
{
    float fadeCoarse = 1.0 - smoothstep(40.0, 160.0, viewDepth);
    if (fadeCoarse <= 0.0) return N;

    vec2 slope = TerrainNoiseSlope(world, 6.0, 0.55) * fadeCoarse;

    float fadeFine = 1.0 - smoothstep(15.0, 70.0, viewDepth);
    if (fadeFine > 0.0) {
        slope += TerrainNoiseSlope(world + 17.3, 1.7, 0.12) * fadeFine;
    }

    // Tilt the normal against the micro height gradient; weaker on steep faces where N.y is small.
    return normalize(N + vec3(-slope.x, 0.0, -slope.y) * N.y);
}

void main()
{
    // Derivatives for receiver-plane shadow bias (uniform control flow, no discards in this shader).
    vec3 dPdx = dFdx(inWorldPos);
    vec3 dPdy = dFdy(inWorldPos);

    vec3 Ng = normalize(inNormal);
    vec3 L = normalize(-uLight.SunDirection); // Direction pointing towards sunlight source

    // Multi-layer procedural splat blending based on slope (N.y)
    float slope = clamp(1.0 - Ng.y, 0.0, 1.0);

    vec3 grassColor = vec3(0.24, 0.55, 0.18);
    vec3 rockColor = vec3(0.48, 0.46, 0.44);
    vec3 dirtColor = vec3(0.42, 0.32, 0.20);

    // Blend: flat = grass, moderate slope = dirt, steep cliff = rock
    vec3 albedo = mix(grassColor, dirtColor, smoothstep(0.15, 0.45, slope));
    albedo = mix(albedo, rockColor, smoothstep(0.40, 0.70, slope));

    // Macro variation (about +-7%) breaks up flat colour fields; stable in absolute world space.
    if (inClipPos.w < 160.0) {
        float macroFade = 1.0 - smoothstep(60.0, 160.0, inClipPos.w);
        float macro = TerrainValueNoise(inTexCoord / 45.0) * 0.65 + TerrainValueNoise(inTexCoord / 11.0) * 0.35;
        albedo *= mix(1.0, mix(0.93, 1.07, macro), macroFade);
    }

    // If debug ring colors are enabled (inRingIndex >= 0.0), blend LOD ring color
    if (inRingIndex >= 0.0)
    {
        vec3 ringColor = getRingColor(int(inRingIndex + 0.5));
        albedo = mix(albedo, ringColor, 0.75);
    }

    // Micro-relief shading normal (the shadow receiver offset keeps using the smooth geometric normal)
    vec3 N = TerrainDetailNormal(Ng, inTexCoord, inClipPos.w);

    // Directional diffuse (calibrated for viewport dynamic range) with 4-Cascade CSM shadows
    float NdotL = max(dot(N, L), 0.0);
    float shadow = EvaluateDirectionalShadow(inWorldPos, Ng, inClipPos.w, gl_FragCoord.xy, dPdx, dPdy);
    if (uLight.ShadowParams.w > 0.5)
    {
        outColor = vec4(ShadowDebugColor(shadow), 1.0);
        return;
    }
    vec3 lightRadiance = uLight.SunColor * (uLight.SunIntensity * 0.000025);
    vec3 diffuse = lightRadiance * NdotL * shadow;

    // Hemispherical ambient (calibrated for clear visibility in shaded valleys)
    vec3 ambient = mix(uLight.AmbientGroundColor, uLight.AmbientSkyColor, N.y * 0.5 + 0.5) * (uLight.AmbientIntensity * 0.88);

    vec3 finalColor = albedo * (diffuse + ambient);
    outColor = vec4(finalColor, 1.0);
}
