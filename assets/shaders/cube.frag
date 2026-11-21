/* =================================================================================================
 * FILE: cube.frag
 * LOCATION: assets/shaders/cube.frag
 * 
 * WHAT THIS SHADER DOES:
 *   3D Fragment (pixel) shader for rendering 3D solid geometry in Vulkan 1.3.
 *   Calculates per-pixel color with directional diffuse lighting based on interpolated surface
 *   normals. This gives the 3D cube rich dimensionality, visible shading gradients, and volume.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Inputs from Vertex Shader:
 *      - location = 0: vec3 fragColor    (Interpolated RGB color from vertex buffer)
 *      - location = 1: vec3 fragNormal   (Interpolated surface normal vector)
 *      - location = 2: vec2 fragTexCoord (Interpolated UV coordinates)
 *
 *   2. Diffuse Directional Lighting:
 *      A simulated key light direction (vec3(0.5, 1.0, 0.7)) shines down on the 3D scene.
 *      The dot product max(dot(N, L), 0.0) calculates Lambertian diffuse reflection.
 *      An ambient term of 0.25 prevents unlit back faces from being completely black.
 *
 *   3. Output:
 *      - location = 0: vec4 outColor (Final RGBA color written to swapchain color attachment)
 * ================================================================================================= */

#version 450

// Inputs from Vertex Shader
layout(location = 0) in vec3 fragColor;
layout(location = 1) in vec3 fragNormal;
layout(location = 2) in vec2 fragTexCoord;

// Output to Color Attachment
layout(location = 0) out vec4 outColor;

void main() {
    // Key directional light vector
    vec3 lightDir = normalize(vec3(0.5, 1.0, 0.7));

    // Lambertian diffuse term with ambient floor
    vec3 normal = normalize(fragNormal);
    float diffuse = max(dot(normal, lightDir), 0.0);
    float ambient = 0.25;
    float lightIntensity = clamp(ambient + diffuse * 0.75, 0.0, 1.0);

    // Final shaded color
    outColor = vec4(fragColor * lightIntensity, 1.0);
}
