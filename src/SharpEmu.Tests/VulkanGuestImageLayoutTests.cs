// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// Exercises the tracked guest-image layout planner behind every
/// VkImage barrier the presenter records: transitions must come from the
/// tracked state (never a guessed layout), cover the full mip chain, and
/// only be elided when the destination is a read the last barrier already
/// covers. The compute-write-then-sample case is the regression that made
/// compute-composited frames sample as black.
/// </summary>
public sealed class VulkanGuestImageLayoutTests
{
    private static VulkanVideoPresenter.GuestImageResource CreateImage(uint mipLevels = 1) =>
        new()
        {
            Image = new Image(0x1234),
            Width = 64,
            Height = 64,
            MipLevels = mipLevels,
        };

    [Fact]
    public void FreshImageTransitionsFromUndefinedWithFullMipRange()
    {
        var image = CreateImage(mipLevels: 5);

        var emitted = VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ShaderReadBit,
            out var barrier,
            out var srcStage);

        Assert.True(emitted);
        Assert.Equal(ImageLayout.Undefined, barrier.OldLayout);
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, barrier.NewLayout);
        Assert.Equal(PipelineStageFlags.TopOfPipeBit, srcStage);
        Assert.Equal((AccessFlags)0, barrier.SrcAccessMask);
        Assert.Equal(5u, barrier.SubresourceRange.LevelCount);
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, image.CurrentLayout);
    }

    [Fact]
    public void ComputeWrittenImageStillGetsBarrierForGraphicsSample()
    {
        // The black-menu regression: compute writes via ImageStore (General),
        // the post-dispatch read barrier returns the image to
        // ShaderReadOnlyOptimal but only with compute-stage scope. A graphics
        // draw sampling it MUST still emit a barrier even though the layout
        // enum already matches, or the fragment stage never observes the
        // compute write.
        var image = CreateImage();

        Assert.True(VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.General,
            PipelineStageFlags.ComputeShaderBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            out _,
            out _));
        Assert.True(VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.ComputeShaderBit,
            AccessFlags.ShaderReadBit,
            out var readBarrier,
            out var readSrcStage));
        Assert.Equal(ImageLayout.General, readBarrier.OldLayout);
        Assert.Equal(
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            readBarrier.SrcAccessMask);
        Assert.Equal(PipelineStageFlags.ComputeShaderBit, readSrcStage);

        var emitted = VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ShaderReadBit,
            out var sampleBarrier,
            out var sampleSrcStage);

        Assert.True(emitted);
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, sampleBarrier.OldLayout);
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, sampleBarrier.NewLayout);
        Assert.Equal(PipelineStageFlags.ComputeShaderBit, sampleSrcStage);
        Assert.Equal(AccessFlags.ShaderReadBit, sampleBarrier.DstAccessMask);
    }

    [Fact]
    public void RenderTargetSamplePathAddsNoExtraBarrier()
    {
        // The already-working path: post-render-pass the target rests in
        // ShaderReadOnlyOptimal with fragment-read scope, so a fragment
        // sample must not emit anything new.
        var image = CreateImage();

        Assert.True(VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.ColorAttachmentWriteBit,
            out _,
            out _));
        Assert.True(VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ShaderReadBit,
            out var postPass,
            out var postPassSrcStage));
        Assert.Equal(AccessFlags.ColorAttachmentWriteBit, postPass.SrcAccessMask);
        Assert.Equal(PipelineStageFlags.ColorAttachmentOutputBit, postPassSrcStage);

        var emitted = VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ShaderReadBit,
            out _,
            out _);

        Assert.False(emitted);
        Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, image.CurrentLayout);
    }

    [Fact]
    public void WriteRequestsAreNeverElided()
    {
        // Same-layout write-after-write (back-to-back dispatches storing to
        // one image) still needs ordering.
        var image = CreateImage();

        Assert.True(VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.General,
            PipelineStageFlags.ComputeShaderBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            out _,
            out _));

        var emitted = VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.General,
            PipelineStageFlags.ComputeShaderBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            out var barrier,
            out _);

        Assert.True(emitted);
        Assert.Equal(ImageLayout.General, barrier.OldLayout);
        Assert.Equal(ImageLayout.General, barrier.NewLayout);
        Assert.Equal(
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            barrier.SrcAccessMask);
    }

    [Fact]
    public void SkippedReadersAccumulateIntoTrackedAccess()
    {
        var image = CreateImage();

        Assert.True(VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
            AccessFlags.ShaderReadBit,
            out _,
            out _));

        Assert.False(VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.ComputeShaderBit,
            AccessFlags.ShaderReadBit,
            out _,
            out _));
        Assert.Equal(
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
            image.LastStageMask);
        Assert.Equal(AccessFlags.ShaderReadBit, image.LastAccessMask);
    }

    [Fact]
    public void InvalidatedImageResynchronizesConservatively()
    {
        var image = CreateImage();
        Assert.True(VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ShaderReadBit,
            out _,
            out _));

        VulkanVideoPresenter.InvalidateGuestImageLayout(image);

        Assert.Equal(ImageLayout.Undefined, image.CurrentLayout);
        var emitted = VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.FragmentShaderBit,
            AccessFlags.ShaderReadBit,
            out var barrier,
            out var srcStage);
        Assert.True(emitted);
        Assert.Equal(ImageLayout.Undefined, barrier.OldLayout);
        Assert.Equal(PipelineStageFlags.AllCommandsBit, srcStage);
        Assert.Equal(AccessFlags.MemoryWriteBit, barrier.SrcAccessMask);
    }

    [Fact]
    public void ZeroMipMetadataStillCoversOneLevel()
    {
        var image = CreateImage(mipLevels: 0);

        Assert.True(VulkanVideoPresenter.TryPlanGuestImageTransition(
            image,
            ImageLayout.TransferDstOptimal,
            PipelineStageFlags.TransferBit,
            AccessFlags.TransferWriteBit,
            out var barrier,
            out _));
        Assert.Equal(1u, barrier.SubresourceRange.LevelCount);
    }
}
