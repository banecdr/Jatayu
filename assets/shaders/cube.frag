/* =================================================================================================
 * FILE: cube.frag
 * LOCATION: assets/shaders/cube.frag
 * 
 * WHAT THIS SHADER DOES:
 *   3D Fragment (pixel) shader for rendering textured 3D solid geometry in Vulkan 1.3.
 *   Samples the texture descriptor (set 0, binding 0) via sampler2D, modulates with per-vertex
 *   color, and applies Lambertian directional diffuse lighting based on surface normals.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Inputs from Vertex Shader:
 *      - location = 0: vec3 fragColor    (Interpolated RGB color from vertex buffer)
 *      - location = 1: vec3 fragNormal   (Interpolated surface normal vector)
 *      - location = 2: vec2 fragTexCoord (Interpolated UV coordinates)
 *
 *   2. Texture Sampling:
 *      - set = 0, binding = 0: uniform sampler2D uTexture (Combined image sampler descriptor)
 *      - Evaluates texture(uTexture, fragTexCoord)
 *
 *   3. Diffuse Directional Lighting:
 *      A simulated key light direction (vec3(0.5, 1.0, 0.7)) shines down on the 3D scene.
 *      The dot product max(dot(N, L), 0.0) calculates Lambertian diffuse reflection.
 *      An ambient term of 0.25 prevents unlit back faces from being completely black.
 *
 *   4. Output:
 *      - location = 0: vec4 outColor (Final RGBA color written to swapchain color attachment)
 * ================================================================================================= */

#version 450

// Inputs from Vertex Shader
layout(location = 0) in vec3 fragColor;
layout(location = 1) in vec3 fragNormal;
layout(location = 2) in vec2 fragTexCoord;

// Combined Image Sampler Descriptor (Set 0, Binding 0)
layout(set = 0, binding = 0) uniform sampler2D uTexture;

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

    // Sample texture and modulate with vertex color & lighting
    vec4 texColor = texture(uTexture, fragTexCoord);
    vec3 finalColor = texColor.rgb * fragColor * lightIntensity;

    outColor = vec4(finalColor, texColor.a);
}
