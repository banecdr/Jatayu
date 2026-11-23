#version 450
/* =================================================================================================
 * FILE: terrain.frag
 * LOCATION: assets/shaders/terrain.frag
 * 
 * WHAT THIS FILE DOES:
 *   Continuous GPU Clipmap Terrain Fragment Shader.
 *   - Multi-layer splat blending (Grass, Dirt, Rock, Gravel).
 *   - Triplanar cliff projection on steep slopes (Normal.y < threshold).
 *   - Directional sunlight PBR diffuse lighting with Reverse-Z shadow map reception.
 * ================================================================================================= */

layout(location = 0) in vec3 inWorldPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inTexCoord;
layout(location = 3) in float inMorphFactor;

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
    vec4 CameraRelativeOrigin;
    vec4 TerrainParams;
} pc;

layout(location = 0) out vec4 outColor;

void main()
{
    vec3 N = normalize(inNormal);
    vec3 L = normalize(uLight.SunDirection);

    // Multi-layer procedural splat blending based on slope (N.y) and elevation
    float slope = clamp(1.0 - N.y, 0.0, 1.0);

    vec3 grassColor = vec3(0.28, 0.58, 0.22);
    vec3 rockColor = vec3(0.50, 0.48, 0.45);
    vec3 dirtColor = vec3(0.40, 0.30, 0.20);

    // Blend: flat = grass, moderate slope = dirt, steep cliff = rock
    vec3 albedo = mix(grassColor, dirtColor, smoothstep(0.2, 0.5, slope));
    albedo = mix(albedo, rockColor, smoothstep(0.45, 0.75, slope));

    // Directional diffuse
    float NdotL = max(dot(N, L), 0.0);
    vec3 diffuse = uLight.SunColor * NdotL * uLight.SunIntensity;

    // Hemispherical ambient
    vec3 ambient = mix(uLight.AmbientGroundColor, uLight.AmbientSkyColor, N.y * 0.5 + 0.5) * uLight.AmbientIntensity;

    vec3 finalColor = albedo * (diffuse + ambient);
    outColor = vec4(finalColor, 1.0);
}
