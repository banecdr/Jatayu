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
    vec4 CascadeSplits;
    mat4 CascadeViewProj0;
    mat4 CascadeViewProj1;
    mat4 CascadeViewProj2;
    mat4 CascadeViewProj3;
    vec4 ShadowParams;
    vec4 ClusterParams;
} uLight;

layout(set = 0, binding = 1) uniform sampler2DArrayShadow uShadowMapArray;

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

// 16-sample Poisson disk distribution for ultra-smooth soft shadows
const vec2 POISSON_DISK[16] = vec2[](
    vec2(-0.94201624, -0.39906216),
    vec2( 0.94558609, -0.76890725),
    vec2(-0.09418410, -0.92938870),
    vec2( 0.34495938,  0.29387760),
    vec2(-0.91588581,  0.45771432),
    vec2(-0.81544232, -0.87912464),
    vec2(-0.38277543,  0.27676845),
    vec2( 0.97484398,  0.75648377),
    vec2( 0.44323325, -0.97511554),
    vec2( 0.53742981, -0.47373420),
    vec2(-0.26496911, -0.41893023),
    vec2( 0.79197514,  0.19090143),
    vec2(-0.24188840,  0.99706507),
    vec2(-0.81409955,  0.91437590),
    vec2( 0.19984126,  0.78641367),
    vec2( 0.14383161, -0.14100790)
);

// Directional shadow map evaluation with 4-Cascade CSM, Reverse-Z comparison and 16-tap Poisson or 3x3 PCF
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

float EvaluateShadow(vec3 worldPos, float viewDepth, float bias, float NdotL) {
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

    // Beyond max shadow distance -> fully unshadowed
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

        vec4 shadowClip = cascadeVP * vec4(worldPos, 1.0);
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

    // If completely outside all cascades -> no shadow
    if (selectedCascade == -1) {
        return 1.0;
    }

    // In Reverse-Z, closer to light is higher depth value (1.0 = near, 0.0 = far).
    // Slope-scaled tangent bias pushes the reference depth closer to the light.
    float cascadeScale = float(selectedCascade + 1);
    float tanTheta = sqrt(clamp(1.0 - NdotL * NdotL, 0.0, 1.0)) / max(NdotL, 0.05);
    float dynamicBias = min(max(bias, 0.0) * cascadeScale * (1.0 + min(tanTheta, 4.0) * 0.25), 0.002);
    if (debugMode == 2u) return projCoords.z;
    if (debugMode == 3u) return clamp(dynamicBias / 0.002, 0.0, 1.0);
    if (debugMode == 1u) return (float(selectedCascade) + 0.5) * 0.25;
    float currentDepth = projCoords.z + dynamicBias;

    vec2 texelSize = vec2(uLight.ShadowParams.y);
    float shadow = 0.0;
    float layer = float(selectedCascade);

    if (shadowMode >= 2u) {
        // Ultra High-Quality Mode: 16-tap Poisson Disk filter (cascade-scaled to maintain crisp distant shadows)
        float filterRadius = max(uLight.ShadowParams.z / sqrt(cascadeScale), 1.0);
        for (int i = 0; i < 16; ++i) {
            vec2 offset = POISSON_DISK[i] * (texelSize * filterRadius);
            shadow += texture(uShadowMapArray, vec4(projCoords.xy + offset, layer, currentDepth));
        }
        return shadow / 16.0;
    } else {
        // Standard Mode: 9-tap 3x3 uniform PCF
        for (int x = -1; x <= 1; ++x) {
            for (int y = -1; y <= 1; ++y) {
                vec2 offset = vec2(x, y) * texelSize;
                shadow += texture(uShadowMapArray, vec4(projCoords.xy + offset, layer, currentDepth));
            }
        }
        return shadow / 9.0;
    }
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
    float shadow = EvaluateShadow(vWorldPos, vCurrClipPos.w, uLight.ShadowBias, NdotL);
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
                        float att = clamp(1.0 - (dist / sl.Range), 0.0, 1.0);
                        att *= att * spotFactor;
                        vec3 H_local = normalize(V + dir);
                        float NDF_local = DistributionGGX(N, H_local, roughness);
                        float G_local = GeometrySmith(N, V, dir, roughness);
                        vec3 F_local = FresnelSchlick(max(dot(H_local, V), 0.0), F0);
                        vec3 spec = (NDF_local * G_local * F_local) / (4.0 * max(dot(N, V), 0.0) * NdotL_local + 0.0001);
                        vec3 diff = (vec3(1.0) - F_local) * (1.0 - metallic) * albedo.rgb;
                        localLighting += (diff + spec * PI) * sl.Color * (att * 5.0) * NdotL_local;
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
                        vec3 H_local = normalize(V + dir);
                        float NDF_local = DistributionGGX(N, H_local, roughness);
                        float G_local = GeometrySmith(N, V, dir, roughness);
                        vec3 F_local = FresnelSchlick(max(dot(H_local, V), 0.0), F0);
                        vec3 spec = (NDF_local * G_local * F_local) / (4.0 * max(dot(N, V), 0.0) * NdotL_local + 0.0001);
                        vec3 diff = (vec3(1.0) - F_local) * (1.0 - metallic) * albedo.rgb;
                        localLighting += (diff + spec * PI) * pl.Color * (pl.Intensity * 0.000025 * att) * NdotL_local;
                    }
                }
            }
        }
    }

    // Split-sum diffuse irradiance and roughness-prefiltered environment reflections.
    vec2 brdf = texture(uBrdfLut, vec2(max(dot(N, V), 0.0), roughness)).rg;
    float skyFactor = clamp(N.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 ambientColor = mix(uLight.AmbientGroundColor, uLight.AmbientSkyColor, skyFactor);
    vec3 ambientDiffuse;
    vec3 ambientSpecular;
    vec3 ambientLighting;
    if ((uLight.CastsShadows & 0x80000000u) != 0u) {
        vec3 irradiance = texture(uIrradianceMap, N).rgb;
        vec3 reflected = reflect(-V, N);
        vec3 prefiltered = textureLod(uPrefilterMap, vec3(reflected.x, -reflected.y, reflected.z), roughness * 4.0).rgb;
        vec3 fresnelRoughness = F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(1.0 - max(dot(N, V), 0.0), 5.0);
        ambientDiffuse = irradiance * albedo.rgb * (1.0 - metallic);
        ambientSpecular = prefiltered * (fresnelRoughness * brdf.x + brdf.y);
        ambientLighting = (ambientDiffuse * uLight.AmbientIntensity + ambientSpecular) * ao;
    } else {
        ambientDiffuse = ambientColor * albedo.rgb * (1.0 - metallic);
        ambientSpecular = ambientColor * (F0 * brdf.x + brdf.y);
        ambientLighting = (ambientDiffuse + ambientSpecular) * uLight.AmbientIntensity * ao;
    }

    // Emissive contribution
    vec3 emissive = mat.EmissiveFactor * mat.EmissiveIntensity;
    if (mat.EmissiveIndex != 0xFFFFFFFFu) {
        emissive *= texture(uTextures[nonuniformEXT(mat.EmissiveIndex)], vTexCoord).rgb;
    }

    vec3 finalColor = directLighting + localLighting + ambientLighting + emissive;

    // ACES Filmic Tone Mapping (Krzysztof Narkowicz fit) + sRGB Gamma Correction for SDR UNorm framebuffer
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    vec3 mapped = clamp((finalColor * (a * finalColor + b)) / (finalColor * (c * finalColor + d) + e), 0.0, 1.0);
    vec3 srgb = pow(max(mapped, vec3(0.0)), vec3(1.0 / 2.2));

    outColor = vec4(srgb, albedo.a);
}
