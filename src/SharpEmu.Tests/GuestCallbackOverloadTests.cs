// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// The guest callback entry points come in two shapes: the legacy overload
/// (no arg2, return value discarded) and the full overload that forwards arg2
/// in Rdx and surfaces the callback's Rax through returnValue. The legacy
/// overload must be a thin wrapper over the full one so both share validation,
/// stack setup, and blocked-callback resume. Native guest execution cannot run
/// under the test host, so these tests pin the shared validation prologue on
/// the real backend and the returnValue contract at the scheduler interface.
/// </summary>
public sealed class GuestCallbackOverloadTests
{
    private const string InvalidEntryError = "invalid guest callback entry=0x0000000000000010";

    // The validation prologue runs before any backend state is touched, so an
    // uninitialized instance (the constructor needs host-native TLS and
    // symbols) exercises the real production code path.
    private static DirectExecutionBackend NewBackend()
        => (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));

    [Fact]
    public void ReturnValueOverload_InvalidEntryPoint_FailsWithZeroReturnValue()
    {
        var backend = NewBackend();
        Assert.False(backend.TryCallGuestFunction(
            null!, 0x10, 1, 2, 3, 0, 0, "test", out var returnValue, out var error));
        Assert.Equal(0UL, returnValue);
        Assert.Equal(InvalidEntryError, error);
    }

    [Fact]
    public void LegacyOverload_DelegatesToReturnValueOverload()
    {
        var backend = NewBackend();
        Assert.False(backend.TryCallGuestFunction(
            null!, 0x10, 1, 2, 0, 0, "test", out var error));
        Assert.Equal(InvalidEntryError, error);
    }

    [Fact]
    public void ReturnValueOverload_RequiresVirtualMemoryBackedCaller()
    {
        var backend = NewBackend();
        var context = new CpuContext(new PlainMemory(), Generation.Gen5);
        Assert.False(backend.TryCallGuestFunction(
            context, 0x40_0000, 1, 2, 3, 0, 0, "test", out var returnValue, out var error));
        Assert.Equal(0UL, returnValue);
        Assert.Equal("caller context memory is not backed by IVirtualMemory", error);
    }

    [Fact]
    public void SchedulerInterface_SurfacesCallbackRaxThroughReturnValue()
    {
        IGuestThreadScheduler scheduler = new RaxScheduler(0xDEAD_BEEF_CAFE_F00DUL);
        Assert.True(scheduler.TryCallGuestFunction(
            null!, 0x40_0000, 1, 2, 3, 0, 0, "test", out var returnValue, out var error));
        Assert.Null(error);
        Assert.Equal(0xDEAD_BEEF_CAFE_F00DUL, returnValue);
    }

    private sealed class PlainMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }

    /// <summary>
    /// Stand-in for a scheduler whose guest callback left a value in Rax;
    /// pins the interface contract that the value reaches the HLE caller.
    /// </summary>
    private sealed class RaxScheduler(ulong rax) : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => false;

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryJoinThread(CpuContext callerContext, ulong threadHandle, out ulong returnValue, out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => Array.Empty<GuestThreadSnapshot>();

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            return TryCallGuestFunction(
                callerContext,
                entryPoint,
                arg0,
                arg1,
                0,
                stackAddress,
                stackSize,
                reason,
                out _,
                out error);
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            error = null;
            returnValue = rax;
            return true;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }
    }
}
