// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using Xunit;

namespace SharpEmu.Tests;

[CollectionDefinition("Json exports", DisableParallelization = true)]
public sealed class JsonExportsCollection;

[Collection("Json exports")]
public sealed class JsonValueTreeExportsTests
{
    private const ulong RootAddress = 0x1000;
    private const ulong BufferAddress = 0x4000;
    private const ulong TextAddress = 0x8000;
    private const ulong StringAddress = 0xC000;
    private const ulong ArrayAddress = 0x1_0000;
    private const ulong ObjectAddress = 0x1_4000;
    private const ulong ValueAddress = 0x1_8000;

    private readonly AllocatingGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public JsonValueTreeExportsTests()
    {
        JsonObjectHeap.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void Parse_ExposesEveryValueKindAndAccessor()
    {
        Parse("{\"nil\":null,\"flag\":false,\"signed\":-7,\"unsigned\":18446744073709551615," +
            "\"real\":2.25,\"text\":\"héllo\",\"array\":[1,2],\"object\":{\"x\":1}}");

        Assert.Equal(0UL, GetType(Index("nil")));
        Assert.Equal(1UL, GetType(Index("flag")));
        Assert.Equal(2UL, GetType(Index("signed")));
        Assert.Equal(3UL, GetType(Index("unsigned")));
        Assert.Equal(4UL, GetType(Index("real")));
        Assert.Equal(5UL, GetType(Index("text")));
        Assert.Equal(6UL, GetType(Index("array")));
        Assert.Equal(7UL, GetType(Index("object")));

        Assert.Equal(-7L, ReadInt64(GetScalar(Index("signed"), JsonExports.ValueGetInteger)));
        Assert.Equal(ulong.MaxValue, ReadUInt64(GetScalar(Index("unsigned"), JsonExports.ValueGetUnsignedInteger)));
        Assert.Equal(2.25, BitConverter.Int64BitsToDouble(ReadInt64(GetScalar(Index("real"), JsonExports.ValueGetReal))));
        Assert.Equal(0, ReadByte(GetScalar(Index("flag"), JsonExports.ValueGetBoolean)));

        var text = Index("text");
        _ctx[CpuRegister.Rdi] = text;
        JsonExports.ValueGetString(_ctx);
        var jsonString = _ctx[CpuRegister.Rax];
        _ctx[CpuRegister.Rdi] = jsonString;
        JsonExports.StringLength(_ctx);
        Assert.Equal((ulong)Encoding.UTF8.GetByteCount("héllo"), _ctx[CpuRegister.Rax]);
        _ctx[CpuRegister.Rdi] = jsonString;
        JsonExports.StringCStr(_ctx);
        Assert.Equal("héllo", ReadCString(_ctx[CpuRegister.Rax]));

        _ctx[CpuRegister.Rdi] = Index("array");
        JsonExports.ValueGetArray(_ctx);
        var array = _ctx[CpuRegister.Rax];
        _ctx[CpuRegister.Rdi] = array;
        JsonExports.ArraySize(_ctx);
        Assert.Equal(2UL, _ctx[CpuRegister.Rax]);
        _ctx[CpuRegister.Rdi] = array;
        JsonExports.ArrayBack(_ctx);
        Assert.Equal(2UL, ReadUInt64(GetScalar(_ctx[CpuRegister.Rax], JsonExports.ValueGetUnsignedInteger)));

        _ctx[CpuRegister.Rdi] = Index("object");
        JsonExports.ValueGetObject(_ctx);
        _ctx[CpuRegister.Rdi] = _ctx[CpuRegister.Rax];
        JsonExports.ObjectSize(_ctx);
        Assert.Equal(1UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void WrongTypeGetters_ReturnStableEmptyStorage()
    {
        Parse("{\"text\":\"value\"}");
        var value = Index("text");

        _ctx[CpuRegister.Rdi] = value;
        JsonExports.ValueGetInteger(_ctx);
        var first = _ctx[CpuRegister.Rax];
        _ctx[CpuRegister.Rdi] = value;
        JsonExports.ValueGetInteger(_ctx);

        Assert.NotEqual(0UL, first);
        Assert.Equal(first, _ctx[CpuRegister.Rax]);
        Assert.Equal(0UL, ReadUInt64(first));

        _ctx[CpuRegister.Rdi] = value;
        JsonExports.ValueGetArray(_ctx);
        var emptyArray = _ctx[CpuRegister.Rax];
        _ctx[CpuRegister.Rdi] = emptyArray;
        JsonExports.ArraySize(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = value;
        JsonExports.ValueReferArray(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        _ctx[CpuRegister.Rdi] = value;
        JsonExports.ValueReferObject(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void ObjectAndArrayAccessors_BuildMutableTreeAndSerializeIt()
    {
        _ctx[CpuRegister.Rdi] = ObjectAddress;
        JsonExports.ObjectConstructor(_ctx);
        WriteCString(TextAddress, "answer");
        _ctx[CpuRegister.Rdi] = StringAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        JsonValueExports.StringCStringConstructor(_ctx);

        _ctx[CpuRegister.Rdi] = ObjectAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        JsonExports.ObjectIndex(_ctx);
        var member = _ctx[CpuRegister.Rax];
        _ctx[CpuRegister.Rdi] = member;
        _ctx[CpuRegister.Rsi] = 42;
        JsonValueExports.ValueSetUnsigned(_ctx);

        _ctx[CpuRegister.Rdi] = ValueAddress;
        _ctx[CpuRegister.Rsi] = ObjectAddress;
        JsonValueExports.ValueObjectConstructor(_ctx);
        Assert.Equal(7UL, GetType(ValueAddress));

        _ctx[CpuRegister.Rdi] = ArrayAddress;
        JsonExports.ArrayConstructor(_ctx);
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = ValueAddress;
        JsonExports.ArrayPushBack(_ctx);
        _ctx[CpuRegister.Rdi] = RootAddress;
        _ctx[CpuRegister.Rsi] = ArrayAddress;
        JsonValueExports.ValueArrayConstructor(_ctx);

        _ctx[CpuRegister.Rdi] = RootAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        Assert.Equal(0, JsonExports.ValueSerialize(_ctx));
        _ctx[CpuRegister.Rdi] = StringAddress;
        JsonExports.StringCStr(_ctx);
        Assert.Equal("[{\"answer\":42}]", ReadCString(_ctx[CpuRegister.Rax]));
    }

    [Fact]
    public void ConstructorsAndSetters_UpdateTheAccessorViewAndGuestLayout()
    {
        _ctx[CpuRegister.Rdi] = ValueAddress;
        _ctx[CpuRegister.Rsi] = unchecked((ulong)-12L);
        JsonValueExports.ValueIntegerConstructor(_ctx);
        Assert.Equal(ValueAddress, _ctx[CpuRegister.Rax]);
        Assert.Equal(2UL, GetType(ValueAddress));
        Assert.Equal(-12L, ReadInt64(GetScalar(ValueAddress, JsonExports.ValueGetInteger)));
        Assert.Equal(2u, ReadUInt32(ValueAddress + 0x1C));

        _ctx[CpuRegister.Rdi] = ValueAddress;
        _ctx.SetXmmRegister(0, unchecked((ulong)BitConverter.DoubleToInt64Bits(9.5)), 0);
        JsonValueExports.ValueSetReal(_ctx);
        Assert.Equal(ValueAddress, _ctx[CpuRegister.Rax]);
        Assert.Equal(4UL, GetType(ValueAddress));
        Assert.Equal(9.5, BitConverter.Int64BitsToDouble(ReadInt64(GetScalar(ValueAddress, JsonExports.ValueGetReal))));

        WriteCString(TextAddress, "constructed");
        _ctx[CpuRegister.Rdi] = ValueAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        JsonValueExports.ValueSetCString(_ctx);
        Assert.Equal(5UL, GetType(ValueAddress));
        _ctx[CpuRegister.Rdi] = ValueAddress;
        JsonExports.ValueGetString(_ctx);
        _ctx[CpuRegister.Rdi] = _ctx[CpuRegister.Rax];
        JsonExports.StringCStr(_ctx);
        Assert.Equal("constructed", ReadCString(_ctx[CpuRegister.Rax]));
    }

    [Fact]
    public void InitParameter2_WritesDocumentedFieldsAndTerminateSucceeds()
    {
        _ctx[CpuRegister.Rdi] = RootAddress;
        Assert.Equal(0, JsonExports.InitParameter2Constructor(_ctx));
        _ctx[CpuRegister.Rdi] = RootAddress;
        _ctx[CpuRegister.Rsi] = 0x1111;
        _ctx[CpuRegister.Rdx] = 0x2222;
        JsonExports.InitParameter2SetAllocator(_ctx);
        _ctx[CpuRegister.Rdi] = RootAddress;
        _ctx[CpuRegister.Rsi] = 0x3333;
        JsonExports.InitParameter2SetFileBufferSize(_ctx);
        _ctx[CpuRegister.Rdi] = RootAddress;
        _ctx[CpuRegister.Rsi] = 2;
        JsonExports.InitParameter2SetSpecialFloatFormatType(_ctx);

        Assert.Equal(0x1111UL, ReadUInt64(RootAddress));
        Assert.Equal(0x2222UL, ReadUInt64(RootAddress + 8));
        Assert.Equal(0x3333UL, ReadUInt64(RootAddress + 0x10));
        Assert.Equal(2u, ReadUInt32(RootAddress + 0x18));

        _ctx[CpuRegister.Rdi] = RootAddress;
        Assert.Equal(0, JsonExports.InitializerTerminate(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void ProviderParser_ReadsJsonThroughGuestCallback()
    {
        var previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = new JsonProviderScheduler(Encoding.UTF8.GetBytes("{\"ok\":true}"));
        try
        {
            _ctx[CpuRegister.Rdi] = RootAddress;
            _ctx[CpuRegister.Rsi] = 0x40_0000;
            _ctx[CpuRegister.Rdx] = 0xCAFE;
            Assert.Equal(0, JsonExports.ParserParseProvider(_ctx));
            Assert.Equal(1UL, GetType(Index("ok")));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    [Fact]
    public void Parse_DuplicateObjectMemberUsesLastValue()
    {
        Parse("{\"value\":1,\"value\":2}");
        Assert.Equal(2UL, ReadUInt64(GetScalar(Index("value"), JsonExports.ValueGetUnsignedInteger)));
    }

    private void Parse(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.True(_memory.TryWrite(BufferAddress, bytes));
        _ctx[CpuRegister.Rdi] = RootAddress;
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = (ulong)bytes.Length;
        Assert.Equal(0, JsonExports.ParserParseBuffer(_ctx));
    }

    private ulong Index(string key)
    {
        WriteCString(TextAddress, key);
        _ctx[CpuRegister.Rdi] = RootAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        JsonExports.ValueIndexCString(_ctx);
        return _ctx[CpuRegister.Rax];
    }

    private ulong GetType(ulong address)
    {
        _ctx[CpuRegister.Rdi] = address;
        JsonExports.ValueGetType(_ctx);
        return _ctx[CpuRegister.Rax];
    }

    private ulong GetScalar(ulong address, Func<CpuContext, int> accessor)
    {
        _ctx[CpuRegister.Rdi] = address;
        accessor(_ctx);
        return _ctx[CpuRegister.Rax];
    }

    private void WriteCString(ulong address, string value) =>
        Assert.True(_memory.TryWrite(address, Encoding.UTF8.GetBytes(value + '\0')));

    private string ReadCString(ulong address)
    {
        Assert.True(_ctx.TryReadNullTerminatedUtf8(address, 256, out var value));
        return value;
    }

    private byte ReadByte(ulong address)
    {
        Assert.True(_ctx.TryReadByte(address, out var value));
        return value;
    }

    private ulong ReadUInt64(ulong address)
    {
        Assert.True(_ctx.TryReadUInt64(address, out var value));
        return value;
    }

    private long ReadInt64(ulong address) => unchecked((long)ReadUInt64(address));

    private uint ReadUInt32(ulong address)
    {
        Assert.True(_ctx.TryReadUInt32(address, out var value));
        return value;
    }

    private sealed class AllocatingGuestMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly SparseGuestMemory _memory = new();
        private ulong _nextAddress = 0x6000_0000;

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            _memory.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            _memory.TryWrite(virtualAddress, source);

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = (_nextAddress + alignment - 1) & ~(alignment - 1);
            _nextAddress = address + size;
            return size != 0 && alignment != 0 && (alignment & (alignment - 1)) == 0;
        }
    }

    private sealed class JsonProviderScheduler(byte[] bytes) : IGuestThreadScheduler
    {
        private int _position;

        public bool SupportsGuestContextTransfer => false;

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
        {
            error = "not supported";
            return false;
        }

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
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

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

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
            var result = TryCallGuestFunction(
                callerContext, entryPoint, arg0, arg1, 0, stackAddress, stackSize, reason,
                out _, out error);
            return result;
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
            if (_position >= bytes.Length)
            {
                returnValue = 1;
                return true;
            }

            returnValue = 0;
            return callerContext.Memory.TryWrite(arg0, bytes.AsSpan(_position++, 1));
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
