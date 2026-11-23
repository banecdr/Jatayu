/* =================================================================================================
 * FILE: TextureStreamerTests.cs
 * LOCATION: tests/Jatayu.Tests/TextureStreamerTests.cs
 * 
 * WHAT THIS FILE DOES:
 *   Unit tests validating TextureStreamer and MipResidency step-of-2 transition rules (D21)
 *   and permanent resident tail protections (D5).
 * ================================================================================================= */

using System;
using Jatayu.AssetFormats;
using Jatayu.Assets.Textures;
using Xunit;

namespace Jatayu.Tests;

public class TextureStreamerTests
{
    [Fact]
    public void ComputeStepTransition_ProtectsResidentMipTail()
    {
        // 10 total mips (0..9), resident tail is 3 mips (7, 8, 9 representing 64x64, 32x32, 16x16)
        // Max allowed top mip is 10 - 3 = 7.
        var state = new TextureResidencyState(
            textureGuid: new AssetGuid(1),
            fullWidth: 1024,
            fullHeight: 1024,
            totalMipLevels: 10,
            residentTailMips: 3,
            initialTopMip: 2,
            bindlessSlot: 0
        );

        // Requesting mip 9 (outside streamable boundary) must clamp to maxAllowedTopMip (7)
        uint nextMip = state.ComputeStepTransition(9);
        // From current 2, stepping by at most 2 brings it to 4 (clamped desired is 7)
        Assert.Equal(4u, nextMip);
    }

    [Fact]
    public void ComputeStepTransition_LimitsStepToAtMostTwoMipLevels()
    {
        var state = new TextureResidencyState(
            textureGuid: new AssetGuid(1),
            fullWidth: 2048,
            fullHeight: 2048,
            totalMipLevels: 11,
            residentTailMips: 4,
            initialTopMip: 6,
            bindlessSlot: 0
        );

        // 1. Increasing resolution drastically: from mip 6 down to 0
        // First step must change by at most 2: 6 - 2 = 4
        uint step1 = state.ComputeStepTransition(0);
        Assert.Equal(4u, step1);
        state.CurrentTopMip = step1;

        // Second step: 4 - 2 = 2
        uint step2 = state.ComputeStepTransition(0);
        Assert.Equal(2u, step2);
        state.CurrentTopMip = step2;

        // Third step: 2 - 2 = 0 (reaches full resolution)
        uint step3 = state.ComputeStepTransition(0);
        Assert.Equal(0u, step3);
        state.CurrentTopMip = step3;

        // 2. Decreasing resolution drastically: from mip 0 up to 6
        // First step must change by at most 2: 0 + 2 = 2
        uint down1 = state.ComputeStepTransition(6);
        Assert.Equal(2u, down1);
    }

    [Fact]
    public void ComputeStepTransition_SingleMipDifference_StepsByOne()
    {
        var state = new TextureResidencyState(
            textureGuid: new AssetGuid(1),
            fullWidth: 512,
            fullHeight: 512,
            totalMipLevels: 9,
            residentTailMips: 3,
            initialTopMip: 2,
            bindlessSlot: 0
        );

        uint nextUp = state.ComputeStepTransition(1);
        Assert.Equal(1u, nextUp);

        uint nextDown = state.ComputeStepTransition(3);
        Assert.Equal(3u, nextDown);

        uint same = state.ComputeStepTransition(2);
        Assert.Equal(2u, same);
    }
}
