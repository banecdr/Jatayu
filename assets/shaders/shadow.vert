#version 460 core

layout(location = 0) in vec3 inPosition;

layout(push_constant) uniform ShadowPushConstants {
    mat4 lightViewProj;
    mat4 modelMatrix;
} pc;

void main() {
    gl_Position = pc.lightViewProj * pc.modelMatrix * vec4(inPosition, 1.0);
}
