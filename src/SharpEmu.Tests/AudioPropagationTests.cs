// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// libSceAudioPropagation stub surface: out-param positions match the register
/// conventions recovered from Astro Bot's call sites (required size at
/// memInfo+0x18, system/portal/material handles through rdx, room/source
/// handles through rsi, zero ray/path counts), and nothing outside those slots
/// is ever written.
/// </summary>
public sealed class AudioPropagationTests
{
    private const ulong MemInfoAddress = 0x2000;
    private const ulong OutHandleAddress = 0x3000;

    private const string DisableVariable = "SHARPEMU_DISABLE_AUDIO_PROPAGATION";

    /// <summary>
    /// The subsystem defaults to disabled (Astro Bot's output-bus buffer
    /// pointer keeps getting stomped mid-session); handle-creating exports
    /// only mint handles when the variable is explicitly "0".
    /// </summary>
    private static EnvScope PropagationEnabled() => new EnvScope(DisableVariable, "0");

    private static EnvScope PropagationDisabled(string? value) => new EnvScope(DisableVariable, value);

    private sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previous);
        }
    }

    private static CpuContext NewContext()
    {
        return new CpuContext(new SparseGuestMemory(), Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void SystemQueryMemory_WritesRequiredSizeAtOffset0x18_Only()
    {
        var ctx = NewContext();

        // The caller zeroes a 0x40-byte info struct; the required size must
        // land at +0x18 and the neighbouring fields must stay untouched.
        for (ulong offset = 0; offset < 0x40; offset += 8)
        {
            Assert.True(ctx.TryWriteUInt64(MemInfoAddress + offset, 0));
        }

        ctx[CpuRegister.Rdi] = 0x1000; // param struct
        ctx[CpuRegister.Rsi] = MemInfoAddress;
        AudioPropagationExports.SystemQueryMemory(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(MemInfoAddress + 0x18, out var size));
        Assert.True(size > 0);
        Assert.True(ctx.TryReadUInt64(MemInfoAddress + 0x10, out var memoryField));
        Assert.Equal(0u, memoryField);
        Assert.True(ctx.TryReadUInt64(MemInfoAddress + 0x20, out var tailField));
        Assert.Equal(0u, tailField);
    }

    [Fact]
    public void SystemQueryMemory_RejectsNullArguments()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = MemInfoAddress;

        AudioPropagationExports.SystemQueryMemory(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));
    }

    [Fact]
    public void SystemCreate_WritesNonNullHandleThroughRdx()
    {
        var ctx = NewContext();
        using var _ = PropagationEnabled();
        ctx[CpuRegister.Rdi] = 0x1000; // param struct
        ctx[CpuRegister.Rsi] = MemInfoAddress;
        ctx[CpuRegister.Rdx] = OutHandleAddress;

        AudioPropagationExports.SystemCreate(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(OutHandleAddress, out var system));
        Assert.NotEqual(0u, system);
    }

    [Fact]
    public void RoomCreate_WritesHandleThroughRsi_AndNeverThroughRdx()
    {
        var ctx = NewContext();
        using var _ = PropagationEnabled();

        // At Astro Bot's call site rdx holds a stale rodata pointer from the
        // previous call, so RoomCreate must leave it strictly alone.
        const ulong staleRdxAddress = 0x4000;
        const ulong sentinel = 0xDEADBEEFCAFEF00D;
        Assert.True(ctx.TryWriteUInt64(staleRdxAddress, sentinel));

        ctx[CpuRegister.Rdi] = 0x4150000100000001; // system handle
        ctx[CpuRegister.Rsi] = OutHandleAddress;
        ctx[CpuRegister.Rdx] = staleRdxAddress;
        AudioPropagationExports.RoomCreate(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(OutHandleAddress, out var room));
        Assert.NotEqual(0u, room);
        Assert.True(ctx.TryReadUInt64(staleRdxAddress, out var untouched));
        Assert.Equal(sentinel, untouched);
    }

    [Fact]
    public void PortalCreate_WritesNonNullHandleThroughRdx()
    {
        var ctx = NewContext();
        using var _ = PropagationEnabled();
        ctx[CpuRegister.Rdi] = 0x4150000100000001; // system handle
        ctx[CpuRegister.Rsi] = 0x1000; // portal param struct
        ctx[CpuRegister.Rdx] = OutHandleAddress;

        AudioPropagationExports.PortalCreate(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(OutHandleAddress, out var portal));
        Assert.NotEqual(0u, portal);
    }

    [Fact]
    public void SourceCreate_WritesNonNullHandleThroughRsi()
    {
        var ctx = NewContext();
        using var _ = PropagationEnabled();
        ctx[CpuRegister.Rdi] = 0x4150000100000001; // system handle
        ctx[CpuRegister.Rsi] = OutHandleAddress;

        AudioPropagationExports.SourceCreate(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(OutHandleAddress, out var source));
        Assert.NotEqual(0u, source);
    }

    [Fact]
    public void SystemRegisterMaterial_WritesUniqueHandlesThroughRdx()
    {
        var ctx = NewContext();
        using var _ = PropagationEnabled();
        ctx[CpuRegister.Rdi] = 0x4150000100000001; // system handle
        ctx[CpuRegister.Rsi] = 0x1000; // material param struct
        ctx[CpuRegister.Rdx] = OutHandleAddress;
        AudioPropagationExports.SystemRegisterMaterial(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(OutHandleAddress, out var first));

        AudioPropagationExports.SystemRegisterMaterial(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(OutHandleAddress, out var second));

        // The game keys a std::map by these, so they must be real and unique.
        Assert.NotEqual(0u, first);
        Assert.NotEqual(0u, second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateCalls_MintDistinctHandlesAcrossObjectTypes()
    {
        var ctx = NewContext();
        using var _ = PropagationEnabled();
        var handles = new ulong[4];

        ctx[CpuRegister.Rdi] = 0x1000;
        ctx[CpuRegister.Rsi] = MemInfoAddress;
        ctx[CpuRegister.Rdx] = OutHandleAddress;
        AudioPropagationExports.SystemCreate(ctx);
        ctx.TryReadUInt64(OutHandleAddress, out handles[0]);

        ctx[CpuRegister.Rdi] = handles[0];
        ctx[CpuRegister.Rsi] = OutHandleAddress;
        AudioPropagationExports.RoomCreate(ctx);
        ctx.TryReadUInt64(OutHandleAddress, out handles[1]);

        ctx[CpuRegister.Rdi] = handles[0];
        ctx[CpuRegister.Rsi] = 0x1000;
        ctx[CpuRegister.Rdx] = OutHandleAddress;
        AudioPropagationExports.PortalCreate(ctx);
        ctx.TryReadUInt64(OutHandleAddress, out handles[2]);

        ctx[CpuRegister.Rdi] = handles[0];
        ctx[CpuRegister.Rsi] = OutHandleAddress;
        AudioPropagationExports.SourceCreate(ctx);
        ctx.TryReadUInt64(OutHandleAddress, out handles[3]);

        Assert.Equal(4, handles.Distinct().Count());
        Assert.All(handles, handle => Assert.NotEqual(0u, handle));
    }

    [Fact]
    public void CreateCalls_RejectNullOutPointers()
    {
        var ctx = NewContext();
        using var _ = PropagationEnabled();

        ctx[CpuRegister.Rdi] = 0x1000;
        ctx[CpuRegister.Rsi] = MemInfoAddress;
        ctx[CpuRegister.Rdx] = 0;
        AudioPropagationExports.SystemCreate(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));

        ctx[CpuRegister.Rdi] = 0x4150000100000001;
        ctx[CpuRegister.Rsi] = 0;
        AudioPropagationExports.RoomCreate(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));

        ctx[CpuRegister.Rsi] = 0x1000;
        ctx[CpuRegister.Rdx] = 0;
        AudioPropagationExports.PortalCreate(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));

        ctx[CpuRegister.Rsi] = 0;
        AudioPropagationExports.SourceCreate(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Result(ctx));
    }

    [Fact]
    public void GetRays_WriteZeroCountThroughRdx()
    {
        var ctx = NewContext();
        const ulong countAddress = 0x5000;

        // Astro Bot pre-sets the in-out count to its array capacity; the stub
        // reports zero valid rays against the caller's pre-zeroed array.
        Assert.True(ctx.TryWriteUInt32(countAddress, 0x40));
        ctx[CpuRegister.Rdi] = 0x4150000100000001;
        ctx[CpuRegister.Rsi] = 0x6000; // ray array
        ctx[CpuRegister.Rdx] = countAddress;
        AudioPropagationExports.SystemGetRays(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(countAddress, out var systemRays));
        Assert.Equal(0u, systemRays);

        Assert.True(ctx.TryWriteUInt32(countAddress, 6));
        AudioPropagationExports.SourceGetRays(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(countAddress, out var sourceRays));
        Assert.Equal(0u, sourceRays);
    }

    [Fact]
    public void SourceGetAudioPathCount_WritesZeroThroughRsi()
    {
        var ctx = NewContext();
        const ulong countAddress = 0x5000;
        Assert.True(ctx.TryWriteUInt32(countAddress, 0x1234));

        ctx[CpuRegister.Rdi] = 0x4150000400000004; // source handle
        ctx[CpuRegister.Rsi] = countAddress;
        AudioPropagationExports.SourceGetAudioPathCount(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(countAddress, out var count));
        Assert.Equal(0u, count);
    }

    [Fact]
    public void InputOnlyCalls_AllSucceed()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0x4150000100000001;
        ctx[CpuRegister.Rsi] = 0x1000;
        ctx[CpuRegister.Rdx] = 1;

        foreach (var call in new Func<CpuContext, int>[]
                 {
                     AudioPropagationExports.SystemUnregisterMaterial,
                     AudioPropagationExports.SystemSetRays,
                     AudioPropagationExports.SystemSetAttributes,
                     AudioPropagationExports.SystemQueryInfo,
                     AudioPropagationExports.SystemLock,
                     AudioPropagationExports.SystemDestroy,
                     AudioPropagationExports.RoomDestroy,
                     AudioPropagationExports.PortalDestroy,
                     AudioPropagationExports.SourceDestroy,
                     AudioPropagationExports.SourceSetAttributes,
                     AudioPropagationExports.SourceSetAudioPaths,
                     AudioPropagationExports.SourceCalculateAudioPaths,
                     AudioPropagationExports.SourceGetAudioPath,
                     AudioPropagationExports.SourceRender,
                     AudioPropagationExports.PortalSetAttributes,
                     AudioPropagationExports.ResetAttributes,
                     AudioPropagationExports.ReportApi,
                 })
        {
            call(ctx);
            Assert.Equal(0, Result(ctx));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("true")]
    public void CreateCalls_FailWithoutWriting_WhenPropagationDisabled(string? value)
    {
        var ctx = NewContext();
        using var _ = PropagationDisabled(value);

        // The game pre-zeroes every out-handle slot; a failing create must
        // leave it that way so the title keeps carrying a null handle into
        // its (log-and-continue) error paths.
        Assert.True(ctx.TryWriteUInt64(OutHandleAddress, 0));

        ctx[CpuRegister.Rdi] = 0x1000;
        ctx[CpuRegister.Rsi] = MemInfoAddress;
        ctx[CpuRegister.Rdx] = OutHandleAddress;
        AudioPropagationExports.SystemCreate(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED, Result(ctx));

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = OutHandleAddress;
        AudioPropagationExports.RoomCreate(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED, Result(ctx));

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x1000;
        ctx[CpuRegister.Rdx] = OutHandleAddress;
        AudioPropagationExports.PortalCreate(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED, Result(ctx));

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = OutHandleAddress;
        AudioPropagationExports.SourceCreate(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED, Result(ctx));

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x1000;
        ctx[CpuRegister.Rdx] = OutHandleAddress;
        AudioPropagationExports.SystemRegisterMaterial(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED, Result(ctx));

        Assert.True(ctx.TryReadUInt64(OutHandleAddress, out var untouched));
        Assert.Equal(0u, untouched);
    }

    [Fact]
    public void QueryMemoryAndZeroCountGetters_StillSucceed_WhenPropagationDisabled()
    {
        var ctx = NewContext();
        using var _ = PropagationDisabled(null);

        // The ctor reads the size back and allocates before SystemCreate can
        // fail, so QueryMemory keeps its normal contract even when disabled.
        ctx[CpuRegister.Rdi] = 0x1000;
        ctx[CpuRegister.Rsi] = MemInfoAddress;
        AudioPropagationExports.SystemQueryMemory(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(MemInfoAddress + 0x18, out var size));
        Assert.True(size > 0);

        // Zero-count getters keep their proven-benign writes: the game reads
        // these counts back unconditionally in its per-frame loops.
        const ulong countAddress = 0x5000;
        Assert.True(ctx.TryWriteUInt32(countAddress, 0x40));
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x6000;
        ctx[CpuRegister.Rdx] = countAddress;
        AudioPropagationExports.SystemGetRays(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(countAddress, out var rays));
        Assert.Equal(0u, rays);

        Assert.True(ctx.TryWriteUInt32(countAddress, 0x1234));
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = countAddress;
        AudioPropagationExports.SourceGetAudioPathCount(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt32(countAddress, out var paths));
        Assert.Equal(0u, paths);
    }
}
