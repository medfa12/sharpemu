// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Tests;

public sealed class NpTusExportsTests
{
    private const ulong NpIdAddress = 0x1000;
    private const ulong ResultAddress = 0x2000;
    private const ulong StatusAddress = 0x3000;
    private const ulong DataAddress = 0x4000;
    private const ulong SlotIdsAddress = 0x5000;
    private const ulong StackAddress = 0x7000;
    private const int InvalidId = unchecked((int)0x8055070E);
    private const int TooManyObjects = unchecked((int)0x80550706);

    private readonly SparseGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public NpTusExportsTests()
    {
        NpTusExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
        Assert.True(_memory.TryWrite(NpIdAddress, new byte[36]));
        _memory.WriteUInt64(StackAddress, 0);
        _memory.WriteUInt64(StackAddress + 8, 0);
        _ctx[CpuRegister.Rsp] = StackAddress;
    }

    [Fact]
    public void ExportCatalog_ContainsEveryAssignedNidOnce()
    {
        var modern = Exports(typeof(NpTusExports));
        var compat = Exports(typeof(NpTusCompatExports));

        Assert.Equal(98, modern.Length);
        Assert.Equal(44, compat.Length);
        Assert.Equal(98, modern.Select(export => export.Nid).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(44, compat.Select(export => export.Nid).Distinct(StringComparer.Ordinal).Count());
        Assert.All(modern, export => Assert.Equal("libSceNpTus", export.LibraryName));
        Assert.All(compat, export => Assert.Equal("libSceNpTusCompat", export.LibraryName));
        Assert.All(modern.Concat(compat), export => Assert.Equal(Generation.Gen5, export.Target));
    }

    [Fact]
    public void ContextAndRequestLifecycle_UsesPackedBoundedHandles()
    {
        var contextId = CreateCompatContext();
        Assert.Equal(1, contextId);

        _ctx[CpuRegister.Rdi] = (ulong)contextId;
        Assert.Equal(0x0001_0001, NpTusExports.sceNpTusCreateRequest(_ctx));
        var requestId = Result();

        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        _ctx[CpuRegister.Rsi] = ResultAddress;
        Assert.Equal(1, NpTusExports.sceNpTusPollAsync(_ctx));

        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        Assert.Equal(0, NpTusExports.sceNpTusSetDataAAsync(_ctx));

        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        _ctx[CpuRegister.Rsi] = ResultAddress;
        Assert.Equal(0, NpTusExports.sceNpTusPollAsync(_ctx));
        Assert.True(_ctx.TryReadInt32(ResultAddress, out var operationResult));
        Assert.Equal(0, operationResult);

        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        Assert.Equal(0, NpTusExports.sceNpTusDeleteRequest(_ctx));
        Assert.Equal(InvalidId, NpTusExports.sceNpTusDeleteRequest(_ctx));
    }

    [Fact]
    public void ContextTable_StopsAtThirtyTwoAndReusesDeletedSlot()
    {
        var contexts = new List<int>();
        for (var index = 0; index < 32; index++)
        {
            contexts.Add(CreateCompatContext());
        }

        Assert.Equal(Enumerable.Range(1, 32), contexts);
        Assert.Equal(TooManyObjects, CreateCompatContext());

        _ctx[CpuRegister.Rdi] = 7;
        Assert.Equal(0, NpTusExports.sceNpTusDeleteNpTitleCtx(_ctx));
        Assert.Equal(7, CreateCompatContext());
    }

    [Fact]
    public void TssGetData_ClearsStatusAndPayloadAndCompletesRequest()
    {
        var requestId = CreateRequest();
        Assert.True(_memory.TryWrite(StatusAddress, Enumerable.Repeat((byte)0xCC, 0x18).ToArray()));
        Assert.True(_memory.TryWrite(DataAddress, Enumerable.Repeat((byte)0xDD, 32).ToArray()));

        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        _ctx[CpuRegister.Rsi] = 4;
        _ctx[CpuRegister.Rdx] = StatusAddress;
        _ctx[CpuRegister.Rcx] = 0x18;
        _ctx[CpuRegister.R8] = DataAddress;
        _ctx[CpuRegister.R9] = 32;
        Assert.Equal(0, NpTusExports.sceNpTssGetDataAsync(_ctx));

        Assert.All(Read(StatusAddress, 0x18), value => Assert.Equal(0, value));
        Assert.All(Read(DataAddress, 32), value => Assert.Equal(0, value));

        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        _ctx[CpuRegister.Rsi] = ResultAddress;
        Assert.Equal(0, NpTusExports.sceNpTusWaitAsync(_ctx));
        Assert.True(_ctx.TryReadInt32(ResultAddress, out var result));
        Assert.Equal(0, result);
    }

    [Fact]
    public void MultiSlotDataStatusA_ValidatesPackingAndClearsWholeArray()
    {
        var requestId = CreateRequest();
        _memory.WriteUInt32(SlotIdsAddress, 2);
        _memory.WriteUInt32(SlotIdsAddress + 4, 9);
        Assert.True(_memory.TryWrite(StatusAddress, Enumerable.Repeat((byte)0xEE, 2 * 0x210).ToArray()));

        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        _ctx[CpuRegister.Rsi] = 123;
        _ctx[CpuRegister.Rdx] = SlotIdsAddress;
        _ctx[CpuRegister.Rcx] = StatusAddress;
        _ctx[CpuRegister.R8] = 2 * 0x210;
        _ctx[CpuRegister.R9] = 2;
        Assert.Equal(0, NpTusExports.sceNpTusGetMultiSlotDataStatusAAsync(_ctx));
        Assert.All(Read(StatusAddress, 2 * 0x210), value => Assert.Equal(0, value));
    }

    private int CreateCompatContext()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = NpIdAddress;
        NpTusCompatExports.sceNpTusCreateNpTitleCtx(_ctx);
        return Result();
    }

    private int CreateRequest()
    {
        var contextId = CreateCompatContext();
        _ctx[CpuRegister.Rdi] = (ulong)contextId;
        NpTusExports.sceNpTusCreateRequest(_ctx);
        return Result();
    }

    private byte[] Read(ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }

    private int Result() => unchecked((int)(uint)_ctx[CpuRegister.Rax]);

    private static SysAbiExportAttribute[] Exports(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<SysAbiExportAttribute>())
            .Where(attribute => attribute != null)
            .Cast<SysAbiExportAttribute>()
            .ToArray();
}
