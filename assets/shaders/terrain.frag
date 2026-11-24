#version 450
/* =================================================================================================
 * FILE: terrain.frag
 * LOCATION: assets/shaders/terrain.frag
 * 
 * WHAT THIS FILE DOES:
 *   Continuous GPU Clipmap Terrain Fragment Shader.
 *   - Multi-layer procedural splat blending (Grass, Dirt, Rock).
 *   - Visualizes concentric clipmap LOD rings when debug ring color mode is active.
 *   - Directional sunlight PBR diffuse lighting with hemispherical ambient.
 *   - 4-Cascade CSM directional shadows.
 * ================================================================================================= */

layout(location = 0) in vec3 inWorldPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inTexCoord;
layout(location = 3) in float inMorphFactor;
layout(location = 4) in float inRingIndex;
layout(location = 5) in vec4 inClipPos;

layout(std140, set = 0, binding = 0) uniform LightEnvironmentUBO {
    vec3 SunDirection;
    float SunIntensity;
    vec3 SunColor;
    uint CastsShadows;
    vec3 AmbientSkyColor;
    float AmbientIntensity;
    vec3 AmbientGroundColor;
    float ShadowBias;
    mat4 LightViewProj;
    vec4 CascadeSplits;
    mat4 CascadeViewProj0;
    mat4 CascadeViewProj1;
    mat4 CascadeViewProj2;
    mat4 CascadeViewProj3;
    vec4 ShadowParams;
    vec4 ClusterParams;
} uLight;

layout(set = 0, binding = 1) uniform sampler2DArrayShadow uShadowMapArray;

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

const vec2 POISSON_DISK[16] = vec2[](
    vec2(-0.94201624, -0.39906216),
    vec2( 0.94558609, -0.76890725),
    vec2(-0.09418410, -0.92938870),
    vec2( 0.34495938,  0.29387760),
    vec2(-0.91588581,  0.45771432),
    vec2(-0.81544232, -0.87912464),
    vec2(-0.38277543,  0.27676845),
    vec2( 0.97484398,  0.75648379),
    vec2( 0.44323325, -0.97511554),
    vec2( 0.53742981, -0.47373420),
    vec2(-0.26496911, -0.41893023),
    vec2( 0.79197514,  0.19090143),
    vec2(-0.24188840,  0.99706507),
    vec2(-0.81409955,  0.91437590),
    vec2( 0.19984126,  0.78641367),
    vec2( 0.14383161, -0.14100790)
);

// Directional shadow map evaluation with 4-Cascade CSM for terrain
vec3 ShadowDebugColor(float value) {
    uint debugMode = uint(round(uLight.ShadowParams.w));
    if (debugMode == 1u) {
        if (value >= 0.999) return vec3(1.0, 0.0, 1.0);
        if (value < 0.25) return vec3(1.0, 0.15, 0.1);
        if (value < 0.5) return vec3(0.1, 0.9, 0.2);
        if (value < 0.75) return vec3(0.1, 0.4, 1.0);
        return vec3(1.0, 0.75, 0.05);
    }
    return vec3(value);
}

float EvaluateShadow(vec3 worldPos, vec3 N, float viewDepth, float bias, float NdotL) {
    uint shadowMode = uLight.CastsShadows & 3u;
    uint debugMode = uint(round(uLight.ShadowParams.w));
    if (debugMode == 0u && (shadowMode == 0u || NdotL <= 0.0)) {
        return NdotL <= 0.0 ? 0.0 : 1.0;
    }

    // Determine initial cascade index based on view space depth
    int cascadeIndex = 0;
    if (viewDepth > uLight.CascadeSplits.x) cascadeIndex = 1;
    if (viewDepth > uLight.CascadeSplits.y) cascadeIndex = 2;
    if (viewDepth > uLight.CascadeSplits.z) cascadeIndex = 3;

    if (viewDepth > uLight.CascadeSplits.w) {
        return 1.0;
    }

    // Try current cascade first; if the sample falls outside its bounds, fall back to outer cascades
    vec3 projCoords = vec3(0.0);
    int selectedCascade = -1;

    for (int c = cascadeIndex; c < 4; ++c) {
        mat4 cascadeVP;
        if (c == 0) cascadeVP = uLight.CascadeViewProj0;
        else if (c == 1) cascadeVP = uLight.CascadeViewProj1;
        else if (c == 2) cascadeVP = uLight.CascadeViewProj2;
        else cascadeVP = uLight.CascadeViewProj3;

        // Normal-offset bias eliminates acne on terrain and anchors contact shadows
        float normalOffset = 0.02 * float(c + 1) * clamp(1.0 - NdotL * 0.5, 0.2, 1.0);
        vec3 biasedWorldPos = worldPos + N * normalOffset;

        vec4 shadowClip = cascadeVP * vec4(biasedWorldPos, 1.0);
        vec3 p = shadowClip.xyz / shadowClip.w;

        // Convert from Vulkan NDC [-1, 1] to texture UV [0, 1]
        p.x = p.x * 0.5 + 0.5;
        p.y = p.y * 0.5 + 0.5;

        // In Reverse-Z, valid depth is [0.0, 1.0]
        if (p.x >= 0.001 && p.x <= 0.999 &&
            p.y >= 0.001 && p.y <= 0.999 &&
            p.z >= 0.0 && p.z <= 1.0) {
            projCoords = p;
            selectedCascade = c;
            break;
        }
    }

    if (selectedCascade == -1) {
        return 1.0;
    }

    float cascadeScale = float(selectedCascade + 1);
    float tanTheta = sqrt(clamp(1.0 - NdotL * NdotL, 0.0, 1.0)) / max(NdotL, 0.05);
    float dynamicBias = min(max(bias, 0.0) * cascadeScale * (1.0 + min(tanTheta, 4.0) * 0.25), 0.002);
    if (debugMode == 2u) return projCoords.z;
    if (debugMode == 3u) return clamp(dynamicBias / 0.002, 0.0, 1.0);
    if (debugMode == 1u) return (float(selectedCascade) + 0.5) * 0.25;
    float currentDepth = projCoords.z + dynamicBias;

    float layer = float(selectedCascade);
    vec2 texelSize = vec2(uLight.ShadowParams.y);

    if (shadowMode >= 2u) {
        // Ultra High-Quality Mode: 16-tap Poisson Disk filter (cascade-scaled to maintain crisp distant shadows)
        float filterRadius = max(uLight.ShadowParams.z / sqrt(cascadeScale), 1.0);
        float shadow = 0.0;
        for (int i = 0; i < 16; ++i) {
            vec2 offset = POISSON_DISK[i] * (texelSize * filterRadius);
            shadow += texture(uShadowMapArray, vec4(projCoords.xy + offset, layer, currentDepth));
        }
        return shadow / 16.0;
    } else {
        float shadow = 0.0;
        for (int x = -1; x <= 1; ++x) {
            for (int y = -1; y <= 1; ++y) {
                vec2 offset = vec2(x, y) * texelSize;
                shadow += texture(uShadowMapArray, vec4(projCoords.xy + offset, layer, currentDepth));
            }
        }
        return shadow / 9.0;
    }
}

void main()
{
    vec3 N = normalize(inNormal);
    vec3 L = normalize(-uLight.SunDirection); // Direction pointing towards sunlight source

    // Multi-layer procedural splat blending based on slope (N.y)
    float slope = clamp(1.0 - N.y, 0.0, 1.0);

    vec3 grassColor = vec3(0.24, 0.55, 0.18);
    vec3 rockColor = vec3(0.48, 0.46, 0.44);
    vec3 dirtColor = vec3(0.42, 0.32, 0.20);

    // Blend: flat = grass, moderate slope = dirt, steep cliff = rock
    vec3 albedo = mix(grassColor, dirtColor, smoothstep(0.15, 0.45, slope));
    albedo = mix(albedo, rockColor, smoothstep(0.40, 0.70, slope));

    // If debug ring colors are enabled (inRingIndex >= 0.0), blend LOD ring color
    if (inRingIndex >= 0.0)
    {
        vec3 ringColor = getRingColor(int(inRingIndex + 0.5));
        albedo = mix(albedo, ringColor, 0.75);
    }

    // Directional diffuse (calibrated for viewport dynamic range) with 4-Cascade CSM shadows
    float NdotL = max(dot(N, L), 0.0);
    float shadow = EvaluateShadow(inWorldPos, N, inClipPos.w, uLight.ShadowBias, NdotL);
    if (uLight.ShadowParams.w > 0.5)
    {
        outColor = vec4(ShadowDebugColor(shadow), 1.0);
        return;
    }
    vec3 lightRadiance = uLight.SunColor * (uLight.SunIntensity * 0.000025);
    vec3 diffuse = lightRadiance * NdotL * shadow;

    // Hemispherical ambient (calibrated for clear visibility in shaded valleys)
    vec3 ambient = mix(uLight.AmbientGroundColor, uLight.AmbientSkyColor, N.y * 0.5 + 0.5) * (uLight.AmbientIntensity * 2.2);

    vec3 finalColor = albedo * (diffuse + ambient);
    outColor = vec4(finalColor, 1.0);
}
