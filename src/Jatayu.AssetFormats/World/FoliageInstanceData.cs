/* =================================================================================================
 * FILE: FoliageInstanceData.cs
 * LOCATION: src/Jatayu.AssetFormats/World/FoliageInstanceData.cs
 * 
 * WHAT THIS FILE DOES:
 *   Compact 16-byte representation of a procedural foliage instance (grass blade, bush, or tree)
 *   supporting dense instancing across Ring A (up to 300m) with zero managed heap allocations.
 * ================================================================================================= */

using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Jatayu.AssetFormats.World;

/// <summary>
/// Packed 16-byte foliage instance structure.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct FoliageInstanceData
{
    /// <summary>
    /// Position offset relative to cell origin or terrain tile in meters (12 bytes).
    /// </summary>
    public Vector3 PositionOffset;

    /// <summary>
    /// Quantized yaw rotation angle: [0, 255] -> [0, 2*PI] (1 byte).
    /// </summary>
    public byte RotationY;

    /// <summary>
    /// Quantized uniform scale factor: [0, 255] -> [0.5, 2.0] (1 byte).
    /// </summary>
    public byte Scale;

    /// <summary>
    /// Packed color tint variation (high 8 bits) and spatial wind animation phase (low 8 bits) (2 bytes).
    /// </summary>
    public ushort TintAndWindPhase;

    public FoliageInstanceData(Vector3 position, float rotationRadians, float scale, byte tintIndex, byte windPhase)
    {
        PositionOffset = position;

        // Wrap radians into [0, 2*PI) and quantize to 0..255
        float wrappedRot = rotationRadians % (MathF.PI * 2.0f);
        if (wrappedRot < 0.0f) wrappedRot += MathF.PI * 2.0f;
        RotationY = (byte)Math.Clamp((int)(wrappedRot / (MathF.PI * 2.0f) * 255.0f), 0, 255);

        // Map scale from [0.5, 2.0] into [0, 255]
        float normScale = (scale - 0.5f) / 1.5f;
        Scale = (byte)Math.Clamp((int)(normScale * 255.0f), 0, 255);

        TintAndWindPhase = (ushort)((tintIndex << 8) | windPhase);
    }

    /// <summary>
    /// Unpacks the yaw rotation angle in radians.
    /// </summary>
    public float UnpackRotation() => (RotationY / 255.0f) * (MathF.PI * 2.0f);

    /// <summary>
    /// Unpacks the scale factor.
    /// </summary>
    public float UnpackScale() => 0.5f + (Scale / 255.0f) * 1.5f;

    /// <summary>
    /// Unpacks the spatial wind phase offset in radians.
    /// </summary>
    public float UnpackWindPhase() => ((TintAndWindPhase & 0xFF) / 255.0f) * (MathF.PI * 2.0f);
}
