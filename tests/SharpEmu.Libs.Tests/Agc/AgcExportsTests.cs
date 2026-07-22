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

        Assert.Equal(0, AgcExports.DriverRegisterResource(_ctx));
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
        Assert.Equal(0, AgcExports.DriverUnregisterResource(_ctx));

        RegisterResource(firstOwner, "replacement resource");
    }
}
