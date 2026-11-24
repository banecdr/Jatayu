#version 450
/* =================================================================================================
 * FILE: terrain.vert
 * LOCATION: assets/shaders/terrain.vert
 * 
 * WHAT THIS FILE DOES:
 *   Geomorphing vertex stage for TerrainClipmap pieces (D22).
 *   Port of Terrain3D main.glsl vertex() with discrete snapping and geomorphing.
 *   - Transforms clipmap concentric ring pieces snapped to 2^(L+1) * spacing.
 *   - Geomorphs outer band vertices towards coarser grid positions to prevent T-junction cracks.
 *   - Evaluates multi-frequency procedural elevation and central-difference normals.
 *   - Outputs camera-relative positions, normals, and clip positions to terrain.frag.
 * ================================================================================================= */

layout(location = 0) in vec3 inPosition;   // integer grid units (x, 0, z); piece grows along +X/+Z
layout(location = 1) in vec3 inNormal;     // unused (flat)
layout(location = 2) in vec2 inTexCoord;   // unused

layout(push_constant) uniform PushConstants
{
    mat4 viewProj;
    vec4 originScale;  // xyz = piece origin relative to camera (y = -camera.y), w = scale (m per grid unit)
    vec4 params;       // x,y = piece world origin XZ, z = mesh size (negative if debug rings active), w = LOD index
} pc;

layout(location = 0) out vec3  outWorldPos;
layout(location = 1) out vec3  outNormal;
layout(location = 2) out vec2  outTexCoord;
layout(location = 3) out float outMorphFactor;
layout(location = 4) out float outRingIndex;
layout(location = 5) out vec4  outClipPos;

float evaluateElevation(vec2 w)
{
    const float maxElevation = 45.0;
    float h1 = sin(w.x * 0.005) * cos(w.y * 0.005) * maxElevation;
    float h2 = sin(w.x * 0.015 + 1.2) * sin(w.y * 0.015 + 0.8) * (maxElevation * 0.35);
    float h3 = cos(w.x * 0.04 - 0.5) * sin(w.y * 0.04 + 1.7) * (maxElevation * 0.1);

    // Flat clearing around origin (radius 200m) for testing arena and rotating cubes
    float dist = length(w);
    float flatFactor = smoothstep(200.0, 260.0, dist);
    return (h1 + h2 + h3) * flatFactor;
}

void main()
{
    float scale    = pc.originScale.w;
    float invScale = 1.0 / scale;
    float meshSize = abs(pc.params.z);
    bool  isLod0   = pc.params.w < 0.5;

    vec2 localXZ = inPosition.xz;

    // Camera-relative vertex position. The clipmap target IS the camera, so the target is the origin
    // and the morph distance is just |rel|.
    vec2 relXZ   = pc.originScale.xz + localXZ * scale;
    vec2 worldXZ = pc.params.xy      + localXZ * scale;

    // Position of this vertex on the world-aligned grid of this ring, mod 4. Piece origins are always
    // integer multiples of the ring scale, so this is exact.
    vec2 phase = mod(round(pc.params.xy * invScale), 4.0);
    vec2 g     = localXZ + phase;

    // Geomorph factor: 0 inside the ring, ramps to 1 across the outer band so the vertex lands on the
    // next coarser ring's grid exactly at the ring boundary.
    float maxXZ = max(abs(relXZ.x), abs(relXZ.y));
    float morph = smoothstep(0.0, 1.0, (maxXZ * invScale - meshSize - 4.0) / (meshSize - 4.0));

    // Which neighbour vertex to slide towards. The snapped grid has odd vertices which don't exist on
    // the coarser ring; shift moves them to the nearest even one.
    vec2 vfract = fract(localXZ * 0.5) * 2.0;
    vec2 shift;
    if (isLod0)
    {
        // LOD0 uses a uniform diagonal grid: morph regular -> alternating so it aligns with LOD1+
        float flip = round(fract(round(mod(g.y, 4.0)) * round(mod(g.x, 4.0)) * 0.25));
        shift = mix(vfract, vec2(vfract.x, -vfract.y), flip);
    }
    else
    {
        shift = vfract * round((fract(g * 0.25) - 0.5) * 4.0);
    }

    // Linear height interpolation between this vertex and its coarse-grid target, same factor as XZ
    vec2 startW = worldXZ;
    vec2 endW   = worldXZ - shift * scale;
    float h = mix(evaluateElevation(startW), evaluateElevation(endW), morph);

    relXZ   -= shift * scale * morph;
    worldXZ -= shift * scale * morph;

    vec3 rel = vec3(relXZ.x, pc.originScale.y + h, relXZ.y);

    // Central-difference normal at the morphed position, step = this ring's vertex spacing
    float e  = scale;
    float hL = evaluateElevation(worldXZ - vec2(e, 0.0));
    float hR = evaluateElevation(worldXZ + vec2(e, 0.0));
    float hD = evaluateElevation(worldXZ - vec2(0.0, e));
    float hU = evaluateElevation(worldXZ + vec2(0.0, e));

    outWorldPos    = vec3(relXZ.x, pc.originScale.y + h, relXZ.y);
    outNormal      = normalize(vec3(hL - hR, 2.0 * e, hD - hU));
    outTexCoord    = worldXZ;
    outMorphFactor = morph;
    outRingIndex   = pc.params.z < 0.0 ? pc.params.w : -1.0;

    vec4 clipPos   = pc.viewProj * vec4(rel, 1.0);
    gl_Position    = clipPos;
    outClipPos     = clipPos;
}
