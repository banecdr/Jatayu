#version 460 core

layout(push_constant) uniform SkyboxPushConstants {
    mat4 invViewProj;
} pc;

layout(location = 0) out vec3 vRayDir;

void main() {
    // Generate fullscreen triangle covering NDC [-1, 1]
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vec2 ndc = uv * 2.0 - 1.0;

    // Reverse-Z infinity depth for rasterizer (Z = 0.0)
    gl_Position = vec4(ndc, 0.0, 1.0);

    // Unproject near plane point (Z = 1.0) to avoid division by w=0 at infinity
    vec4 worldPos = pc.invViewProj * vec4(ndc, 1.0, 1.0);
    vRayDir = worldPos.xyz / worldPos.w;
}
