#version 460 core

// ==================================================================================================
// Jatayu Engine - Order-Independent Transparency Compositing
// Shader: transparent_composite.frag
// Role: Fullscreen composite pass blending accumulated transparent color over opaque HDR target.
// ==================================================================================================

layout(location = 0) in vec2 vTexCoord;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D accumTexture;
layout(set = 0, binding = 1) uniform sampler2D revealageTexture;

void main() {
    vec4 accum = texture(accumTexture, vTexCoord);
    float revealage = texture(revealageTexture, vTexCoord).r;

    // If completely opaque or nothing drawn in transparent pass, discard
    if (revealage >= 1.0) discard;

    // WBOIT reconstruction: average color = accum.rgb / max(accum.a, 0.00001)
    vec3 averageColor = accum.rgb / max(accum.a, 0.00001);
    float alpha = 1.0 - revealage;

    outColor = vec4(averageColor, alpha);
}
