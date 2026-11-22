// ==================================================================================================
// Jatayu Engine - 3D Materials & Shading
// Module: Material.cs
// Role: PBR material definition binding textures to bindless slots with scalar roughness/metallic factors.
// ==================================================================================================

using System.Numerics;
using System.Runtime.InteropServices;

namespace Jatayu.Renderer.Material;

/// <summary>
/// GPU-packed 64-byte PBR material struct aligned for direct storage buffer upload.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct GpuMaterialData
{
    public Vector4 BaseColorFactor; // 16 bytes: RGBA color factor
    public float MetallicFactor;     // 4 bytes: metallic scalar [0, 1]
    public float RoughnessFactor;    // 4 bytes: roughness scalar [0, 1]
    public uint AlbedoIndex;         // 4 bytes: bindless slot for base color texture
    public uint NormalIndex;         // 4 bytes: bindless slot for normal map
    public uint MetallicRoughnessIndex; // 4 bytes: bindless slot for metallic-roughness texture
    public uint OcclusionIndex;      // 4 bytes: bindless slot for ambient occlusion texture
    public uint EmissiveIndex;       // 4 bytes: bindless slot for emissive map
    public float EmissiveIntensity;  // 4 bytes: emissive luminance multiplier
    public Vector3 EmissiveFactor;   // 12 bytes: RGB emissive color
    public uint Flags;               // 4 bytes: material flags (double-sided, alpha-tested)
}

/// <summary>
/// Runtime PBR material asset binding texture slots and surface parameters.
/// </summary>
public sealed class Material
{
    public string Name { get; set; } = "DefaultMaterial";
    public Vector4 BaseColor { get; set; } = Vector4.One;
    public float Metallic { get; set; } = 0.0f;
    public float Roughness { get; set; } = 0.5f;

    public uint AlbedoTextureSlot { get; set; } = uint.MaxValue;
    public uint NormalTextureSlot { get; set; } = uint.MaxValue;
    public uint MetallicRoughnessTextureSlot { get; set; } = uint.MaxValue;
    public uint OcclusionTextureSlot { get; set; } = uint.MaxValue;
    public uint EmissiveTextureSlot { get; set; } = uint.MaxValue;

    public Vector3 EmissiveColor { get; set; } = Vector3.Zero;
    public float EmissiveIntensity { get; set; } = 1.0f;
    public bool DoubleSided { get; set; } = false;

    /// <summary>
    /// Converts this material into packed GPU layout format.
    /// </summary>
    public GpuMaterialData ToGpuData()
    {
        return new GpuMaterialData
        {
            BaseColorFactor = BaseColor,
            MetallicFactor = Metallic,
            RoughnessFactor = Roughness,
            AlbedoIndex = AlbedoTextureSlot,
            NormalIndex = NormalTextureSlot,
            MetallicRoughnessIndex = MetallicRoughnessTextureSlot,
            OcclusionIndex = OcclusionTextureSlot,
            EmissiveIndex = EmissiveTextureSlot,
            EmissiveIntensity = EmissiveIntensity,
            EmissiveFactor = EmissiveColor,
            Flags = DoubleSided ? 1u : 0u,
        };
    }
}
