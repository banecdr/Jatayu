/* =================================================================================================
 * FILE: cube.vert
 * LOCATION: assets/shaders/cube.vert
 * 
 * WHAT THIS SHADER DOES:
 *   3D Vertex shader for rendering 3D meshes with built-in Panini (Pannini) cylindrical projection.
 *   Receives vertex attributes from the GPU vertex buffer, transforms the position by the 64-byte
 *   Model-View-Projection (MVP) matrix, and applies cylindrical Panini projection when enabled
 *   to eliminate wide-angle peripheral distortion.
 *
 * HOW PANINI PROJECTION WORKS IN THIS SHADER:
 *   1. Standard linear perspective stretches geometry towards the edges of wide-angle screens (FOV > 90°).
 *   2. When uPaniniDistance > 0.0, this shader converts the vertex clip position to Normalized Device
 *      Coordinates (NDC) and applies the TK Sharpless Panini formula:
 *        u = ndc.x * uAspect;
 *        denom = 1.0 + d * sqrt(1.0 + u * u);
 *        factor = ((d + 1.0) / denom) * uPaniniScale;
 *        ndc.x = (u * factor) / uAspect;
 *        ndc.y = ndc.y * factor;
 *   3. Vertical lines remain straight and upright while horizontal coordinates are gently compressed,
 *      eliminating tangential distortion and edge elongation.
 *
 * PUSH CONSTANT MEMORY LAYOUT (80 BYTES TOTAL):
 *   - offset 0  (64 bytes): mat4  uMvp            (Combined Model-View-Projection Matrix)
 *   - offset 64 ( 4 bytes): float uPaniniDistance (0.0 = linear perspective, 0.5 = moderate, 1.0 = full)
 *   - offset 68 ( 4 bytes): float uPaniniScale    (Fit/scale factor, default: 1.0)
 *   - offset 72 ( 4 bytes): float uAspect         (Viewport aspect ratio: Width / Height)
 *   - offset 76 ( 4 bytes): float uPad            (Alignment padding)
 * ================================================================================================= */

#version 450

// Vertex Buffer Attributes
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inColor;
layout(location = 2) in vec3 inNormal;
layout(location = 3) in vec2 inTexCoord;

// Outputs to Fragment Shader
layout(location = 0) out vec3 fragColor;
layout(location = 1) out vec3 fragNormal;
layout(location = 2) out vec2 fragTexCoord;

// 80-byte Push Constants: 64-byte MVP matrix + 16-byte Panini parameters
layout(push_constant) uniform PushConstants {
    mat4 uMvp;
    float uPaniniDistance;
    float uPaniniScale;
    float uAspect;
    float uPad;
};

// TK Sharpless Panini cylindrical projection function
vec2 ApplyPanini(vec2 ndc, float d, float aspect, float scale) {
    if (d <= 0.0001)
        return ndc;

    float u = ndc.x * aspect;
    float v = ndc.y;

    float denom = 1.0 + d * sqrt(1.0 + u * u);
    float factor = ((d + 1.0) / denom) * scale;

    return vec2((u * factor) / aspect, v * factor);
}

void main() {
    vec4 clip = uMvp * vec4(inPosition, 1.0);

    // Apply Panini projection to clip-space coordinates when enabled
    if (uPaniniDistance > 0.0001 && clip.w > 0.0) {
        vec2 ndc = clip.xy / clip.w;
        vec2 paniniNdc = ApplyPanini(ndc, uPaniniDistance, uAspect, uPaniniScale);
        clip.xy = paniniNdc * clip.w;
    }

    gl_Position = clip;
    fragColor = inColor;
    fragNormal = inNormal;
    fragTexCoord = inTexCoord;
}
