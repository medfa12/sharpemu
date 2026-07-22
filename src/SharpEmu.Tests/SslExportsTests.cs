// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Tests;

public sealed class SslExportsTests : IDisposable
{
    private const ulong OutputAddress = 0x4000;

    public SslExportsTests() => SslExports.ResetForTests();

    public void Dispose() => SslExports.ResetForTests();

    private static CpuContext NewContext() => new(new SparseGuestMemory(), Generation.Gen5);

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    [Fact]
    public void ConnectionLifecycle_AllocatesAndCascadesHandles()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0x20000;
        SslExports.SslInit(ctx);
        var contextId = Result(ctx);

        ctx[CpuRegister.Rdi] = unchecked((ulong)contextId);
        ctx[CpuRegister.Rsi] = 42;
        SslExports.sceSslCreateConnection(ctx);
        var connectionId = Result(ctx);

        ctx[CpuRegister.Rdi] = unchecked((ulong)connectionId);
        SslExports.sceSslCreateSslConnection(ctx);
        var sslConnectionId = Result(ctx);

        Assert.True(contextId > 0);
        Assert.True(connectionId > 0);
        Assert.True(sslConnectionId > 0);
        Assert.Equal(1, SslExports.ConnectionHandleCount);
        Assert.Equal(1, SslExports.SslConnectionHandleCount);

        ctx[CpuRegister.Rdi] = unchecked((ulong)sslConnectionId);
        SslExports.sceSslConnect(ctx);
        Assert.Equal(0, Result(ctx));

        ctx[CpuRegister.Rdi] = unchecked((ulong)connectionId);
        SslExports.sceSslDeleteConnection(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal(0, SslExports.ConnectionHandleCount);
        Assert.Equal(0, SslExports.SslConnectionHandleCount);
    }

    [Fact]
    public void CertificateLifecycle_RejectsUnknownContextAndReleasesHandle()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 99;
        SslExports.sceSslLoadCert(ctx);
        Assert.Equal(unchecked((int)0x8095F006), Result(ctx));

        ctx[CpuRegister.Rdi] = 0x10000;
        SslExports.SslInit(ctx);
        var contextId = Result(ctx);

        ctx[CpuRegister.Rdi] = unchecked((ulong)contextId);
        SslExports.sceSslLoadCert(ctx);
        var certificateId = Result(ctx);
        Assert.True(certificateId > 0);
        Assert.Equal(1, SslExports.CertificateHandleCount);

        ctx[CpuRegister.Rdi] = unchecked((ulong)certificateId);
        SslExports.sceSslUnloadCert(ctx);
        Assert.Equal(0, Result(ctx));
        Assert.Equal(0, SslExports.CertificateHandleCount);
    }

    [Fact]
    public void CaCertificatesAndPoolStats_WritePackedOutputs()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0x34567;
        SslExports.SslInit(ctx);
        var contextId = Result(ctx);

        ctx[CpuRegister.Rdi] = unchecked((ulong)contextId);
        ctx[CpuRegister.Rsi] = OutputAddress;
        SslExports.sceSslGetCaCerts(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(OutputAddress, out var certs));
        Assert.True(ctx.TryReadUInt64(OutputAddress + 8, out var count));
        Assert.True(ctx.TryReadUInt64(OutputAddress + 16, out var pool));
        Assert.Equal(0UL, certs);
        Assert.Equal(0UL, count);
        Assert.Equal(0UL, pool);

        ctx[CpuRegister.Rsi] = OutputAddress + 0x100;
        SslExports.sceSslGetMemoryPoolStats(ctx);

        Assert.Equal(0, Result(ctx));
        Assert.True(ctx.TryReadUInt64(OutputAddress + 0x100, out var poolSize));
        Assert.True(ctx.TryReadUInt64(OutputAddress + 0x108, out var maximumInUse));
        Assert.True(ctx.TryReadUInt64(OutputAddress + 0x110, out var currentInUse));
        Assert.Equal(0x34567UL, poolSize);
        Assert.Equal(0UL, maximumInUse);
        Assert.Equal(0UL, currentInUse);
    }

    [Fact]
    public void ExportTable_ContainsAllAssignedNidsWithoutDuplicates()
    {
        var exports = typeof(SslExports)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<SysAbiExportAttribute>())
            .Where(attribute => attribute is not null)
            .ToArray();

        Assert.Equal(217, exports.Length);
        Assert.Equal(217, exports.Select(attribute => attribute!.Nid).Distinct().Count());
        Assert.All(exports, attribute => Assert.True((attribute!.Target & Generation.Gen5) != 0));
    }
}
