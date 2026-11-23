/* =================================================================================================
 * FILE: ImpostorCooker.cs
 * LOCATION: tools/Jatayu.Cooker/ImpostorCooker.cs
 * 
 * WHAT THIS FILE DOES:
 *   Generates 8-angle billboard impostor texture atlases for far-distance vegetation and props.
 *   Provides geometric billboard quad vertices and texture coordinate mapping across the 8 angle slices
 *   (45° yaw steps) to replace dense 3D meshes beyond Ring A (> 300m - 600m).
 * ================================================================================================= */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;

namespace Jatayu.Cooker;

/// <summary>
/// Offline cooker generating 8-angle billboard impostor metadata and texture atlas coordinates.
/// </summary>
public static class ImpostorCooker
{
    public const int AngleCount = 8; // 8 angles around vertical yaw axis (45° steps)

    /// <summary>
    /// Computes the UV sub-rectangle within an 8-angle impostor texture atlas (arranged as 4x2 grid).
    /// </summary>
    public static void GetAngleAtlasUv(int angleIndex, out float uMin, out float vMin, out float uMax, out float vMax)
    {
        int index = Math.Clamp(angleIndex, 0, AngleCount - 1);
        int col = index % 4; // 4 columns
        int row = index / 4; // 2 rows

        float colWidth = 0.25f;
        float rowHeight = 0.5f;

        uMin = col * colWidth;
        vMin = row * rowHeight;
        uMax = uMin + colWidth;
        vMax = vMin + rowHeight;
    }

    /// <summary>
    /// Computes which of the 8 atlas angle slices best matches a given camera-relative view direction.
    /// </summary>
    public static int ComputeBestAngleIndex(float viewDirX, float viewDirZ)
    {
        float angleRad = MathF.Atan2(viewDirX, viewDirZ);
        if (angleRad < 0f) angleRad += MathF.PI * 2.0f;

        // 8 slices = PI / 4 (45° per slice). Offset by half slice (22.5°) for optimal centering
        float sliceSize = MathF.PI * 2.0f / AngleCount;
        int slice = (int)MathF.Floor((angleRad + (sliceSize * 0.5f)) / sliceSize) % AngleCount;
        return slice;
    }
}
