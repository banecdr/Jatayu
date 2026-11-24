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
 * ================================================================================================= */

layout(location = 0) in vec3 inWorldPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inTexCoord;
layout(location = 3) in float inMorphFactor;
layout(location = 4) in float inRingIndex;
layout(location = 5) in vec2 inGridPos;
layout(location = 6) in vec4 inClipPos;

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

layout(push_constant) uniform TerrainPushConstants
{
    mat4 ViewProjMatrix;
    vec4 CameraRelativeOrigin; // xyz = camera world position, w = geomorph factor
    vec4 TerrainParams;        // x = ring scale, y = debug ring colors flag, z = max height, w = ring index
} pc;

layout(location = 0) out vec4 outColor;

vec3 getRingColor(int r)
{
    if (r == 0) return vec3(0.10, 0.90, 0.25); // Ring 0: Emerald Green
    if (r == 1) return vec3(0.10, 0.80, 0.95); // Ring 1: Cyan
    if (r == 2) return vec3(0.95, 0.90, 0.10); // Ring 2: Bright Yellow
    if (r == 3) return vec3(0.95, 0.50, 0.10); // Ring 3: Orange
    if (r == 4) return vec3(0.95, 0.15, 0.15); // Ring 4: Crimson Red
    return vec3(0.70, 0.10, 0.90);              // Ring 5: Deep Purple
}

// Directional shadow map evaluation with 4-Cascade CSM for terrain
float EvaluateShadow(vec3 worldPos, float viewDepth, float bias, float NdotL) {
    if (uLight.CastsShadows == 0u || NdotL <= 0.0) {
        return NdotL <= 0.0 ? 0.0 : 1.0;
    }

    int cascadeIndex = 0;
    if (viewDepth > uLight.CascadeSplits.x) cascadeIndex = 1;
    if (viewDepth > uLight.CascadeSplits.y) cascadeIndex = 2;
    if (viewDepth > uLight.CascadeSplits.z) cascadeIndex = 3;

    if (viewDepth > uLight.CascadeSplits.w) {
        return 1.0;
    }

    mat4 cascadeVP;
    if (cascadeIndex == 0) cascadeVP = uLight.CascadeViewProj0;
    else if (cascadeIndex == 1) cascadeVP = uLight.CascadeViewProj1;
    else if (cascadeIndex == 2) cascadeVP = uLight.CascadeViewProj2;
    else cascadeVP = uLight.CascadeViewProj3;

    vec4 shadowClip = cascadeVP * vec4(worldPos, 1.0);
    vec3 projCoords = shadowClip.xyz / shadowClip.w;

    projCoords.x = projCoords.x * 0.5 + 0.5;
    projCoords.y = projCoords.y * 0.5 + 0.5;

    if (projCoords.x < 0.0 || projCoords.x > 1.0 ||
        projCoords.y < 0.0 || projCoords.y > 1.0 ||
        projCoords.z < 0.0 || projCoords.z > 1.0) {
        return 1.0;
    }

    float cascadeScale = float(cascadeIndex + 1);
    float tanTheta = sqrt(clamp(1.0 - NdotL * NdotL, 0.0, 1.0)) / max(NdotL, 0.05);
    float dynamicBias = clamp(bias * cascadeScale * (1.0 + tanTheta * 1.5), bias * 0.5, 0.015);
    float currentDepth = projCoords.z + dynamicBias;

    vec2 texelSize = vec2(uLight.ShadowParams.y);
    float shadow = 0.0;
    float layer = float(cascadeIndex);

    for (int x = -1; x <= 1; ++x) {
        for (int y = -1; y <= 1; ++y) {
            vec2 offset = vec2(x, y) * texelSize;
            shadow += texture(uShadowMapArray, vec4(projCoords.xy + offset, layer, currentDepth));
        }
    }
    return shadow / 9.0;
}

void main()
{
    // Eliminate Z-fighting by discarding the inner region of outer rings (already drawn by finer ring)
    if (inRingIndex > 0.5)
    {
        vec2 absGrid = abs(inGridPos);
        if (absGrid.x < 0.245 && absGrid.y < 0.245)
        {
            discard;
        }
    }

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

    // If debug ring colors are enabled (TerrainParams.y > 0.5), blend LOD ring color
    if (pc.TerrainParams.y > 0.5)
    {
        vec3 ringColor = getRingColor(int(inRingIndex + 0.5));
        albedo = mix(albedo, ringColor, 0.75);
    }

    // Directional diffuse (calibrated for viewport dynamic range) with 4-Cascade CSM shadows
    float NdotL = max(dot(N, L), 0.0);
    float shadow = EvaluateShadow(inWorldPos, inClipPos.w, uLight.ShadowBias, NdotL);
    vec3 lightRadiance = uLight.SunColor * (uLight.SunIntensity * 0.000025);
    vec3 diffuse = lightRadiance * NdotL * shadow;

    // Hemispherical ambient (calibrated for clear visibility in shaded valleys)
    vec3 ambient = mix(uLight.AmbientGroundColor, uLight.AmbientSkyColor, N.y * 0.5 + 0.5) * (uLight.AmbientIntensity * 2.2);

    vec3 finalColor = albedo * (diffuse + ambient);
    outColor = vec4(finalColor, 1.0);
}
