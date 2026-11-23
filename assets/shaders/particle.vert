#version 460 core

// ==================================================================================================
// Jatayu Engine - GPU Particle Billboard Rendering
// Shader: particle.vert
// Role: Expands particle instance into camera-facing billboard quads in view space.
// ==================================================================================================

layout(location = 0) in vec2 inQuadPos; // (-1..1, -1..1)

struct Particle {
    vec3 Position;
    float Age;
    vec3 Velocity;
    float Lifetime;
    vec4 Color;
    float Size;
    float Padding0;
    float Padding1;
    float Padding2;
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

    // If particle is expired or dead, scale to zero
    float activeScale = (p.Age < p.Lifetime && p.Lifetime > 0.0) ? p.Size : 0.0;

    // View-space spherical billboarding
    vec4 posView = pc.View * vec4(p.Position, 1.0);
    posView.xy += inQuadPos * activeScale;

    vColor = p.Color;
    vTexCoord = inQuadPos * 0.5 + 0.5;
    vViewZ = -posView.z;

    gl_Position = pc.Proj * posView;
}
