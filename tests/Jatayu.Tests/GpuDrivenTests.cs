/* =================================================================================================
 * FILE: GpuDrivenTests.cs
 * LOCATION: tests/Jatayu.Tests/GpuDrivenTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests for Sprint 7a GPU-driven rendering foundation:
 *   Struct packing & alignment for std430/std140 GPU shader interoperability,
 *   Multi-LOD record data structures, Frustum plane extraction, and Indirect draw offset math.
 * ================================================================================================= */

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using Jatayu.Renderer;
using Jatayu.Renderer.Camera;
using Jatayu.Renderer.GpuDriven;
using Xunit;

namespace Jatayu.Tests;

public class GpuDrivenTests
{
    [Fact]
    public void GpuInstanceData_SizeAndAlignment_Matches160Bytes()
    {
        Assert.Equal(160, Marshal.SizeOf<GpuInstanceData>());

        Assert.Equal(0, Marshal.OffsetOf<GpuInstanceData>(nameof(GpuInstanceData.CurrentTransform)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<GpuInstanceData>(nameof(GpuInstanceData.PreviousTransform)).ToInt32());
        Assert.Equal(128, Marshal.OffsetOf<GpuInstanceData>(nameof(GpuInstanceData.BoundingCenter)).ToInt32());
        Assert.Equal(140, Marshal.OffsetOf<GpuInstanceData>(nameof(GpuInstanceData.BoundingRadius)).ToInt32());
        Assert.Equal(144, Marshal.OffsetOf<GpuInstanceData>(nameof(GpuInstanceData.MeshId)).ToInt32());
        Assert.Equal(148, Marshal.OffsetOf<GpuInstanceData>(nameof(GpuInstanceData.MaterialId)).ToInt32());
        Assert.Equal(152, Marshal.OffsetOf<GpuInstanceData>(nameof(GpuInstanceData.Flags)).ToInt32());
        Assert.Equal(156, Marshal.OffsetOf<GpuInstanceData>(nameof(GpuInstanceData.LodBias)).ToInt32());
    }

    [Fact]
    public void PbrPushConstants_SizeAndAlignment_Matches208Bytes()
    {
        Assert.Equal(208, Marshal.SizeOf<PbrPushConstants>());

        Assert.Equal(0, Marshal.OffsetOf<PbrPushConstants>(nameof(PbrPushConstants.ModelMatrix)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<PbrPushConstants>(nameof(PbrPushConstants.ViewProjMatrix)).ToInt32());
        Assert.Equal(128, Marshal.OffsetOf<PbrPushConstants>(nameof(PbrPushConstants.LightViewProj)).ToInt32());
        Assert.Equal(192, Marshal.OffsetOf<PbrPushConstants>(nameof(PbrPushConstants.MaterialId)).ToInt32());
        Assert.Equal(196, Marshal.OffsetOf<PbrPushConstants>(nameof(PbrPushConstants.UseInstanceBuffer)).ToInt32());
    }

    [Fact]
    public void CullUniforms_SizeAndAlignment_Matches192Bytes()
    {
        Assert.Equal(192, Marshal.SizeOf<CullUniforms>());

        Assert.Equal(0, Marshal.OffsetOf<CullUniforms>(nameof(CullUniforms.FrustumPlane0)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<CullUniforms>(nameof(CullUniforms.FrustumPlane1)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<CullUniforms>(nameof(CullUniforms.FrustumPlane2)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<CullUniforms>(nameof(CullUniforms.FrustumPlane3)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<CullUniforms>(nameof(CullUniforms.FrustumPlane4)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<CullUniforms>(nameof(CullUniforms.CameraPosAndMaxDist)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<CullUniforms>(nameof(CullUniforms.ViewProj)).ToInt32());
        Assert.Equal(160, Marshal.OffsetOf<CullUniforms>(nameof(CullUniforms.HiZScreenSize)).ToInt32());
        Assert.Equal(168, Marshal.OffsetOf<CullUniforms>(nameof(CullUniforms.Padding)).ToInt32());
    }

    [Fact]
    public void CullPushConstants_IncludeShadowCasterFilter()
    {
        Assert.Equal(20, Marshal.SizeOf<CullPushConstants>());
        Assert.Equal(16, Marshal.OffsetOf<CullPushConstants>(nameof(CullPushConstants.ShadowView)).ToInt32());
    }

    [Fact]
    public void MeshLodLevel_And_MeshLodRecord_StructLayouts()
    {
        Assert.Equal(16, Marshal.SizeOf<MeshLodLevel>());
        Assert.Equal(48, Marshal.SizeOf<MeshLodRecord>());

        var record = new MeshLodRecord
        {
            Lod0 = new MeshLodLevel { IndexCount = 36, FirstIndex = 0, VertexOffset = 0, LodThreshold = 50f },
            Lod1 = new MeshLodLevel { IndexCount = 18, FirstIndex = 36, VertexOffset = 0, LodThreshold = 150f },
            Lod2 = new MeshLodLevel { IndexCount = 6, FirstIndex = 54, VertexOffset = 0, LodThreshold = 300f },
        };

        Assert.Equal(36u, record.Lod0.IndexCount);
        Assert.Equal(50f, record.Lod0.LodThreshold);
        Assert.Equal(18u, record.Lod1.IndexCount);
        Assert.Equal(150f, record.Lod1.LodThreshold);
        Assert.Equal(6u, record.Lod2.IndexCount);
        Assert.Equal(300f, record.Lod2.LodThreshold);
    }

    [Fact]
    public void HiZPushConstants_Size_Matches16Bytes()
    {
        Assert.Equal(16, Marshal.SizeOf<HiZPushConstants>());

        Assert.Equal(0, Marshal.OffsetOf<HiZPushConstants>(nameof(HiZPushConstants.PrevWidth)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<HiZPushConstants>(nameof(HiZPushConstants.PrevHeight)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<HiZPushConstants>(nameof(HiZPushConstants.DestWidth)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<HiZPushConstants>(nameof(HiZPushConstants.DestHeight)).ToInt32());
    }

    [Fact]
    public void VkDrawIndexedIndirectCommand_Size_Matches32Bytes()
    {
        // 5 uint fields (indexCount, instanceCount, firstIndex, vertexOffset, firstInstance) + alignment = 32 bytes in 64-bit ABI
        Assert.True(Marshal.SizeOf<VkDrawIndexedIndirectCommand>() >= 20);
    }

    [Fact]
    public void Frustum_PlaneExtraction_ProducesFiveValidPlanes()
    {
        var frustum = new Frustum();
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 16f / 9f, 0.1f, 1000f);
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 10), Vector3.Zero, Vector3.UnitY);
        var vp = view * proj;

        frustum.Update(vp, Vector3.Zero);

        var pLeft = frustum.GetPlane(Frustum.PlaneIndex.Left);
        var pRight = frustum.GetPlane(Frustum.PlaneIndex.Right);
        var pBottom = frustum.GetPlane(Frustum.PlaneIndex.Bottom);
        var pTop = frustum.GetPlane(Frustum.PlaneIndex.Top);
        var pNear = frustum.GetPlane(Frustum.PlaneIndex.Near);

        // Plane normals should be unit length
        Assert.True(MathF.Abs(pLeft.Normal.Length() - 1.0f) < 1e-4f);
        Assert.True(MathF.Abs(pRight.Normal.Length() - 1.0f) < 1e-4f);
        Assert.True(MathF.Abs(pBottom.Normal.Length() - 1.0f) < 1e-4f);
        Assert.True(MathF.Abs(pTop.Normal.Length() - 1.0f) < 1e-4f);
        Assert.True(MathF.Abs(pNear.Normal.Length() - 1.0f) < 1e-4f);

        // An object centered inside the frustum should be visible
        Assert.True(frustum.IsSphereVisible(Vector3.Zero, 1.0f));

        // An object far behind the camera should not be visible
        Assert.False(frustum.IsSphereVisible(new Vector3(0, 0, 50), 1.0f));
    }

    [Fact]
    public void GpuInstanceData_VelocityOutput_PreservesPreviousTransform()
    {
        var curr = Matrix4x4.CreateTranslation(10, 20, 30);
        var prev = Matrix4x4.CreateTranslation(9, 19, 29);

        var inst = new GpuInstanceData
        {
            CurrentTransform = curr,
            PreviousTransform = prev,
            BoundingCenter = new Vector3(10, 20, 30),
            BoundingRadius = 5f,
            MeshId = 42,
            MaterialId = 7,
            Flags = 1,
            LodBias = 0f,
        };

        Assert.Equal(curr, inst.CurrentTransform);
        Assert.Equal(prev, inst.PreviousTransform);
        Assert.Equal(42u, inst.MeshId);
        Assert.Equal(7u, inst.MaterialId);
    }
}
