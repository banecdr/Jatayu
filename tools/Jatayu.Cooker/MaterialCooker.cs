/* =================================================================================================
 * FILE: MaterialCooker.cs
 * LOCATION: tools/Jatayu.Cooker/MaterialCooker.cs
 * 
 * WHAT THIS FILE DOES:
 *   Packages PBR material parameters and texture AssetGuids into binary descriptor blobs (.jmat).
 *   Enables instant zero-overhead runtime material loading and binding into bindless descriptor tables.
 * ================================================================================================= */

using System;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;

namespace Jatayu.Cooker;

/// <summary>
/// Packages PBR materials into cooked .jmat binary descriptors.
/// </summary>
public static class MaterialCooker
{
    /// <summary>
    /// Cooks PBR material parameters and texture bindings into a .jmat binary blob.
    /// </summary>
    public static byte[] CookMaterial(
        AssetGuid guid,
        AssetGuid albedoTex = default,
        AssetGuid normalTex = default,
        AssetGuid metallicRoughnessTex = default,
        AssetGuid occlusionTex = default,
        AssetGuid emissiveTex = default,
        float roughness = 1.0f,
        float metallic = 0.0f,
        float alphaCutoff = 0.5f,
        MaterialFlags flags = MaterialFlags.None,
        float baseColorR = 1.0f,
        float baseColorG = 1.0f,
        float baseColorB = 1.0f,
        float baseColorA = 1.0f)
    {
        var record = new CookedMaterialRecord
        {
            Magic = CookedMaterialRecord.MagicValue,
            Version = CookedMaterialRecord.CurrentVersion,
            Guid = guid,
            AlbedoTexture = albedoTex,
            NormalTexture = normalTex,
            MetallicRoughnessTexture = metallicRoughnessTex,
            OcclusionTexture = occlusionTex,
            EmissiveTexture = emissiveTex,
            RoughnessFactor = roughness,
            MetallicFactor = metallic,
            AlphaCutoff = alphaCutoff,
            Flags = flags,
            BaseColorR = baseColorR,
            BaseColorG = baseColorG,
            BaseColorB = baseColorB,
            BaseColorA = baseColorA,
        };

        int size = Marshal.SizeOf<CookedMaterialRecord>();
        var result = new byte[size];
        MemoryMarshal.Write(result, in record);
        return result;
    }
}
