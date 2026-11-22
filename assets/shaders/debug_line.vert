#version 460 core

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColor;

layout(push_constant) uniform DebugLinePushConstants {
    mat4 viewProjMatrix;
} pc;

layout(location = 0) out vec4 vColor;

void main() {
    gl_Position = pc.viewProjMatrix * vec4(inPosition, 1.0);
    vColor = inColor;
}
