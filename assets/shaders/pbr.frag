#version 460 core
#extension GL_EXT_nonuniform_qualifier : enable

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec3 vTangent;
layout(location = 3) in vec3 vBitangent;
layout(location = 4) in vec2 vTexCoord;
layout(location = 5) in vec4 vColor;
layout(location = 6) in vec4 vShadowCoord;
layout(location = 7) flat in uint vMaterialId;
layout(location = 8) in vec4 vCurrClipPos;
layout(location = 9) in vec4 vPrevClipPos;

layout(location = 0) out vec4 outColor;

struct GpuMaterialData {
    vec4 BaseColorFactor;
    float MetallicFactor;
    float RoughnessFactor;
    uint AlbedoIndex;
    uint NormalIndex;
    uint MetallicRoughnessIndex;
    uint OcclusionIndex;
    uint EmissiveIndex;
    float EmissiveIntensity;
    vec3 EmissiveFactor;
    uint Flags;
};

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

layout(std430, set = 0, binding = 2) readonly buffer MaterialStorage {
    GpuMaterialData materials[];
} uMaterialTable;

layout(set = 1, binding = 0) uniform sampler2D uTextures[];

const float PI = 3.14159265358979323846;

// Cook-Torrance GGX Normal Distribution Function
float DistributionGGX(vec3 N, vec3 H, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;

    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;

    return a2 / max(denom, 0.0000001);
}

// Schlick-GGX Geometry Function
float GeometrySchlickGGX(float NdotV, float roughness) {
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;

    float denom = NdotV * (1.0 - k) + k;
    return NdotV / max(denom, 0.0000001);
}

// Smith Geometric Attenuation
float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness) {
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float ggx1 = GeometrySchlickGGX(NdotV, roughness);
    float ggx2 = GeometrySchlickGGX(NdotL, roughness);

    return ggx1 * ggx2;
}

// Fresnel-Schlick approximation
vec3 FresnelSchlick(float cosTheta, vec3 F0) {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// Directional shadow map evaluation with Reverse-Z comparison and 3x3 PCF
float EvaluateShadow(vec4 shadowCoord, float bias, float NdotL) {
    if (uLight.CastsShadows == 0u || NdotL <= 0.0) {
        return NdotL <= 0.0 ? 0.0 : 1.0;
    }

    vec3 projCoords = shadowCoord.xyz / shadowCoord.w;

    // Outside light frustum -> no shadow
    if (projCoords.x < 0.0 || projCoords.x > 1.0 ||
        projCoords.y < 0.0 || projCoords.y > 1.0 ||
        projCoords.z < 0.0 || projCoords.z > 1.0) {
        return 1.0;
    }

    // In Reverse-Z, closer to light is higher depth value (1.0 = near, 0.0 = far).
    // Slope-scaled tangent bias pushes the reference depth closer to the light, preventing grazing shadow acne.
    float tanTheta = sqrt(clamp(1.0 - NdotL * NdotL, 0.0, 1.0)) / max(NdotL, 0.05);
    float dynamicBias = clamp(bias * (1.0 + tanTheta * 1.5), bias * 0.5, 0.008);
    float currentDepth = projCoords.z + dynamicBias;

    vec2 texelSize = 1.0 / vec2(textureSize(uShadowMap, 0));
    float shadow = 0.0;

    for (int x = -1; x <= 1; ++x) {
        for (int y = -1; y <= 1; ++y) {
            vec2 offset = vec2(x, y) * texelSize;
            shadow += texture(uShadowMap, vec3(projCoords.xy + offset, currentDepth));
        }
    }

    return shadow / 9.0;
}

void main() {
    GpuMaterialData mat = uMaterialTable.materials[vMaterialId];

    vec4 albedo = mat.BaseColorFactor * vColor;
    if (mat.AlbedoIndex != 0xFFFFFFFFu) {
        albedo *= texture(uTextures[nonuniformEXT(mat.AlbedoIndex)], vTexCoord);
    }

    // Alpha test if needed
    if (albedo.a < 0.01) {
        discard;
    }

    float metallic = mat.MetallicFactor;
    float roughness = mat.RoughnessFactor;
    if (mat.MetallicRoughnessIndex != 0xFFFFFFFFu) {
        vec4 mrSample = texture(uTextures[nonuniformEXT(mat.MetallicRoughnessIndex)], vTexCoord);
        roughness *= mrSample.g;
        metallic *= mrSample.b;
    }
    roughness = clamp(roughness, 0.04, 1.0);

    float ao = 1.0;
    if (mat.OcclusionIndex != 0xFFFFFFFFu) {
        ao = texture(uTextures[nonuniformEXT(mat.OcclusionIndex)], vTexCoord).r;
    }

    // Normal mapping
    vec3 N = normalize(vNormal);
    if (mat.NormalIndex != 0xFFFFFFFFu) {
        vec3 normalSample = texture(uTextures[nonuniformEXT(mat.NormalIndex)], vTexCoord).rgb * 2.0 - 1.0;
        mat3 TBN = mat3(normalize(vTangent), normalize(vBitangent), N);
        N = normalize(TBN * normalSample);
    }

    // Camera view vector (assuming camera is at (0, 0, 0) in camera-relative space or vWorldPos)
    vec3 V = normalize(-vWorldPos);
    vec3 L = normalize(-uLight.SunDirection);
    vec3 H = normalize(V + L);

    // Dielectric base reflectivity = 0.04, metals use albedo
    vec3 F0 = mix(vec3(0.04), albedo.rgb, metallic);

    // Direct lighting Cook-Torrance BRDF
    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    vec3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

    vec3 numerator = NDF * G * F;
    float denominator = 4.0 * max(dot(N, V), 0.0) * max(dot(N, L), 0.0) + 0.0001;
    vec3 specular = numerator / denominator;

    float NdotL = max(dot(N, L), 0.0);
    float shadow = EvaluateShadow(vShadowCoord, uLight.ShadowBias, NdotL);

    // Directional light radiance (calibrated for SDR viewport)
    vec3 lightRadiance = uLight.SunColor * (uLight.SunIntensity * 0.000025);
    vec3 directLighting = (kD * albedo.rgb + specular * PI) * lightRadiance * NdotL * shadow;

    // Hemispherical ambient environment lighting with metallic ambient specular fallback
    float skyFactor = clamp(N.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 ambientColor = mix(uLight.AmbientGroundColor, uLight.AmbientSkyColor, skyFactor);
    vec3 ambientDiffuse = ambientColor * albedo.rgb * (1.0 - metallic);
    vec3 ambientSpecular = ambientColor * F0 * (1.0 - roughness * 0.5);
    vec3 ambientLighting = (ambientDiffuse + ambientSpecular) * uLight.AmbientIntensity * ao;

    // Emissive contribution
    vec3 emissive = mat.EmissiveFactor * mat.EmissiveIntensity;
    if (mat.EmissiveIndex != 0xFFFFFFFFu) {
        emissive *= texture(uTextures[nonuniformEXT(mat.EmissiveIndex)], vTexCoord).rgb;
    }

    vec3 finalColor = directLighting + ambientLighting + emissive;

    outColor = vec4(finalColor, albedo.a);
}
