#version 460 core

layout(location = 0) in vec3 vRayDir;
layout(location = 0) out vec4 outColor;

const vec3 ZenithColor = vec3(0.12, 0.32, 0.72);
const vec3 HorizonColor = vec3(0.70, 0.82, 0.95);
const vec3 GroundColor = vec3(0.18, 0.16, 0.14);
const vec3 SunDirection = normalize(vec3(0.5, 1.0, 0.3));
const vec3 SunColor = vec3(1.0, 0.95, 0.85);

void main() {
    vec3 dir = normalize(vRayDir);
    float y = dir.y;

    vec3 skyColor;
    if (y >= 0.0) {
        // Sky hemisphere: blend horizon to zenith
        float t = pow(y, 0.6);
        skyColor = mix(HorizonColor, ZenithColor, t);

        // Sun disc
        float sunDot = max(dot(dir, SunDirection), 0.0);
        float sunDisc = pow(sunDot, 512.0) * 8.0;
        float sunGlow = pow(sunDot, 16.0) * 0.4;
        skyColor += (SunColor * sunDisc) + (HorizonColor * sunGlow);
    } else {
        // Ground hemisphere: blend horizon to ground
        float t = pow(-y, 0.7);
        skyColor = mix(HorizonColor, GroundColor, t);
    }

    outColor = vec4(skyColor, 1.0);
}
