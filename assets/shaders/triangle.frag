/* =================================================================================================
 * FILE: triangle.frag
 * LOCATION: assets/shaders/triangle.frag
 * 
 * WHAT THIS SHADER DOES:
 *   This is the Vulkan Fragment (Pixel) Shader for rendering the colored triangle.
 *   It runs once per covered pixel on screen inside the triangle boundaries.
 *   It receives smoothly interpolated RGB colors from the rasterizer and writes the final 
 *   color (RGBA) into the swapchain framebuffer image.
 *
 * HOW IT WORKS INTERNALLY:
 *   1. Rasterization & Color Interpolation:
 *      Between the vertex shader and this fragment shader, the GPU hardware automatically blends 
 *      the colors of the 3 triangle corners (Red at top, Green at bottom-right, Blue at bottom-left).
 *      Every pixel inside the triangle receives its exact smoothly blended color.
 *
 *   2. Output Attachment (outColor):
 *      Writes a 4D RGBA vector to Location 0 (which maps directly to our swapchain color attachment).
 *      The alpha channel is set to 1.0 (fully opaque).
 *
 * PARAMETERS & ATTRIBUTES (IN PLAIN ENGLISH):
 *   - in vec3 inColor: The smoothly interpolated RGB color arriving from the rasterizer.
 *   - out vec4 outColor: The final 32-bit sRGB color (Red, Green, Blue, Alpha) written to the monitor.
 * ================================================================================================= */

#version 450

// Interpolated input from vertex shader (via rasterizer)
layout(location = 0) in vec3 inColor;

// Final color output to the swapchain color attachment
layout(location = 0) out vec4 outColor;

void main()
{
    // Write RGB color with full opacity (Alpha = 1.0)
    outColor = vec4(inColor, 1.0);
}
