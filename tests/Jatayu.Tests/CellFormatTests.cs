// ==================================================================================================
// SPRINT 6a: CellFormat Unit Tests
// Validates binary layout, sequential packing, and struct sizes of CookedCellHeader and records.
// ==================================================================================================

using System.Runtime.InteropServices;
using Jatayu.AssetFormats;
using Jatayu.AssetFormats.World;
using Xunit;

namespace Jatayu.Tests;

public class CellFormatTests
{
    [Fact]
    public void CookedCellHeader_HasExpectedMemoryLayoutAndMagic()
    {
        var coord = new CellCoord(3, -7);
        var guid = coord.ToAssetGuid();

        var header = CookedCellHeader.Create(
            coord,
            guid,
            instanceCount: 15,
            colliderCount: 2,
            audioCount: 1,
            CellFlags.HasFoliage | CellFlags.HasTerrain,
            minX: 0f, minY: -5f, minZ: 0f,
            maxX: 128f, maxY: 30f, maxZ: 128f
        );

        Assert.Equal(CookedCellHeader.MagicValue, header.Magic);
        Assert.Equal(CookedCellHeader.CurrentVersion, header.Version);
        Assert.True(header.IsValid);
        Assert.Equal(coord, header.Coord);
        Assert.Equal(guid, header.CellGuid);
        Assert.Equal(15u, header.StaticInstanceCount);
        Assert.Equal(2u, header.ColliderCount);
        Assert.Equal(1u, header.AudioEmitterCount);
        Assert.Equal(CellFlags.HasFoliage | CellFlags.HasTerrain, header.Flags);

        // Check offsets
        int headerSize = Marshal.SizeOf<CookedCellHeader>();
        int instanceSize = Marshal.SizeOf<CookedStaticInstance>();
        int colliderSize = Marshal.SizeOf<CookedColliderRecord>();

        Assert.Equal((ulong)headerSize, header.StaticInstancesOffset);
        Assert.Equal((ulong)(headerSize + 15 * instanceSize), header.CollidersOffset);
        Assert.Equal((ulong)(headerSize + 15 * instanceSize + 2 * colliderSize), header.AudioEmittersOffset);
    }

    [Fact]
    public void CookedStaticInstance_IsBlittableAndUnmanaged()
    {
        int size = Marshal.SizeOf<CookedStaticInstance>();
        Assert.True(size > 0);

        var inst = new CookedStaticInstance
        {
            PosX = 12.5f,
            PosY = 0.0f,
            PosZ = 95.2f,
            RotX = 0f,
            RotY = 0.7071f,
            RotZ = 0f,
            RotW = 0.7071f,
            ScaleX = 2.0f,
            ScaleY = 2.0f,
            ScaleZ = 2.0f,
            MeshGuid = AssetGuid.FromPath("rock"),
            MaterialGuid = AssetGuid.Empty,
            MaterialId = 42,
            BoundingRadius = 5.0f,
            Flags = StaticInstanceFlags.CastShadow | StaticInstanceFlags.Occluder,
        };

        Assert.Equal(12.5f, inst.PosX);
        Assert.Equal(42u, inst.MaterialId);
        Assert.True(inst.Flags.HasFlag(StaticInstanceFlags.CastShadow));
    }
}
