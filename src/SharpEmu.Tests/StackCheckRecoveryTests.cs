// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// SHARPEMU_IGNORE_STACK_CHK recovery may only fire on the one known
/// __stack_chk_fail call site layout: the canary compare's jne (75 0F), the
/// add rsp, imm8 epilogue (48 83 C4) 20 bytes before the return address, and
/// UD2 (0F 0B) at the return address itself. Anything else must fall through
/// to the fatal HLE export.
/// </summary>
public sealed class StackCheckRecoveryTests
{
    private const int ReturnOffset = 32;

    [Fact]
    public void MatchingEpilogueIsRecoverable()
    {
        using var site = new NativeReturnSite(BuildMatchingSite());
        Assert.True(IsRecoverableStackCheckReturnSite(site.ReturnRip));
    }

    [Fact]
    public void MissingUd2AtReturnAddressIsNotRecoverable()
    {
        var bytes = BuildMatchingSite();
        bytes[ReturnOffset] = 0x90;
        using var site = new NativeReturnSite(bytes);
        Assert.False(IsRecoverableStackCheckReturnSite(site.ReturnRip));
    }

    [Fact]
    public void MissingJneIsNotRecoverable()
    {
        var bytes = BuildMatchingSite();
        bytes[ReturnOffset - 22] = 0x74;
        using var site = new NativeReturnSite(bytes);
        Assert.False(IsRecoverableStackCheckReturnSite(site.ReturnRip));
    }

    [Fact]
    public void MissingAddRspEpilogueIsNotRecoverable()
    {
        var bytes = BuildMatchingSite();
        bytes[ReturnOffset - 19] = 0x81;
        using var site = new NativeReturnSite(bytes);
        Assert.False(IsRecoverableStackCheckReturnSite(site.ReturnRip));
    }

    [Fact]
    public void LowReturnAddressIsNotRecoverable()
    {
        Assert.False(IsRecoverableStackCheckReturnSite(0x10UL));
    }

    private static byte[] BuildMatchingSite()
    {
        var bytes = new byte[64];
        // jne over the failure path.
        bytes[ReturnOffset - 22] = 0x75;
        bytes[ReturnOffset - 21] = 0x0F;
        // add rsp, imm8 on the passing path.
        bytes[ReturnOffset - 20] = 0x48;
        bytes[ReturnOffset - 19] = 0x83;
        bytes[ReturnOffset - 18] = 0xC4;
        // UD2 immediately after the noreturn __stack_chk_fail call.
        bytes[ReturnOffset] = 0x0F;
        bytes[ReturnOffset + 1] = 0x0B;
        return bytes;
    }

    private static bool IsRecoverableStackCheckReturnSite(ulong returnRip)
    {
        var method = typeof(DirectExecutionBackend).GetMethod(
            "IsRecoverableStackCheckReturnSite",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object[] { returnRip })!;
    }

    private sealed class NativeReturnSite : IDisposable
    {
        private readonly nint _buffer;

        public NativeReturnSite(byte[] bytes)
        {
            _buffer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, _buffer, bytes.Length);
            ReturnRip = (ulong)_buffer + ReturnOffset;
        }

        public ulong ReturnRip { get; }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_buffer);
        }
    }
}
