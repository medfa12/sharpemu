// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using Xunit;

namespace SharpEmu.Tests;

/// <summary>
/// sce::Json::Parser::parse and the Value/String read-side exports: parse a guest buffer, walk the
/// document through the export entry points, and verify referValue hands out stable per-member
/// child addresses plus the documented parser error codes.
/// </summary>
public sealed class JsonParserExportsTests
{
    private const ulong RootValueAddress = 0x4000;
    private const ulong BufferAddress = 0x8000;
    private const ulong KeyAddress = 0xC000;
    private const ulong StringAddress = 0x1_0000;
    private const ulong SecondStringAddress = 0x1_4000;
    private const int ParserErrorInvalidToken = unchecked((int)0x80920101);
    private const int ParserErrorEmptyBuffer = unchecked((int)0x80920105);

    private readonly AllocatingGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public JsonParserExportsTests()
    {
        JsonObjectHeap.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    private static int Result(CpuContext ctx) => unchecked((int)(uint)ctx[CpuRegister.Rax]);

    private int Parse(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.True(_memory.TryWrite(BufferAddress, bytes));
        _ctx[CpuRegister.Rdi] = RootValueAddress;
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = (ulong)bytes.Length;
        return JsonExports.ParserParseBuffer(_ctx);
    }

    private ulong IndexByKey(ulong valueAddress, string key)
    {
        WriteCString(KeyAddress, key);
        _ctx[CpuRegister.Rdi] = valueAddress;
        _ctx[CpuRegister.Rsi] = KeyAddress;
        JsonExports.ValueIndexCString(_ctx);
        return _ctx[CpuRegister.Rax];
    }

    private ulong GetType(ulong valueAddress)
    {
        _ctx[CpuRegister.Rdi] = valueAddress;
        JsonExports.ValueGetType(_ctx);
        return _ctx[CpuRegister.Rax];
    }

    private void WriteCString(ulong address, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + '\0');
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private ulong ReferValue(ulong valueAddress, string key, ulong stringAddress)
    {
        WriteCString(KeyAddress, key);
        _ctx[CpuRegister.Rdi] = stringAddress;
        _ctx[CpuRegister.Rsi] = KeyAddress;
        JsonValueExports.StringCStringConstructor(_ctx);

        _ctx[CpuRegister.Rdi] = valueAddress;
        _ctx[CpuRegister.Rsi] = stringAddress;
        JsonExports.ValueReferValueByString(_ctx);
        return _ctx[CpuRegister.Rax];
    }

    [Fact]
    public void Parse_WalksDocumentThroughAccessors()
    {
        Assert.Equal(0, Parse(
            "{\"title\":\"astro\",\"count\":3,\"scale\":1.5,\"enabled\":true,\"items\":[10,20,30]}"));

        // Root: object with five members.
        Assert.Equal(7UL, GetType(RootValueAddress));
        _ctx[CpuRegister.Rdi] = RootValueAddress;
        JsonExports.ValueCount(_ctx);
        Assert.Equal(5UL, _ctx[CpuRegister.Rax]);

        // Integer member through operator[](const char*) and the storage pointer getInteger hands
        // back.
        var countValue = IndexByKey(RootValueAddress, "count");
        Assert.NotEqual(0UL, countValue);
        Assert.Equal(2UL, GetType(countValue));
        _ctx[CpuRegister.Rdi] = countValue;
        JsonExports.ValueGetInteger(_ctx);
        Assert.True(_ctx.TryReadUInt64(_ctx[CpuRegister.Rax], out var integer));
        Assert.Equal(3UL, integer);

        // Real member.
        var scaleValue = IndexByKey(RootValueAddress, "scale");
        Assert.Equal(4UL, GetType(scaleValue));
        _ctx[CpuRegister.Rdi] = scaleValue;
        JsonExports.ValueGetReal(_ctx);
        Assert.True(_ctx.TryReadUInt64(_ctx[CpuRegister.Rax], out var realBits));
        Assert.Equal(1.5, BitConverter.Int64BitsToDouble(unchecked((long)realBits)));

        // Boolean member.
        var enabledValue = IndexByKey(RootValueAddress, "enabled");
        Assert.Equal(1UL, GetType(enabledValue));
        _ctx[CpuRegister.Rdi] = enabledValue;
        JsonExports.ValueGetBoolean(_ctx);
        Assert.True(_ctx.TryReadByte(_ctx[CpuRegister.Rax], out var boolean));
        Assert.Equal(1, boolean);

        // Array member through operator[](size_t) and getValue(size_t).
        var itemsValue = IndexByKey(RootValueAddress, "items");
        Assert.Equal(6UL, GetType(itemsValue));
        _ctx[CpuRegister.Rdi] = itemsValue;
        JsonExports.ValueCount(_ctx);
        Assert.Equal(3UL, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rdi] = itemsValue;
        _ctx[CpuRegister.Rsi] = 1;
        JsonExports.ValueIndexPosition(_ctx);
        var secondItem = _ctx[CpuRegister.Rax];
        _ctx[CpuRegister.Rdi] = secondItem;
        JsonExports.ValueGetInteger(_ctx);
        Assert.True(_ctx.TryReadUInt64(_ctx[CpuRegister.Rax], out var secondValue));
        Assert.Equal(20UL, secondValue);

        _ctx[CpuRegister.Rdi] = itemsValue;
        _ctx[CpuRegister.Rsi] = 2;
        JsonExports.ValueGetPosition(_ctx);
        _ctx[CpuRegister.Rdi] = _ctx[CpuRegister.Rax];
        JsonExports.ValueGetInteger(_ctx);
        Assert.True(_ctx.TryReadUInt64(_ctx[CpuRegister.Rax], out var thirdValue));
        Assert.Equal(30UL, thirdValue);

        // String member through toString + c_str.
        var titleValue = IndexByKey(RootValueAddress, "title");
        Assert.Equal(5UL, GetType(titleValue));
        _ctx[CpuRegister.Rdi] = StringAddress;
        JsonExports.StringBaseConstructor(_ctx);
        _ctx[CpuRegister.Rdi] = titleValue;
        _ctx[CpuRegister.Rsi] = StringAddress;
        JsonExports.ValueToString(_ctx);
        _ctx[CpuRegister.Rdi] = StringAddress;
        JsonExports.StringCStr(_ctx);
        Assert.Equal("astro", ReadCString(_ctx[CpuRegister.Rax]));
    }

    [Fact]
    public void IndexByKey_ReturnsStableChildAddress()
    {
        Assert.Equal(0, Parse("{\"count\":3}"));
        var first = IndexByKey(RootValueAddress, "count");
        var second = IndexByKey(RootValueAddress, "count");
        Assert.NotEqual(0UL, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ReferValue_ReturnsStablePerKeyAddresses()
    {
        Assert.Equal(0, Parse("{\"level\":\"title_controller_ship\",\"index\":7}"));

        var first = ReferValue(RootValueAddress, "level", StringAddress);
        Assert.NotEqual(0UL, first);

        // Same (parent, key) through a different String object still resolves to the same child.
        var second = ReferValue(RootValueAddress, "level", SecondStringAddress);
        Assert.Equal(first, second);

        // A different key gets its own child address.
        var other = ReferValue(RootValueAddress, "index", SecondStringAddress);
        Assert.NotEqual(0UL, other);
        Assert.NotEqual(first, other);

        Assert.Equal(5UL, GetType(first));
        Assert.Equal(2UL, GetType(other));
    }

    [Fact]
    public void ReferValue_MissingMemberReturnsNullReference()
    {
        Assert.Equal(0, Parse("{}"));
        Assert.Equal(0UL, ReferValue(RootValueAddress, "missing", StringAddress));
    }

    [Fact]
    public void ValueAssignment_CopiesParsedElement()
    {
        Assert.Equal(0, Parse("{\"count\":42}"));
        var source = IndexByKey(RootValueAddress, "count");

        const ulong destination = 0x1_8000;
        _ctx[CpuRegister.Rdi] = destination;
        JsonExports.ValueBaseConstructor(_ctx);
        _ctx[CpuRegister.Rdi] = destination;
        _ctx[CpuRegister.Rsi] = source;
        JsonExports.ValueAssignment(_ctx);

        Assert.Equal(2UL, GetType(destination));
        _ctx[CpuRegister.Rdi] = destination;
        JsonExports.ValueGetInteger(_ctx);
        Assert.True(_ctx.TryReadUInt64(_ctx[CpuRegister.Rax], out var copied));
        Assert.Equal(42UL, copied);
    }

    [Fact]
    public void Parse_EmptyBufferReturnsEmptyBufferError()
    {
        _ctx[CpuRegister.Rdi] = RootValueAddress;
        _ctx[CpuRegister.Rsi] = BufferAddress;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(ParserErrorEmptyBuffer, JsonExports.ParserParseBuffer(_ctx));
        Assert.Equal(ParserErrorEmptyBuffer, Result(_ctx));
    }

    [Fact]
    public void Parse_MalformedDocumentReturnsInvalidToken()
    {
        Assert.Equal(ParserErrorInvalidToken, Parse("{\"broken\":"));
        Assert.Equal(ParserErrorInvalidToken, Result(_ctx));
    }

    [Fact]
    public void Parse_ToleratesTrailingNulFromCStringLength()
    {
        // strlen()+1 style call: the buffer length covers the terminating NUL.
        Assert.Equal(0, Parse("{\"count\":3}\0"));
        Assert.Equal(2UL, GetType(IndexByKey(RootValueAddress, "count")));
    }

    [Fact]
    public void Parse_AllNulBufferReturnsEmptyBufferError()
    {
        Assert.Equal(ParserErrorEmptyBuffer, Parse("\0\0"));
    }

    [Fact]
    public void Parse_UnreadableBufferReturnsMemoryFault()
    {
        _ctx[CpuRegister.Rdi] = RootValueAddress;
        _ctx[CpuRegister.Rsi] = 0xDEAD_0000;
        _ctx[CpuRegister.Rdx] = 16;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            JsonExports.ParserParseBuffer(_ctx));
    }

    private string ReadCString(ulong address)
    {
        Assert.NotEqual(0UL, address);
        Assert.True(_ctx.TryReadNullTerminatedUtf8(address, 256, out var text));
        return text;
    }

    // SparseGuestMemory plus the bump allocator the child-Value and c_str marshalling paths need.
    private sealed class AllocatingGuestMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly SparseGuestMemory _memory = new();
        private ulong _nextAddress = 0x5000_0000;

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            _memory.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            _memory.TryWrite(virtualAddress, source);

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
            {
                return false;
            }

            address = (_nextAddress + alignment - 1) & ~(alignment - 1);
            _nextAddress = address + size;
            return true;
        }
    }
}
