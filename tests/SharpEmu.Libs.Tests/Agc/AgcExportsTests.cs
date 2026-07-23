// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OwnerAddress = MemoryBase;
    private const ulong ResourceHandleAddress = MemoryBase + 0x100;
    private const ulong NameAddress = MemoryBase + 0x200;
    private const ulong StackAddress = MemoryBase + 0x300;
    private const ulong RegistrationMemoryAddress = MemoryBase + 0x400;
    private const ulong ResourceAddress = MemoryBase + 0x800;
    private const ulong CommandBufferAddress = MemoryBase + 0x1000;
    private const ulong CommandAddress = MemoryBase + 0x1200;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x2000);
    private readonly CpuContext _ctx;

    public AgcExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    private void InitializeResourceRegistration(uint maxOwners)
    {
        _ctx[CpuRegister.Rdi] = RegistrationMemoryAddress;
        _ctx[CpuRegister.Rsi] = 0x1000;
        _ctx[CpuRegister.Rdx] = maxOwners;

        Assert.Equal(0, AgcExports.DriverInitResourceRegistration(_ctx));
    }

    private uint RegisterOwner(string name)
    {
        _memory.WriteCString(NameAddress, name);
        _ctx[CpuRegister.Rdi] = OwnerAddress;
        _ctx[CpuRegister.Rsi] = NameAddress;

        Assert.Equal(0, AgcExports.DriverRegisterOwner(_ctx));
        Assert.True(_ctx.TryReadUInt32(OwnerAddress, out var owner));
        return owner;
    }

    private uint RegisterResource(uint owner, string name)
    {
        _memory.WriteCString(NameAddress, name);
        Assert.True(_ctx.TryWriteUInt32(StackAddress + sizeof(ulong), 0));
        _ctx[CpuRegister.Rdi] = ResourceHandleAddress;
        _ctx[CpuRegister.Rsi] = owner;
        _ctx[CpuRegister.Rdx] = ResourceAddress;
        _ctx[CpuRegister.Rcx] = 0x100;
        _ctx[CpuRegister.R8] = NameAddress;
        _ctx[CpuRegister.R9] = 0;
        _ctx[CpuRegister.Rsp] = StackAddress;

        // Retail firmware returns SCE_AGC_ERROR_RESOURCE_REGISTRATION_NO_PA_DEBUG
        // even though the internal registry the unregister exports use is populated.
        Assert.Equal(unchecked((int)0x8A6C9018), AgcExports.DriverRegisterResource(_ctx));
        Assert.True(_ctx.TryReadUInt32(ResourceHandleAddress, out var resourceHandle));
        return resourceHandle;
    }

    [Fact]
    public void UnregisterOwnerAndResources_FreesOwnerSlotAndRemovesResources()
    {
        InitializeResourceRegistration(maxOwners: 1);
        var owner = RegisterOwner("streaming batch");
        var resource = RegisterResource(owner, "texture");

        _ctx[CpuRegister.Rdi] = owner;
        Assert.Equal(0, AgcExports.DriverUnregisterOwnerAndResources(_ctx));

        _ctx[CpuRegister.Rdi] = resource;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverUnregisterResource(_ctx));

        var replacementOwner = RegisterOwner("next streaming batch");
        Assert.NotEqual(owner, replacementOwner);

        _ctx[CpuRegister.Rdi] = owner;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverUnregisterOwnerAndResources(_ctx));
    }

    [Fact]
    public void UnregisterAllResourcesForOwner_PreservesOwnerAndOtherOwnersResources()
    {
        InitializeResourceRegistration(maxOwners: 2);
        var firstOwner = RegisterOwner("first owner");
        var firstResource = RegisterResource(firstOwner, "first resource");
        var secondOwner = RegisterOwner("second owner");
        var secondResource = RegisterResource(secondOwner, "second resource");

        _ctx[CpuRegister.Rdi] = firstOwner;
        Assert.Equal(0, AgcExports.DriverUnregisterAllResourcesForOwner(_ctx));

        _ctx[CpuRegister.Rdi] = firstResource;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverUnregisterResource(_ctx));

        _ctx[CpuRegister.Rdi] = secondResource;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverUnregisterResource(_ctx));

        RegisterResource(firstOwner, "replacement resource");
    }

    [Fact]
    public void DcbAcquireMem_AlignsAndReturnsFirstPacketOfCompatibilityPair()
    {
        Assert.True(_ctx.TryWriteUInt64(CommandBufferAddress + 0x10, CommandAddress + 1));
        Assert.True(_ctx.TryWriteUInt64(CommandBufferAddress + 0x18, MemoryBase + 0x1700));
        Assert.True(_ctx.TryWriteUInt32(CommandBufferAddress + 0x30, 0));
        Assert.True(_ctx.TryWriteUInt32(StackAddress + sizeof(ulong), 400));

        _ctx[CpuRegister.Rdi] = CommandBufferAddress;
        _ctx[CpuRegister.Rsi] = 3;
        _ctx[CpuRegister.Rdx] = 0xFFFF_FFFF;
        _ctx[CpuRegister.Rcx] = 0xFFFF_FFFF;
        _ctx[CpuRegister.R8] = 0x0000_0012_3456_7800;
        _ctx[CpuRegister.R9] = 0x1234;
        _ctx[CpuRegister.Rsp] = StackAddress;

        Assert.Equal(0, AgcExports.DcbAcquireMem(_ctx));
        Assert.Equal(CommandAddress + 4, _ctx[CpuRegister.Rax]);
        Assert.Equal(CommandAddress + 68, ReadUInt64(CommandBufferAddress + 0x10));
        Assert.Equal(0xC006_5800u, ReadUInt32(CommandAddress + 4));
        Assert.Equal(0x8600_7FC0u, ReadUInt32(CommandAddress + 8));
        Assert.Equal(0x13u, ReadUInt32(CommandAddress + 12));
        Assert.Equal(0x1234_5678u, ReadUInt32(CommandAddress + 20));
        Assert.Equal(25u, ReadUInt32(CommandAddress + 28));
        Assert.Equal(0x100u, ReadUInt32(CommandAddress + 32));
        Assert.Equal(0xC006_5800u, ReadUInt32(CommandAddress + 36));
        Assert.Equal(0x8000_0000u, ReadUInt32(CommandAddress + 40));
        Assert.Equal(0x7_FEFFu, ReadUInt32(CommandAddress + 64));
    }

    [Fact]
    public void AcbAcquireMem_UsesAlignedCompatibilityPairAndClampedPolling()
    {
        Assert.True(_ctx.TryWriteUInt64(CommandBufferAddress + 0x10, CommandAddress + 2));
        Assert.True(_ctx.TryWriteUInt64(CommandBufferAddress + 0x18, MemoryBase + 0x1700));
        Assert.True(_ctx.TryWriteUInt32(CommandBufferAddress + 0x30, 0));

        _ctx[CpuRegister.Rdi] = CommandBufferAddress;
        _ctx[CpuRegister.Rsi] = 0xFFFF_FFFF;
        _ctx[CpuRegister.Rdx] = 0x0000_0012_3456_7800;
        _ctx[CpuRegister.Rcx] = 0x100;
        _ctx[CpuRegister.R8] = uint.MaxValue;

        Assert.Equal(0, AgcExports.AcbAcquireMem(_ctx));
        Assert.Equal(CommandAddress + 4, _ctx[CpuRegister.Rax]);
        Assert.Equal(CommandAddress + 68, ReadUInt64(CommandBufferAddress + 0x10));
        Assert.Equal(0u, ReadUInt32(CommandAddress + 8));
        Assert.Equal(0u, ReadUInt32(CommandAddress + 12));
        Assert.Equal(0xFFFFu, ReadUInt32(CommandAddress + 28));
        Assert.Equal(0x100u, ReadUInt32(CommandAddress + 32));
        Assert.Equal(0xC006_5800u, ReadUInt32(CommandAddress + 36));
        Assert.Equal(0x7_FEFFu, ReadUInt32(CommandAddress + 64));
    }

    [Fact]
    public void AcquireMemGetSize_ReturnsNormalInitializationMaximum()
    {
        Assert.Equal(0, AgcExports.DcbAcquireMemGetSize(_ctx));
        Assert.Equal(64UL, _ctx[CpuRegister.Rax]);

        Assert.Equal(0, AgcExports.AcbAcquireMemGetSize(_ctx));
        Assert.Equal(64UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void DcbAcquireMem_AdvancesOnePacketWhenCompatibilityPacketIsNotNeeded()
    {
        Assert.True(_ctx.TryWriteUInt64(CommandBufferAddress + 0x10, CommandAddress));
        Assert.True(_ctx.TryWriteUInt64(CommandBufferAddress + 0x18, MemoryBase + 0x1700));
        Assert.True(_ctx.TryWriteUInt32(CommandBufferAddress + 0x30, 0));
        Assert.True(_ctx.TryWriteUInt32(StackAddress + sizeof(ulong), 400));
        _ctx[CpuRegister.Rdi] = CommandBufferAddress;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0;
        _ctx[CpuRegister.R8] = 0;
        _ctx[CpuRegister.R9] = 0;
        _ctx[CpuRegister.Rsp] = StackAddress;

        Assert.Equal(0, AgcExports.DcbAcquireMem(_ctx));
        Assert.Equal(CommandAddress, _ctx[CpuRegister.Rax]);
        Assert.Equal(CommandAddress + 32, ReadUInt64(CommandBufferAddress + 0x10));
        Assert.Equal(25u, ReadUInt32(CommandAddress + 24));
    }

    [Fact]
    public void IndirectRegisterAddressPatches_UseSharpEmuLayoutWithoutOpcodeValidation()
    {
        var patches = new Func<CpuContext, int>[]
        {
            AgcExports.SetCxRegIndirectPatchSetAddress,
            AgcExports.SetShRegIndirectPatchSetAddress,
            AgcExports.SetUcRegIndirectPatchSetAddress,
        };

        foreach (var patch in patches)
        {
            Assert.True(_ctx.TryWriteUInt32(CommandAddress, 0xDEAD_BEEF));
            Assert.True(_ctx.TryWriteUInt32(CommandAddress + 4, 17));
            _ctx[CpuRegister.Rdi] = CommandAddress;
            _ctx[CpuRegister.Rsi] = 0x0000_1234_5678_9ABC;

            Assert.Equal(0, patch(_ctx));
            Assert.Equal(17u, ReadUInt32(CommandAddress + 4));
            Assert.Equal(0x0000_1234_5678_9ABCUL, ReadUInt64(CommandAddress + 8));
        }
    }

    [Fact]
    public void CondExecAddressPatch_IsLenientAndPreservesLowModeBits()
    {
        Assert.True(_ctx.TryWriteUInt32(CommandAddress, 0xDEAD_BEEF));
        Assert.True(_ctx.TryWriteUInt32(CommandAddress + 4, 3));
        _ctx[CpuRegister.Rdi] = CommandAddress;
        _ctx[CpuRegister.Rsi] = 0x0000_1234_5678_9AB8;

        Assert.Equal(0, AgcExports.CondExecPatchSetCommandAddress(_ctx));
        Assert.Equal(0x5678_9ABBu, ReadUInt32(CommandAddress + 4));
        Assert.Equal(0x0000_1234u, ReadUInt32(CommandAddress + 8));
    }

    [Fact]
    public void DmaDataSourceAddressPatch_UsesSharpEmuSourceFieldWithoutOpcodeValidation()
    {
        Assert.True(_ctx.TryWriteUInt32(CommandAddress, 0xDEAD_BEEF));
        Assert.True(_ctx.TryWriteUInt64(CommandAddress + 8, 0x1111_2222_3333_4444));
        _ctx[CpuRegister.Rdi] = CommandAddress;
        _ctx[CpuRegister.Rsi] = 0x1234_5678_9ABC_DEF0;

        Assert.Equal(0, AgcExports.DmaDataPatchSetSrcAddressOrOffsetOrImmediate(_ctx));
        Assert.Equal(0x1111_2222_3333_4444UL, ReadUInt64(CommandAddress + 8));
        Assert.Equal(0x1234_5678_9ABC_DEF0UL, ReadUInt64(CommandAddress + 24));
    }

    [Theory]
    [InlineData(0x3C, 8)]
    [InlineData(0x10, 4)]
    [InlineData(0xEE, 4)]
    public void WaitRegMemAddressPatch_UsesEmitterLayoutAndFallsBackLeniently(uint opcode, ulong fieldOffset)
    {
        Assert.True(_ctx.TryWriteUInt32(CommandAddress, 0xC000_0000u | (opcode << 8)));
        _ctx[CpuRegister.Rdi] = CommandAddress;
        _ctx[CpuRegister.Rsi] = 0x1234_5678_9ABC_DEF0;

        Assert.Equal(0, AgcExports.WaitRegMemPatchAddress(_ctx));
        Assert.Equal(0x1234_5678_9ABC_DEF0UL, ReadUInt64(CommandAddress + fieldOffset));
    }

    [Fact]
    public void EndOfPipeAddressPatch_IsLenientAndPreservesLowModeBits()
    {
        Assert.True(_ctx.TryWriteUInt32(CommandAddress, 0xDEAD_BEEF));
        Assert.True(_ctx.TryWriteUInt32(CommandAddress + 12, 2));
        _ctx[CpuRegister.Rdi] = CommandAddress;
        _ctx[CpuRegister.Rsi] = 0x1234_5678_9ABC_DEF0;

        Assert.Equal(0, AgcExports.QueueEndOfPipeActionPatchAddress(_ctx));
        Assert.Equal(0x9ABC_DEF2u, ReadUInt32(CommandAddress + 12));
        Assert.Equal(0x1234_5678u, ReadUInt32(CommandAddress + 16));
    }

    [Fact]
    public void AddressPatches_RejectOnlyNullCommandPointers()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0x1234;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.WaitRegMemPatchAddress(_ctx));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.QueueEndOfPipeActionPatchAddress(_ctx));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.SetShRegIndirectPatchSetAddress(_ctx));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.CondExecPatchSetCommandAddress(_ctx));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DmaDataPatchSetSrcAddressOrOffsetOrImmediate(_ctx));
    }

    private uint ReadUInt32(ulong address)
    {
        Assert.True(_ctx.TryReadUInt32(address, out var value));
        return value;
    }

    private ulong ReadUInt64(ulong address)
    {
        Assert.True(_ctx.TryReadUInt64(address, out var value));
        return value;
    }
}
