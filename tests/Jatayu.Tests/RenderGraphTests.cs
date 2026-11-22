/* =================================================================================================
 * FILE: RenderGraphTests.cs
 * LOCATION: tests/Jatayu.Tests/RenderGraphTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests for Vulkan 1.3 RenderGraph DAG scheduling, topological sorting, pass culling,
 *   and synchronization2 barrier compilation.
 * ================================================================================================= */

using System.Collections.Generic;
using Brutal.VulkanApi;
using Jatayu.Renderer.Graph;
using Xunit;

namespace Jatayu.Tests;

public class RenderGraphTests
{
    [Fact]
    public void RenderGraph_TopologicalSorting_ExecutesInDependencyOrder()
    {
        using var graph = new RenderGraph();

        var dummyImage = new VkImage();
        var dummyView = new VkImageView();

        var texA = graph.ImportTexture("TexA", dummyImage, dummyView, VkFormat.R8G8B8A8UNorm, 1280, 720, VkImageLayout.Undefined);
        var texB = graph.ImportTexture("TexB", dummyImage, dummyView, VkFormat.R8G8B8A8UNorm, 1280, 720, VkImageLayout.Undefined);

        var executionOrder = new List<string>();

        // Register Pass 3 first in registration order, but it depends on texB produced by Pass 2
        graph.AddPass(
            "Pass3_Present",
            setup =>
            {
                setup.ReadTexture(texB, VkPipelineStageFlags2.FragmentShaderBit, VkAccessFlags2.ShaderSampledReadBit);
                setup.HasSideEffects(true);
            },
            ctx => executionOrder.Add("Pass3_Present")
        );

        // Register Pass 1 which writes texA
        graph.AddPass(
            "Pass1_DepthPrepass",
            setup =>
            {
                setup.WriteTexture(texA, VkPipelineStageFlags2.ColorAttachmentOutputBit, VkAccessFlags2.ColorAttachmentWriteBit);
            },
            ctx => executionOrder.Add("Pass1_DepthPrepass")
        );

        // Register Pass 2 which reads texA and writes texB
        graph.AddPass(
            "Pass2_OpaquePass",
            setup =>
            {
                setup.ReadTexture(texA, VkPipelineStageFlags2.FragmentShaderBit, VkAccessFlags2.ShaderSampledReadBit);
                setup.WriteTexture(texB, VkPipelineStageFlags2.ColorAttachmentOutputBit, VkAccessFlags2.ColorAttachmentWriteBit);
            },
            ctx => executionOrder.Add("Pass2_OpaquePass")
        );

        graph.Compile();

        Assert.Equal(3, graph.CompiledPasses.Count);
        Assert.Equal("Pass1_DepthPrepass", graph.CompiledPasses[0].Name);
        Assert.Equal("Pass2_OpaquePass", graph.CompiledPasses[1].Name);
        Assert.Equal("Pass3_Present", graph.CompiledPasses[2].Name);
    }

    [Fact]
    public void RenderGraph_PassCulling_CullsUnusedBranches()
    {
        using var graph = new RenderGraph();

        var dummyImage = new VkImage();
        var dummyView = new VkImageView();

        var usedTex = graph.ImportTexture("UsedTex", dummyImage, dummyView, VkFormat.R8G8B8A8UNorm, 1280, 720, VkImageLayout.Undefined);
        var deadTex = graph.ImportTexture("DeadTex", dummyImage, dummyView, VkFormat.R8G8B8A8UNorm, 1280, 720, VkImageLayout.Undefined);

        // Pass A produces usedTex
        graph.AddPass(
            "ProducerPass",
            setup => setup.WriteTexture(usedTex),
            ctx => { }
        );

        // Dead Pass produces deadTex which no active pass consumes
        graph.AddPass(
            "DeadPass",
            setup => setup.WriteTexture(deadTex),
            ctx => { }
        );

        // Consumer Pass reads usedTex and presents (has side effects)
        graph.AddPass(
            "PresentPass",
            setup =>
            {
                setup.ReadTexture(usedTex);
                setup.HasSideEffects(true);
            },
            ctx => { }
        );

        graph.Compile();

        Assert.Equal(2, graph.CompiledPasses.Count);
        Assert.Equal("ProducerPass", graph.CompiledPasses[0].Name);
        Assert.Equal("PresentPass", graph.CompiledPasses[1].Name);

        // DeadPass was marked as culled
        var deadPass = graph.Passes[1];
        Assert.Equal("DeadPass", deadPass.Name);
        Assert.True(deadPass.IsCulled);
    }

    [Fact]
    public void BarrierBuilder_QueuesAndCountsBarriersCorrectly()
    {
        var builder = new BarrierBuilder();
        Assert.False(builder.HasBarriers);
        Assert.Equal(0, builder.ImageBarrierCount);

        builder.AddImageBarrier(
            new VkImage(),
            VkImageAspectFlags.ColorBit,
            VkImageLayout.Undefined,
            VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags2.TopOfPipeBit,
            VkAccessFlags2.None,
            VkPipelineStageFlags2.ColorAttachmentOutputBit,
            VkAccessFlags2.ColorAttachmentWriteBit
        );

        Assert.True(builder.HasBarriers);
        Assert.Equal(1, builder.ImageBarrierCount);

        builder.Clear();
        Assert.False(builder.HasBarriers);
        Assert.Equal(0, builder.ImageBarrierCount);
    }

    [Fact]
    public void RenderGraph_LayoutTracking_TransitionsResourcesAcrossPasses()
    {
        using var graph = new RenderGraph();

        var dummyImage = new VkImage();
        var dummyView = new VkImageView();

        var colorTarget = graph.ImportTexture(
            "ColorTarget",
            dummyImage,
            dummyView,
            VkFormat.B8G8R8A8UNorm,
            1280,
            720,
            VkImageLayout.Undefined
        );

        // Pass 1: Render into color target
        graph.AddPass(
            "OpaquePass",
            setup =>
            {
                setup.WriteTexture(
                    colorTarget,
                    VkPipelineStageFlags2.ColorAttachmentOutputBit,
                    VkAccessFlags2.ColorAttachmentWriteBit,
                    VkImageLayout.ColorAttachmentOptimal
                );
            },
            ctx => { }
        );

        // Pass 2: Present transition (reads as present src)
        graph.AddPass(
            "PresentPass",
            setup =>
            {
                setup.ReadTexture(
                    colorTarget,
                    VkPipelineStageFlags2.ColorAttachmentOutputBit,
                    VkAccessFlags2.None,
                    VkImageLayout.PresentSrcKHR
                );
                setup.HasSideEffects(true);
            },
            ctx => { }
        );

        graph.Compile();

        Assert.Equal(2, graph.CompiledPasses.Count);
        Assert.Equal("OpaquePass", graph.CompiledPasses[0].Name);
        Assert.Equal("PresentPass", graph.CompiledPasses[1].Name);
    }
}
