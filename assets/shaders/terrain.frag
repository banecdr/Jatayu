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
} uLight;

layout(set = 0, binding = 1) uniform sampler2DShadow uShadowMap;

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

void main()
{
    vec3 N = normalize(inNormal);
    vec3 L = normalize(uLight.SunDirection);

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
        albedo = mix(albedo, ringColor, 0.65);
    }

    // Directional diffuse
    float NdotL = max(dot(N, L), 0.0);
    vec3 diffuse = uLight.SunColor * NdotL * uLight.SunIntensity;

    // Hemispherical ambient
    vec3 ambient = mix(uLight.AmbientGroundColor, uLight.AmbientSkyColor, N.y * 0.5 + 0.5) * uLight.AmbientIntensity;

    vec3 finalColor = albedo * (diffuse + ambient);
    outColor = vec4(finalColor, 1.0);
}
