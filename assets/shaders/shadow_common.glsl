// ==================================================================================================
// Jatayu Engine - Shared Directional Shadow Evaluation
// File: shadow_common.glsl
// Role: Single source of truth for the light-environment UBO, the cascaded shadow map samplers and
//       the directional shadow filter used by pbr.frag and terrain.frag (Sprint 8 lighting pass).
//
// Techniques:
//   - Normal-offset receiver positioning (texel-scaled, grazing-angle weighted)
//   - Receiver-plane depth bias (per-tap exact plane depth from screen-space derivatives)
//   - Cascade blending band + smooth fade-out at the maximum shadow distance
//   - Contact-hardening soft shadows (PCSS): blocker search + variable-radius PCF (High / Ultra)
//   - Vogel-disk taps rotated per pixel with interleaved gradient noise (no fixed-pattern banding)
//   - Soft terminator so the geometric shadow edge never cuts the lit side in a hard line
//
// Requirements of the including shader:
//   - GLSL 450 or newer, fragment stage.
//   - Descriptor set 0 layout of ForwardRenderer3D (bindings 0, 1 and 11 are declared here).
//   - Call EvaluateDirectionalShadow() from uniform control flow; derivatives (dFdx/dFdy of the
//     world position) must be computed by the caller before any discard.
// Note: the engine's ShaderCompiler inlines includes textually and does not resolve nested includes.
// ==================================================================================================

#ifndef SHADOW_COMMON_GLSL
#define SHADOW_COMMON_GLSL

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
    vec4 ShadowParams;   // x = resolution, y = 1 / resolution, z = softness / filter radius, w = debug mode
    vec4 ClusterParams;
} uLight;

// Hardware depth-compare sampler (Reverse-Z, GREATER_OR_EQUAL) used for PCF taps.
layout(set = 0, binding = 1) uniform sampler2DArrayShadow uShadowMapArray;

// Same depth image, point sampled without comparison: used only for the PCSS blocker search.
layout(set = 0, binding = 11) uniform sampler2DArray uShadowDepthArray;

// --------------------------------------------------------------------------------------------------
// Tunables
// --------------------------------------------------------------------------------------------------
const float SHADOW_NORMAL_OFFSET_TEXELS = 1.25;  // receiver push along the geometric normal
const float SHADOW_CONST_BIAS_TEXELS    = 0.35;  // constant slack along the light direction
const float SHADOW_MAX_BIAS_TEXELS      = 4.0;   // clamp for receiver-plane / slope bias
const float SHADOW_BLEND_BAND           = 0.12;  // fraction of a cascade's depth range used for blending
const float SHADOW_SUN_TAN              = 0.019; // tan of the (artistically widened) sun angular radius
const float SHADOW_SEARCH_TEXELS        = 8.0;   // PCSS blocker search radius (texels)
const float SHADOW_MAX_FILTER_TEXELS    = 8.0;   // PCSS maximum penumbra radius (texels, <= search)
const int   SHADOW_SEARCH_TAPS          = 12;
const int   SHADOW_FILTER_TAPS          = 16;

// --------------------------------------------------------------------------------------------------
// Helpers
// --------------------------------------------------------------------------------------------------
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

// Interleaved gradient noise (Jimenez 2014): cheap, well distributed per-pixel rotation.
float ShadowInterleavedGradientNoise(vec2 screenPos) {
    return fract(52.9829189 * fract(dot(screenPos, vec2(0.06711056, 0.00583715))));
}

const vec2 VOGEL_DISK_12[12] = vec2[](
    vec2(  0.20412415,   0.00000000),
    vec2( -0.26069927,   0.23882188),
    vec2(  0.03990420,  -0.45468779),
    vec2(  0.32859454,   0.42859339),
    vec2( -0.60301140,  -0.10666423),
    vec2(  0.57122504,  -0.36336661),
    vec2( -0.19106360,   0.71074705),
    vec2( -0.36437900,  -0.70158959),
    vec2(  0.79055667,   0.28871003),
    vec2( -0.82244249,   0.33949230),
    vec2(  0.39647163,  -0.84723683),
    vec2(  0.29298244,   0.93407421)
);

const vec2 VOGEL_DISK_16[16] = vec2[](
    vec2(  0.17677670,   0.00000000),
    vec2( -0.22577219,   0.20682582),
    vec2(  0.03455805,  -0.39377118),
    vec2(  0.28457122,   0.37117276),
    vec2( -0.52222319,  -0.09237393),
    vec2(  0.49469539,  -0.31468471),
    vec2( -0.16546593,   0.61552500),
    vec2( -0.31556147,  -0.60759440),
    vec2(  0.68464216,   0.25003022),
    vec2( -0.71225609,   0.29400896),
    vec2(  0.34335450,  -0.73372862),
    vec2(  0.25373024,   0.80893199),
    vec2( -0.76474589,  -0.44318588),
    vec2(  0.89713398,  -0.19723239),
    vec2( -0.54750691,   0.77877223),
    vec2( -0.12648677,  -0.97608970)
);

// Rotate precomputed disk sample by (cos(phi), sin(phi)) using 2D complex multiplication
vec2 ShadowRotateDisk(vec2 pt, vec2 rot) {
    return vec2(pt.x * rot.x - pt.y * rot.y, pt.x * rot.y + pt.y * rot.x);
}

// Vogel (golden-angle) disk fallback
vec2 ShadowVogelDisk(int index, int count, float phi) {
    float r = sqrt((float(index) + 0.5) / float(count));
    float theta = float(index) * 2.39996323 + phi;
    return r * vec2(cos(theta), sin(theta));
}

mat4 ShadowCascadeMatrix(int c) {
    if (c == 0) return uLight.CascadeViewProj0;
    if (c == 1) return uLight.CascadeViewProj1;
    if (c == 2) return uLight.CascadeViewProj2;
    return uLight.CascadeViewProj3;
}

float ShadowCascadeFar(int c) {
    if (c == 0) return uLight.CascadeSplits.x;
    if (c == 1) return uLight.CascadeSplits.y;
    if (c == 2) return uLight.CascadeSplits.z;
    return uLight.CascadeSplits.w;
}

float ShadowCascadeNear(int c) {
    return c == 0 ? 0.0 : ShadowCascadeFar(c - 1);
}

// The cascade projection is orthographic, so its scales give exact world-space metrics:
//   clip.x = (2 / extent) * lightViewX   ->  metres per texel = extent / resolution
float ShadowTexelWorldSize(mat4 vp) {
    float ndcPerMetre = length(vec3(vp[0][0], vp[1][0], vp[2][0]));
    return 2.0 / (max(ndcPerMetre, 1e-6) * uLight.ShadowParams.x);
}

// Reverse-Z depth change per metre travelled along the light direction (= 1 / (far - near)).
float ShadowDepthPerMetre(mat4 vp) {
    return max(length(vec3(vp[0][2], vp[1][2], vp[2][2])), 1e-8);
}

// --------------------------------------------------------------------------------------------------
// Single cascade evaluation
// --------------------------------------------------------------------------------------------------
// Returns the lit fraction in [0, 1] (debug modes return their visualisation value instead).
// 'inside' is false when the receiver falls outside the cascade's shadow map.
float ShadowEvaluateCascade(
    int cascade,
    vec3 worldPos,
    vec3 geomNormal,
    float geomNdotL,
    vec2 screenPos,
    vec3 dPdx,
    vec3 dPdy,
    out bool inside)
{
    mat4 vp = ShadowCascadeMatrix(cascade);
    float texelWorld = ShadowTexelWorldSize(vp);
    float depthPerMetre = ShadowDepthPerMetre(vp);

    // 1. Normal-offset: slide the receiver off the surface proportionally to the texel footprint.
    // Ensure a baseline offset even at perpendicular incidence (geomNdotL == 1) to eliminate acne on flat faces.
    float sinTheta = sqrt(clamp(1.0 - geomNdotL * geomNdotL, 0.0, 1.0));
    float normalScale = clamp(sinTheta, 0.35, 1.0);
    vec3 offsetPos = worldPos + geomNormal * (texelWorld * SHADOW_NORMAL_OFFSET_TEXELS * normalScale);

    vec4 clip = vp * vec4(offsetPos, 1.0);
    vec3 uvz = clip.xyz / clip.w;
    uvz.xy = uvz.xy * 0.5 + 0.5;

    // In Reverse-Z the valid depth range is [0, 1]. Keep a 1-texel guard band at the map edges.
    float guard = uLight.ShadowParams.y;
    inside = uvz.x >= guard && uvz.x <= 1.0 - guard &&
             uvz.y >= guard && uvz.y <= 1.0 - guard &&
             uvz.z >= 0.0 && uvz.z <= 1.0;
    if (!inside) {
        return 1.0;
    }

    // 2. Receiver-plane depth gradient d(depth)/d(uv) from the (affine) cascade transform.
    vec3 ddx = mat3(vp) * dPdx;
    vec3 ddy = mat3(vp) * dPdy;
    ddx.xy *= 0.5;
    ddy.xy *= 0.5;
    float det = ddx.x * ddy.y - ddx.y * ddy.x;

    float texelDepth = texelWorld * depthPerMetre;               // depth change over one texel of travel
    float maxBias = texelDepth * SHADOW_MAX_BIAS_TEXELS;
    float texelUv = uLight.ShadowParams.y;
    vec2 gradZ = vec2(0.0);
    if (abs(det) > 1e-6) {
        gradZ = vec2(ddy.y * ddx.z - ddx.y * ddy.z,
                     ddx.x * ddy.z - ddy.x * ddx.z) / det;
        float maxGrad = maxBias / max(texelUv, 1e-6);
        gradZ = clamp(gradZ, -maxGrad, maxGrad);
    }

    float constBias = texelDepth * max(SHADOW_CONST_BIAS_TEXELS, 0.5) + uLight.ShadowBias * 0.5;
    float slopeBias = min(0.5 * texelUv * (abs(gradZ.x) + abs(gradZ.y)), maxBias);
    float grazingBias = (1.0 - clamp(geomNdotL, 0.0, 1.0)) * texelDepth * 1.5;
    float baseBias = constBias + slopeBias + grazingBias;

    uint debugMode = uint(round(uLight.ShadowParams.w));
    if (debugMode == 2u) return uvz.z;
    if (debugMode == 3u) return clamp(baseBias / max(maxBias, 1e-8), 0.0, 1.0);

    uint shadowMode = uLight.CastsShadows & 3u;
    float layer = float(cascade);
    float noise = ShadowInterleavedGradientNoise(screenPos);
    float phi = noise * 6.28318530718;

    // 3a. Standard quality or distant cascades (>= 1): 3x3 PCF with exact per-tap receiver-plane depth.
    // Clamping planeBias to [-slopeBias, slopeBias] guarantees (planeBias + baseBias) >= constBias > 0.
    if (shadowMode < 2u || cascade >= 1) {
        float sum = 0.0;
        for (int x = -1; x <= 1; ++x) {
            for (int y = -1; y <= 1; ++y) {
                vec2 off = vec2(float(x), float(y)) * texelUv;
                float planeBias = clamp(dot(off, gradZ), -slopeBias, slopeBias);
                sum += texture(uShadowMapArray, vec4(uvz.xy + off, layer, uvz.z + planeBias + baseBias));
            }
        }
        return sum / 9.0;
    }

    vec2 rot = vec2(cos(phi), sin(phi));

    // 3b. High / Ultra: PCSS. Blocker search first (also gives a cheap early-out for fully lit pixels).
    float searchUv = SHADOW_SEARCH_TEXELS * texelUv;
    float blockerSum = 0.0;
    int blockerCount = 0;
    for (int i = 0; i < SHADOW_SEARCH_TAPS; ++i) {
        vec2 off = ShadowRotateDisk(VOGEL_DISK_12[i], rot) * searchUv;
        float planeBias = clamp(dot(off, gradZ), -slopeBias, slopeBias);
        float stored = texture(uShadowDepthArray, vec3(uvz.xy + off, layer)).r;
        float reference = uvz.z + planeBias + baseBias;
        if (stored > reference) {          // Reverse-Z: larger depth = closer to the light
            blockerSum += stored;
            blockerCount++;
        }
    }

    if (blockerCount == 0) {
        return 1.0;                        // no occluder anywhere near this pixel
    }

    // Penumbra width from similar triangles: receiver-blocker distance (metres) x sun angular size.
    float avgBlocker = blockerSum / float(blockerCount);

    // Full umbra early out: if all search taps were solid blockers well above bias, pixel is completely shadowed.
    if (blockerCount == SHADOW_SEARCH_TAPS && (avgBlocker - uvz.z) > baseBias * 2.5) {
        return 0.0;
    }

    float blockerDistM = max(avgBlocker - uvz.z, 0.0) / depthPerMetre;
    float softness = max(uLight.ShadowParams.z, 0.5);
    float radiusTexels = clamp(blockerDistM * SHADOW_SUN_TAN * softness / texelWorld,
                                1.0 + 0.25 * softness, SHADOW_MAX_FILTER_TEXELS);
    float radiusUv = radiusTexels * texelUv;

    // Adaptive filter taps: narrow penumbras (contact shadows) need only 8 taps; wide penumbras use 16.
    int filterTaps = radiusTexels <= 2.2 ? 8 : SHADOW_FILTER_TAPS;
    float lit = 0.0;
    for (int i = 0; i < filterTaps; ++i) {
        vec2 off = ShadowRotateDisk(VOGEL_DISK_16[i], rot) * radiusUv;
        float planeBias = clamp(dot(off, gradZ), -slopeBias, slopeBias);
        lit += texture(uShadowMapArray, vec4(uvz.xy + off, layer, uvz.z + planeBias + baseBias));
    }
    return lit / float(filterTaps);
}

// --------------------------------------------------------------------------------------------------
// Public entry point
// --------------------------------------------------------------------------------------------------
// worldPos / dPdx / dPdy : camera-relative position of the receiver and its screen-space derivatives
// geomNormal             : interpolated geometric (vertex) normal, NOT the normal-mapped normal
// viewDepth              : positive view-space depth (clip.w)
float EvaluateDirectionalShadow(
    vec3 worldPos,
    vec3 geomNormal,
    float viewDepth,
    vec2 screenPos,
    vec3 dPdx,
    vec3 dPdy)
{
    uint shadowMode = uLight.CastsShadows & 3u;
    uint debugMode = uint(round(uLight.ShadowParams.w));

    vec3 L = normalize(-uLight.SunDirection);
    vec3 Ng = normalize(geomNormal);
    float geomNdotL = dot(Ng, L);

    // Fast backface early out: surfaces facing away from the sun are 100% shadowed by geometry,
    // and smoothstep(0.02, 0.12, geomNdotL) will zero out any shadow contribution anyway.
    if (debugMode == 0u) {
        if (shadowMode == 0u) {
            return geomNdotL <= 0.02 ? 0.0 : 1.0;
        }
        if (geomNdotL <= 0.02) {
            return 0.0;
        }
    }

    // Beyond the last cascade everything is unshadowed.
    if (viewDepth > uLight.CascadeSplits.w) {
        return 1.0;
    }

    int firstCascade = 0;
    if (viewDepth > uLight.CascadeSplits.x) firstCascade = 1;
    if (viewDepth > uLight.CascadeSplits.y) firstCascade = 2;
    if (viewDepth > uLight.CascadeSplits.z) firstCascade = 3;

    // Use the tightest cascade that actually contains the receiver (fall back outwards).
    int used = firstCascade;
    bool inside = false;
    float shadow = ShadowEvaluateCascade(used, worldPos, Ng, geomNdotL, screenPos, dPdx, dPdy, inside);
    for (int c = firstCascade + 1; c < 4 && !inside; ++c) {
        used = c;
        shadow = ShadowEvaluateCascade(used, worldPos, Ng, geomNdotL, screenPos, dPdx, dPdy, inside);
    }
    if (!inside) {
        return 1.0;
    }

    if (debugMode == 1u) return (float(used) + 0.5) * 0.25;
    if (debugMode != 0u) return shadow;

    // Cascade blending band (and distance fade for the last cascade).
    float cascadeFar = ShadowCascadeFar(used);
    float band = max(SHADOW_BLEND_BAND * (cascadeFar - ShadowCascadeNear(used)), 0.001);
    float t = smoothstep(cascadeFar - band, cascadeFar, viewDepth);
    if (t > 0.0) {
        if (used < 3) {
            bool insideNext = false;
            float nextShadow = ShadowEvaluateCascade(used + 1, worldPos, Ng, geomNdotL, screenPos, dPdx, dPdy, insideNext);
            if (insideNext) {
                shadow = mix(shadow, nextShadow, t);
            }
        } else {
            shadow = mix(shadow, 1.0, t);
        }
    }

    // Soft terminator: avoids a hard geometric cut-off line where the surface turns away from the sun.
    shadow *= smoothstep(0.02, 0.12, geomNdotL);
    return shadow;
}

#endif // SHADOW_COMMON_GLSL
