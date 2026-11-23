/* =================================================================================================
 * FILE: TerrainMaterial.cs
 * LOCATION: src/Terrain/TerrainMaterial.cs
 * 
 * WHAT THIS FILE DOES:
 *   Configures multi-layer terrain splatting parameters, triplanar cliff projection factors,
 *   and texture layer bindings (Grass, Dirt, Rock, Gravel) for the continuous clipmap terrain.
 * ================================================================================================= */

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;

namespace Jatayu.Terrain;

/// <summary>
/// GPU uniform parameters for terrain multi-layer splatting and triplanar cliff projection (128 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 128)]
public struct GpuTerrainMaterialData
{
    // Layer UV tiling scales
    public Vector4 LayerTiling; // X=Grass, Y=Dirt, Z=Rock, W=Gravel

    // Triplanar projection blend parameters
    public float TriplanarSharpness;
    public float CliffSlopeThreshold;
    public float EdgeBlendDistance;
    public float RoughnessMultiplier;

    // Layer albedo tints
    public Vector4 GrassTint;
    public Vector4 RockTint;
    public Vector4 DirtTint;
    public Vector4 GravelTint;
}

/// <summary>
/// Container holding terrain texture GUIDs and material blending parameters.
/// </summary>
public sealed class TerrainMaterial
{
    public AssetGuid GrassTexture { get; set; }
    public AssetGuid DirtTexture { get; set; }
    public AssetGuid RockTexture { get; set; }
    public AssetGuid GravelTexture { get; set; }

    public Vector4 LayerTiling { get; set; } = new(16.0f, 16.0f, 8.0f, 16.0f);
    public float TriplanarSharpness { get; set; } = 4.0f;
    public float CliffSlopeThreshold { get; set; } = 0.65f; // Normal.Y below 0.65 blends into cliff rock
    public float EdgeBlendDistance { get; set; } = 16.0f;   // 16m edge blend (D22)

    public GpuTerrainMaterialData ToGpuData() => new()
    {
        LayerTiling = LayerTiling,
        TriplanarSharpness = TriplanarSharpness,
        CliffSlopeThreshold = CliffSlopeThreshold,
        EdgeBlendDistance = EdgeBlendDistance,
        RoughnessMultiplier = 1.0f,
        GrassTint = new Vector4(0.35f, 0.65f, 0.25f, 1.0f),
        RockTint = new Vector4(0.55f, 0.52f, 0.50f, 1.0f),
        DirtTint = new Vector4(0.45f, 0.35f, 0.25f, 1.0f),
        GravelTint = new Vector4(0.60f, 0.58f, 0.55f, 1.0f),
    };
}
