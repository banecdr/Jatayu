/* =================================================================================================
 * FILE: AssetFormatsTests.cs
 * LOCATION: tests/Jatayu.Tests/AssetFormatsTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Validates the Jatayu Asset Formats binary specifications (Sprint 4.2):
 *   - AssetGuid determinism, normalization, parsing, and formatting.
 *   - JpakHeader, JpakEntry struct sizes, magic numbers, and binary validity.
 *   - CookedMeshHeader, CookedTextureHeader, and CookedMaterialRecord memory layout integrity.
 * ================================================================================================= */

using System;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;
using Xunit;

namespace Jatayu.Tests;

public class AssetFormatsTests
{
    [Fact]
    public void AssetGuid_FromPath_IsDeterministicAndNormalized()
    {
        var guid1 = AssetGuid.FromPath("Assets/Models/Rock.gltf");
        var guid2 = AssetGuid.FromPath("assets\\models\\rock.gltf");
        var guid3 = AssetGuid.FromPath("assets/models/rock.gltf ");

        Assert.True(guid1.IsValid);
        Assert.Equal(guid1, guid2);
        Assert.Equal(guid1, guid3);
    }

    [Fact]
    public void AssetGuid_ParseAndToString_RoundTripsCorrectly()
    {
        var original = AssetGuid.FromPath("textures/cliff_albedo.png");
        string hex = original.ToString();

        Assert.Equal(16, hex.Length);
        bool parsed = AssetGuid.TryParse(hex, out var recovered);

        Assert.True(parsed);
        Assert.Equal(original, recovered);

        // Test with 0x prefix
        Assert.True(AssetGuid.TryParse("0x" + hex, out var prefixedRecovered));
        Assert.Equal(original, prefixedRecovered);
    }

    [Fact]
    public void JpakHeader_MagicAndValidation_Passes()
    {
        var header = JpakHeader.Create(entryCount: 12, tocOffset: 64, dataOffset: 1024);

        Assert.True(header.IsValid);
        Assert.Equal(JpakHeader.MagicValue, header.Magic);
        Assert.Equal(12u, header.EntryCount);
        Assert.Equal(32u, (uint)Marshal.SizeOf<JpakHeader>());
    }

    [Fact]
    public void CookedMeshHeader_BinaryIntegrity_Passes()
    {
        var header = new CookedMeshHeader
        {
            Magic = CookedMeshHeader.MagicValue,
            Version = CookedMeshHeader.CurrentVersion,
            Guid = AssetGuid.FromPath("models/tree.gltf"),
            LodCount = 3,
            TotalVertexCount = 1200,
            TotalIndexCount = 3600,
            BoundingRadius = 5.5f,
        };

        Assert.True(header.IsValid);
        Assert.Equal(3u, header.LodCount);
    }

    [Fact]
    public void CookedTextureHeader_FormatAndResidentTail_Passes()
    {
        var header = new CookedTextureHeader
        {
            Magic = CookedTextureHeader.MagicValue,
            Version = CookedTextureHeader.CurrentVersion,
            Guid = AssetGuid.FromPath("textures/sand.png"),
            Width = 1024,
            Height = 1024,
            MipLevels = 11,
            ResidentMips = 4,
            Format = CookedTextureFormat.Bc7Rgba,
            Flags = TextureFlags.Srgb | TextureFlags.HasMipmaps,
            ResidentTailOffset = 512,
            ResidentTailSize = 4096,
        };

        Assert.True(header.IsValid);
        Assert.Equal(CookedTextureFormat.Bc7Rgba, header.Format);
        Assert.True(header.Flags.HasFlag(TextureFlags.Srgb));
    }

    [Fact]
    public void CookedMaterialRecord_Bindings_Passes()
    {
        var mat = new CookedMaterialRecord
        {
            Magic = CookedMaterialRecord.MagicValue,
            Version = CookedMaterialRecord.CurrentVersion,
            Guid = AssetGuid.FromPath("materials/brick.mat"),
            AlbedoTexture = AssetGuid.FromPath("textures/brick_albedo.png"),
            NormalTexture = AssetGuid.FromPath("textures/brick_normal.png"),
            RoughnessFactor = 0.8f,
            MetallicFactor = 0.1f,
            Flags = MaterialFlags.None,
            BaseColorR = 1.0f,
            BaseColorG = 1.0f,
            BaseColorB = 1.0f,
            BaseColorA = 1.0f,
        };

        Assert.True(mat.IsValid);
        Assert.True(mat.AlbedoTexture.IsValid);
        Assert.True(mat.NormalTexture.IsValid);
    }

    [Fact]
    public void ModelLoader_ThrowsOnMissingFile()
    {
        Assert.Throws<FileNotFoundException>(() =>
        {
            Jatayu.AssetFormats.Loaders.ModelLoader.LoadFromFile("non_existent_model.gltf");
        });
    }
}
