#version 460 core

// ==================================================================================================
// Jatayu Engine - GPU Particle Billboard Rendering
// Shader: particle.vert
// Role: Expands particle instance into camera-facing billboard quads in view space.
// ==================================================================================================

layout(location = 0) in vec2 inQuadPos; // (-1..1, -1..1)

struct Particle {
    vec4 PositionAge;
    vec4 VelocityLifetime;
    vec4 Color;
    vec4 SizePadding;
};

layout(std430, set = 0, binding = 0) readonly buffer ParticleBuffer {
    Particle particles[];
};

layout(push_constant) uniform ParticleRenderPushConstants {
    mat4 View;
    mat4 Proj;
    vec4 ScreenParams; // X=Width, Y=Height, Z=NearZ, W=SoftFadeDist
} pc;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vTexCoord;
layout(location = 2) out float vViewZ;

void main() {
    Particle p = particles[gl_InstanceIndex];

    // If particle is expired or dead, clip immediately to prevent degenerate primitives
    float activeScale = (p.PositionAge.w < p.VelocityLifetime.w && p.VelocityLifetime.w > 0.0) ? p.SizePadding.x : 0.0;
    if (activeScale <= 0.0001) {
        gl_Position = vec4(0.0, 0.0, 2.0, 1.0); // Outside Vulkan [0, 1] Reverse-Z depth range
        vColor = vec4(0.0);
        vTexCoord = vec2(0.0);
        vViewZ = 0.0;
        return;
    }

    // View-space spherical billboarding
    vec4 posView = pc.View * vec4(p.PositionAge.xyz, 1.0);
    posView.xy += inQuadPos * activeScale;

    vColor = p.Color;
    vTexCoord = inQuadPos * 0.5 + 0.5;
    vViewZ = -posView.z;

    gl_Position = pc.Proj * posView;
}
