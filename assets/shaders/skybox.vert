#version 460 core

layout(push_constant) uniform SkyboxPushConstants {
    mat4 invViewProj;
} pc;

layout(location = 0) out vec3 vRayDir;

void main() {
    // Generate fullscreen triangle covering NDC [-1, 1]
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vec4 clipPos = vec4(uv * 2.0 - 1.0, 0.0, 1.0); // Z = 0.0 (Reverse-Z far infinity!)
    gl_Position = clipPos;

    // Unproject to world-space direction vector
    vec4 worldPos = pc.invViewProj * clipPos;
    vRayDir = worldPos.xyz / worldPos.w;
}
