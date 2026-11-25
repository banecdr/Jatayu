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

// Light environment UBO (binding 0), shadow samplers (bindings 1 / 11) and the shared shadow filter.
#include "shadow_common.glsl"

layout(std430, set = 0, binding = 2) readonly buffer MaterialStorage {
    GpuMaterialData materials[];
} uMaterialTable;

struct PointLight {
    vec3 Position;
    float Radius;
    vec3 Color;
    float Intensity;
};

struct SpotLight {
    vec3 Position;
    float Range;
    vec3 Direction;
    float CosInner;
    vec3 Color;
    float CosOuter;
};

struct ClusterRecord {
    uint Offset;
    uint Count;
};

layout(std430, set = 0, binding = 4) readonly buffer PointLightStorage {
    PointLight pointLights[];
} uPointLights;

layout(std430, set = 0, binding = 5) readonly buffer SpotLightStorage {
    SpotLight spotLights[];
} uSpotLights;

layout(std430, set = 0, binding = 6) readonly buffer ClusterRecordStorage {
    ClusterRecord clusterRecords[];
} uClusterRecords;

layout(std430, set = 0, binding = 7) readonly buffer LightIndexStorage {
    uint globalIndexCounter;
    uint lightIndices[];
} uLightIndices;

layout(set = 0, binding = 8) uniform sampler2D uBrdfLut;
layout(set = 0, binding = 9) uniform samplerCube uIrradianceMap;
layout(set = 0, binding = 10) uniform samplerCube uPrefilterMap;

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

// Height-correlated Smith-GGX visibility term V = G / (4 * NdotL * NdotV).
// More accurate than separable Schlick-GGX, especially at grazing angles and low roughness.
float VisibilitySmithGGXCorrelated(float NdotV, float NdotL, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float gv = NdotL * sqrt(NdotV * NdotV * (1.0 - a2) + a2);
    float gl = NdotV * sqrt(NdotL * NdotL * (1.0 - a2) + a2);
    return 0.5 / max(gv + gl, 0.00001);
}

// Fresnel-Schlick approximation
vec3 FresnelSchlick(float cosTheta, vec3 F0) {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// Specular anti-aliasing (Tokuyoshi / Kaplanyan): widens roughness where the shading normal varies
// quickly across a pixel, which removes specular shimmer on normal-mapped and high-frequency geometry.
float SpecularAntiAliasRoughness(vec3 N, float roughness) {
    vec3 dndx = dFdx(N);
    vec3 dndy = dFdy(N);
    float variance = 0.25 * (dot(dndx, dndx) + dot(dndy, dndy));
    float kernel = min(2.0 * variance, 0.18);
    float alpha = roughness * roughness;
    float filteredAlpha2 = clamp(alpha * alpha + kernel, 0.0, 1.0);
    return clamp(sqrt(sqrt(filteredAlpha2)), 0.0, 1.0);
}

// Specular occlusion from ambient occlusion (Lagarde & de Rousiers, Frostbite).
float SpecularOcclusion(float NdotV, float ao, float roughness) {
    return clamp(pow(NdotV + ao, exp2(-16.0 * roughness - 1.0)) - 1.0 + ao, 0.0, 1.0);
}

// Shared BRDF evaluation for point and spot lights: returns (diffuse + specular * PI), to be multiplied
// by radiance * NdotL by the caller.
vec3 EvaluateLocalBrdf(
    vec3 N, vec3 V, vec3 dir, float NdotL,
    vec3 albedoRgb, float metallic, float roughness, vec3 F0, vec3 energyComp)
{
    vec3 H = normalize(V + dir);
    float NdotV = max(dot(N, V), 0.0001);
    float NDF = DistributionGGX(N, H, roughness);
    float Vis = VisibilitySmithGGXCorrelated(NdotV, NdotL, roughness);
    vec3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);
    vec3 spec = NDF * Vis * F * energyComp;
    vec3 diff = (vec3(1.0) - F) * (1.0 - metallic) * albedoRgb;
    return diff + spec * PI;
}

void main() {
    // Screen-space derivatives must be evaluated in uniform control flow, before any discard.
    vec3 dPdx = dFdx(vWorldPos);
    vec3 dPdy = dFdy(vWorldPos);

    GpuMaterialData mat = uMaterialTable.materials[vMaterialId];

    vec4 albedo = mat.BaseColorFactor * vColor;
    if (mat.AlbedoIndex != 0xFFFFFFFFu) {
        albedo *= texture(uTextures[nonuniformEXT(mat.AlbedoIndex)], vTexCoord);
    }

    float metallic = mat.MetallicFactor;
    float roughness = mat.RoughnessFactor;
    if (mat.MetallicRoughnessIndex != 0xFFFFFFFFu) {
        vec4 mrSample = texture(uTextures[nonuniformEXT(mat.MetallicRoughnessIndex)], vTexCoord);
        roughness *= mrSample.g;
        metallic *= mrSample.b;
    }
    roughness = clamp(roughness, 0.0, 1.0);

    float ao = 1.0;
    if (mat.OcclusionIndex != 0xFFFFFFFFu) {
        ao = texture(uTextures[nonuniformEXT(mat.OcclusionIndex)], vTexCoord).r;
    }

    // Geometric normal (used for shadow receiver offset and reflection horizon occlusion)
    vec3 Ng = normalize(vNormal);

    // Normal mapping
    vec3 N = Ng;
    if (mat.NormalIndex != 0xFFFFFFFFu) {
        vec3 normalSample = texture(uTextures[nonuniformEXT(mat.NormalIndex)], vTexCoord).rgb * 2.0 - 1.0;
        mat3 TBN = mat3(normalize(vTangent), normalize(vBitangent), Ng);
        N = normalize(TBN * normalSample);

        // Filter roughness against sub-pixel normal variation on high-frequency normal maps
        roughness = SpecularAntiAliasRoughness(N, roughness);
    }

    // Alpha test if needed
    if (albedo.a < 0.01) {
        discard;
    }

    // Camera view vector (camera sits at the origin of camera-relative space)
    vec3 V = normalize(-vWorldPos);
    vec3 L = normalize(-uLight.SunDirection);
    vec3 H = normalize(V + L);

    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);

    // Dielectric base reflectivity = 0.04, metals use albedo
    vec3 F0 = mix(vec3(0.04), albedo.rgb, metallic);

    // Split-sum BRDF terms (also used for multiple-scattering energy compensation)
    vec2 brdf = texture(uBrdfLut, vec2(NdotV, roughness)).rg;
    bool hasIbl = (uLight.CastsShadows & 0x80000000u) != 0u;

    // Multiple-scattering compensation (Fdez-Aguera 2019): restores energy lost by the single-scatter
    // GGX model on rough metals. Only valid once the real BRDF LUT is bound (IBL probe present).
    vec3 energyComp = vec3(1.0);
    if (hasIbl) {
        float ess = max(brdf.x + brdf.y, 0.05);
        energyComp = min(1.0 + F0 * (1.0 / ess - 1.0), vec3(3.0));
    }

    // Direct sun lighting: Cook-Torrance BRDF with height-correlated visibility
    float directRoughness = max(roughness, 0.04);
    float NDF = DistributionGGX(N, H, directRoughness);
    float Vis = VisibilitySmithGGXCorrelated(max(NdotV, 0.0001), max(NdotL, 0.0001), directRoughness);
    vec3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);
    vec3 specular = NDF * Vis * F * energyComp;

    float shadow = EvaluateDirectionalShadow(vWorldPos, Ng, vCurrClipPos.w, gl_FragCoord.xy, dPdx, dPdy);
    if (uLight.ShadowParams.w > 0.5) {
        outColor = vec4(ShadowDebugColor(shadow), 1.0);
        return;
    }

    // Directional light radiance (calibrated for SDR viewport)
    vec3 lightRadiance = uLight.SunColor * (uLight.SunIntensity * 0.000025);
    vec3 directLighting = (kD * albedo.rgb + specular * PI) * lightRadiance * NdotL * shadow;

    // 3D Clustered Local Lighting (Point & Spot lights assigned to 16x8x24 grid)
    vec3 localLighting = vec3(0.0);
    if (uLight.ClusterParams.w > 0.5) {
        float tileWidth = uLight.ClusterParams.x / 16.0;
        float tileHeight = uLight.ClusterParams.y / 8.0;
        uint clusterX = clamp(uint(gl_FragCoord.x / tileWidth), 0u, 15u);
        uint clusterY = clamp(uint(gl_FragCoord.y / tileHeight), 0u, 7u);

        float nearZ = uLight.ClusterParams.z;
        float farZ = 500.0;
        float viewZ = max(vCurrClipPos.w, nearZ);
        float sliceZ = log(viewZ / nearZ) / log(farZ / nearZ);
        uint clusterZ = clamp(uint(sliceZ * 24.0), 0u, 23u);

        uint clusterIndex = clusterX + clusterY * 16u + clusterZ * 128u;
        ClusterRecord rec = uClusterRecords.clusterRecords[clusterIndex];

        for (uint i = 0u; i < rec.Count; ++i) {
            uint lightIndex = uLightIndices.lightIndices[rec.Offset + i];
            if ((lightIndex & 0x80000000u) != 0u) {
                // Spot Light
                uint spotIdx = lightIndex & 0x7FFFFFFFu;
                SpotLight sl = uSpotLights.spotLights[spotIdx];
                vec3 toLight = sl.Position - vWorldPos;
                float dist = length(toLight);
                if (dist < sl.Range) {
                    vec3 dir = toLight / dist;
                    float NdotL_local = max(dot(N, dir), 0.0);
                    if (NdotL_local > 0.0) {
                        float spotDot = dot(-dir, sl.Direction);
                        float spotFactor = clamp((spotDot - sl.CosOuter) / max(sl.CosInner - sl.CosOuter, 0.0001), 0.0, 1.0);
                        spotFactor = spotFactor * spotFactor * (3.0 - 2.0 * spotFactor); // smooth cone edge
                        float att = clamp(1.0 - (dist / sl.Range), 0.0, 1.0);
                        att *= att * spotFactor;
                        vec3 brdfLocal = EvaluateLocalBrdf(N, V, dir, NdotL_local, albedo.rgb, metallic, roughness, F0, energyComp);
                        localLighting += brdfLocal * sl.Color * (att * 5.0) * NdotL_local;
                    }
                }
            } else {
                // Point Light
                PointLight pl = uPointLights.pointLights[lightIndex];
                vec3 toLight = pl.Position - vWorldPos;
                float dist = length(toLight);
                if (dist < pl.Radius) {
                    vec3 dir = toLight / dist;
                    float NdotL_local = max(dot(N, dir), 0.0);
                    if (NdotL_local > 0.0) {
                        float att = clamp(1.0 - (dist / pl.Radius), 0.0, 1.0);
                        att *= att;
                        vec3 brdfLocal = EvaluateLocalBrdf(N, V, dir, NdotL_local, albedo.rgb, metallic, roughness, F0, energyComp);
                        localLighting += brdfLocal * pl.Color * (pl.Intensity * 0.000025 * att) * NdotL_local;
                    }
                }
            }
        }
    }

    // Ambient: split-sum diffuse irradiance and roughness-prefiltered environment reflections.
    float skyFactor = clamp(N.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 ambientColor = mix(uLight.AmbientGroundColor, uLight.AmbientSkyColor, skyFactor);
    float specAo = SpecularOcclusion(NdotV, ao, roughness);
    vec3 ambientDiffuse;
    vec3 ambientSpecular;
    vec3 ambientLighting;
    if (hasIbl) {
        vec3 irradiance = texture(uIrradianceMap, N).rgb;
        vec3 reflected = reflect(-V, N);
        vec3 prefiltered = textureLod(uPrefilterMap, vec3(reflected.x, -reflected.y, reflected.z), roughness * 4.0).rgb;
        vec3 fresnelRoughness = F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(1.0 - NdotV, 5.0);
        ambientDiffuse = (irradiance / PI) * albedo.rgb * (1.0 - metallic);
        ambientSpecular = prefiltered * (fresnelRoughness * brdf.x + brdf.y);
        ambientLighting = (ambientDiffuse * ao + ambientSpecular) * uLight.AmbientIntensity;
    } else {
        ambientDiffuse = ambientColor * albedo.rgb * (1.0 - metallic);
        ambientSpecular = ambientColor * (F0 * brdf.x + brdf.y);
        ambientLighting = (ambientDiffuse * ao + ambientSpecular * specAo) * uLight.AmbientIntensity;
    }

    // Emissive contribution
    vec3 emissive = mat.EmissiveFactor * mat.EmissiveIntensity;
    if (mat.EmissiveIndex != 0xFFFFFFFFu) {
        emissive *= texture(uTextures[nonuniformEXT(mat.EmissiveIndex)], vTexCoord).rgb;
    }

    vec3 finalColor = directLighting + localLighting + ambientLighting + emissive;

    outColor = vec4(finalColor, albedo.a);
}
