// ==================================================================================================
// Jatayu Engine - Screen-Door Stipple Dithering
// File: lod_dither.glsl
// Role: Evaluates 4x4 Bayer stipple matrix for smooth LOD cross-fading and distance dissolve.
// ==================================================================================================

#ifndef LOD_DITHER_GLSL
#define LOD_DITHER_GLSL

// 4x4 Bayer dithering matrix normalized to [0, 15] / 16.0
const float BayerMatrix4x4[16] = float[16](
     0.0 / 16.0,  8.0 / 16.0,  2.0 / 16.0, 10.0 / 16.0,
    12.0 / 16.0,  4.0 / 16.0, 14.0 / 16.0,  6.0 / 16.0,
     3.0 / 16.0, 11.0 / 16.0,  1.0 / 16.0,  9.0 / 16.0,
    15.0 / 16.0,  7.0 / 16.0, 13.0 / 16.0,  5.0 / 16.0
);

// Returns true if the fragment passes the dither threshold.
// alpha: desired opacity in [0.0, 1.0]
bool EvaluateDither(vec2 screenPos, float alpha) {
    if (alpha >= 1.0) return true;
    if (alpha <= 0.0) return false;

    int x = int(mod(screenPos.x, 4.0));
    int y = int(mod(screenPos.y, 4.0));
    float threshold = BayerMatrix4x4[y * 4 + x];

    return alpha > threshold;
}

void ApplyLODDither(vec2 screenPos, float ditherAlpha) {
    if (!EvaluateDither(screenPos, ditherAlpha)) {
        discard;
    }
}

#endif // LOD_DITHER_GLSL
