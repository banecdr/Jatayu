#version 460 core
/* =================================================================================================
 * FILE: transparent_composite.vert
 * LOCATION: assets/shaders/transparent_composite.vert
 * 
 * WHAT THIS FILE DOES:
 *   Fullscreen procedural triangle vertex shader for Order-Independent Transparency (WBOIT)
 *   compositing. Generates a triangle covering NDC [-1, 1] with corresponding texture UVs [0, 1].
 * ================================================================================================= */

layout(location = 0) out vec2 vTexCoord;

void main() {
    // Generates a triangle covering NDC: (-1, -1), (3, -1), (-1, 3)
    // with UV coordinates:                (0,  0), (2,  0), (0,  2)
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    vTexCoord = uv;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}
