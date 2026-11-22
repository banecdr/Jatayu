/* =================================================================================================
 * FILE: CookerTests.cs
 * LOCATION: tests/Jatayu.Tests/CookerTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Validates the Jatayu Offline Cooker & Packager (Sprint 4.3):
 *   - MeshCooker vertex cache, overdraw, fetch, and LOD simplification.
 *   - TextureCooker 2x2 box downsampling and resident mip tail computation.
 *   - MaterialCooker binary PBR descriptor encoding.
 *   - PackWriter .jpak contiguous packaging and TOC generation.
 * ================================================================================================= */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Jatayu.AssetFormats;
using Jatayu.Cooker;
using Xunit;

namespace Jatayu.Tests;

public class CookerTests
{
    [Fact]
    public void MeshCooker_CooksAndOptimizesGeometry()
    {
        // Simple cube geometry (8 vertices, 36 indices)
        var vertices = new CookedVertex[]
        {
            new(-1, -1, -1, 0, 0),
            new( 1, -1, -1, 1, 0),
            new( 1,  1, -1, 1, 1),
            new(-1,  1, -1, 0, 1),
            new(-1, -1,  1, 0, 0),
            new( 1, -1,  1, 1, 0),
            new( 1,  1,  1, 1, 1),
            new(-1,  1,  1, 0, 1),
        };

        var indices = new uint[]
        {
            0, 1, 2, 2, 3, 0,
            1, 5, 6, 6, 2, 1,
            5, 4, 7, 7, 6, 5,
            4, 0, 3, 3, 7, 4,
            3, 2, 6, 6, 7, 3,
            4, 5, 1, 1, 0, 4,
        };

        var guid = AssetGuid.FromPath("models/test_cube.gltf");
        byte[] cookedBytes = MeshCooker.CookMesh(guid, vertices, indices, lodLevels: 2);

        Assert.NotNull(cookedBytes);
        Assert.True(cookedBytes.Length > Marshal.SizeOf<CookedMeshHeader>());

        var header = MemoryMarshal.Read<CookedMeshHeader>(cookedBytes.AsSpan(0, Marshal.SizeOf<CookedMeshHeader>()));
        Assert.True(header.IsValid);
        Assert.Equal(guid, header.Guid);
        Assert.Equal(8u, header.TotalVertexCount);
        Assert.True(header.BoundingRadius > 1.0f);
    }

    [Fact]
    public void TextureCooker_GeneratesMipsAndResidentTail()
    {
        const int width = 128;
        const int height = 128;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)200);

        var guid = AssetGuid.FromPath("textures/test_wood.png");
        byte[] cookedBytes = TextureCooker.CookRgba8(guid, width, height, pixels, isSrgb: true);

        Assert.NotNull(cookedBytes);
        int headerSize = Marshal.SizeOf<CookedTextureHeader>();
        Assert.True(cookedBytes.Length > headerSize);

        var header = MemoryMarshal.Read<CookedTextureHeader>(cookedBytes.AsSpan(0, headerSize));
        Assert.True(header.IsValid);
        Assert.Equal(guid, header.Guid);
        Assert.Equal(128u, header.Width);
        Assert.Equal(128u, header.Height);
        Assert.Equal(8u, header.MipLevels); // 128, 64, 32, 16, 8, 4, 2, 1 = 8 mips
        Assert.True(header.ResidentMips >= 7); // mips <= 64x64: 64, 32, 16, 8, 4, 2, 1 = 7 mips
        Assert.True(header.ResidentTailSize > 0);
    }

    [Fact]
    public void MaterialCooker_BakesPbrRecord()
    {
        var matGuid = AssetGuid.FromPath("materials/metal.mat");
        var albedoGuid = AssetGuid.FromPath("textures/metal_albedo.png");
        var normalGuid = AssetGuid.FromPath("textures/metal_normal.png");

        byte[] cookedBytes = MaterialCooker.CookMaterial(
            matGuid,
            albedoTex: albedoGuid,
            normalTex: normalGuid,
            roughness: 0.25f,
            metallic: 0.95f
        );

        int size = Marshal.SizeOf<CookedMaterialRecord>();
        Assert.Equal(size, cookedBytes.Length);

        var record = MemoryMarshal.Read<CookedMaterialRecord>(cookedBytes);
        Assert.True(record.IsValid);
        Assert.Equal(matGuid, record.Guid);
        Assert.Equal(albedoGuid, record.AlbedoTexture);
        Assert.Equal(normalGuid, record.NormalTexture);
        Assert.Equal(0.25f, record.RoughnessFactor);
        Assert.Equal(0.95f, record.MetallicFactor);
    }

    [Fact]
    public void PackWriter_CreatesValidJpakArchive()
    {
        string tempPak = Path.Combine(Path.GetTempPath(), $"test_pack_{Guid.NewGuid():N}.jpak");
        try
        {
            using (var writer = new PackWriter())
            {
                var guid1 = AssetGuid.FromPath("assets/tex1.png");
                var guid2 = AssetGuid.FromPath("assets/mesh1.gltf");

                byte[] data1 = new byte[100];
                Array.Fill(data1, (byte)0xAA);
                byte[] data2 = new byte[250];
                Array.Fill(data2, (byte)0xBB);

                writer.AddEntry(guid1, AssetType.Texture, data1);
                writer.AddEntry(guid2, AssetType.Mesh, data2);

                writer.WriteToFile(tempPak);
            }

            Assert.True(File.Exists(tempPak));

            using var fs = File.OpenRead(tempPak);
            int headerSize = Marshal.SizeOf<JpakHeader>();
            Span<byte> headerBytes = stackalloc byte[headerSize];
            fs.ReadExactly(headerBytes);
            var header = MemoryMarshal.Read<JpakHeader>(headerBytes);

            Assert.True(header.IsValid);
            Assert.Equal(2u, header.EntryCount);
            Assert.True(header.DataOffset >= header.TocOffset);
        }
        finally
        {
            if (File.Exists(tempPak))
                File.Delete(tempPak);
        }
    }
}
