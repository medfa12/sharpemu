// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using Xunit;

namespace SharpEmu.Tests;

public sealed class FontExtendedExportsTests
{
    private readonly SparseGuestMemory _memory = new();
    private readonly CpuContext _ctx;

    public FontExtendedExportsTests()
    {
        FontExports.ResetScaleForTests();
        FontExports.ResetAdditionalFontStateForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void LibraryHandle_IsCreatedTypedAndDestroyedThroughItsSlot()
    {
        const ulong slot = 0x1000;
        _memory.Map(slot, 8);
        _ctx[CpuRegister.Rdx] = slot;

        FontExports.FontCreateLibrary(_ctx);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.True(_ctx.TryReadUInt64(slot, out var library));
        Assert.NotEqual(0UL, library);
        Assert.True(_ctx.TryReadUInt16(library, out var magic));
        Assert.Equal(0x0F01, magic);

        _ctx[CpuRegister.Rdi] = slot;
        FontExports.FontDestroyLibrary(_ctx);

        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.True(_ctx.TryReadUInt64(slot, out library));
        Assert.Equal(0UL, library);

        FontExports.FontDestroyLibrary(_ctx);
        Assert.Equal(0x80460004UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void StyleFrame_PointAndPixelGettersUsePackedDpiAndFlags()
    {
        const ulong frame = 0x2000;
        const ulong width = 0x2100;
        const ulong height = 0x2104;
        _memory.Map(frame, 0x60);
        _memory.Map(width, 8);

        _ctx[CpuRegister.Rdi] = frame;
        FontExports.FontStyleFrameInit(_ctx);
        _ctx[CpuRegister.Rsi] = 144;
        _ctx[CpuRegister.Rdx] = 72;
        FontExports.FontStyleFrameSetResolutionDpi(_ctx);

        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(10f), 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(10f), 0);
        FontExports.FontStyleFrameSetScalePoint(_ctx);

        _ctx[CpuRegister.Rsi] = width;
        _ctx[CpuRegister.Rdx] = height;
        FontExports.FontStyleFrameGetScalePixel(_ctx);
        Assert.Equal(20f, ReadFloat(width));
        Assert.Equal(10f, ReadFloat(height));

        FontExports.FontStyleFrameGetScalePoint(_ctx);
        Assert.Equal(10f, ReadFloat(width));
        Assert.Equal(10f, ReadFloat(height));

        FontExports.FontStyleFrameUnsetScale(_ctx);
        FontExports.FontStyleFrameGetScalePixel(_ctx);
        Assert.Equal(0x80460058UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0f, ReadFloat(width));
        Assert.Equal(0f, ReadFloat(height));
    }

    [Fact]
    public void CharacterAccessors_ReadPs5NodeLayoutAndSkipSyntheticLinks()
    {
        const ulong character = 0x3000;
        const ulong synthetic = 0x3200;
        const ulong previous = 0x3400;
        const ulong fontOutput = 0x3600;
        const ulong codeOutput = 0x3608;
        _memory.Map(character, 0xC8);
        _memory.Map(synthetic, 0xC8);
        _memory.Map(previous, 0xC8);
        _memory.Map(fontOutput, 0x10);

        Assert.True(_ctx.TryWriteUInt64(character + 0x00, synthetic));
        Assert.True(_ctx.TryWriteUInt64(synthetic + 0x00, previous));
        Assert.True(_ctx.TryWriteUInt64(character + 0x18, 0x12345678));
        Assert.True(_ctx.TryWriteUInt32(character + 0x28, 0x20));
        Assert.True(_ctx.TryWriteUInt64(character + 0x38, 0x0E00));
        Assert.True(_memory.TryWrite(synthetic + 0x33, [1]));

        _ctx[CpuRegister.Rdi] = character;
        _ctx[CpuRegister.Rsi] = fontOutput;
        _ctx[CpuRegister.Rdx] = codeOutput;
        FontExports.FontCharacterGetTextFontCode(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.True(_ctx.TryReadUInt64(fontOutput, out var font));
        Assert.Equal(0x12345678UL, font);
        Assert.True(_ctx.TryReadUInt32(codeOutput, out var code));
        Assert.Equal(0x20u, code);

        FontExports.FontCharacterLooksWhiteSpace(_ctx);
        Assert.Equal(0x20UL, _ctx[CpuRegister.Rax]);

        FontExports.FontCharacterRefersTextBack(_ctx);
        Assert.Equal(previous, _ctx[CpuRegister.Rax]);
    }

    private float ReadFloat(ulong address)
    {
        Assert.True(_ctx.TryReadUInt32(address, out var bits));
        return BitConverter.UInt32BitsToSingle(bits);
    }

    private sealed class SparseGuestMemory : ICpuMemory, IGuestAddressSpace
    {
        private readonly Dictionary<ulong, byte> _bytes = [];

        public void Map(ulong address, int size)
        {
            for (var index = 0; index < size; index++)
            {
                _bytes[address + (ulong)index] = 0;
            }
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            for (var index = 0; index < destination.Length; index++)
            {
                if (!_bytes.TryGetValue(virtualAddress + (ulong)index, out destination[index]))
                {
                    return false;
                }
            }
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            for (var index = 0; index < source.Length; index++)
            {
                _bytes[virtualAddress + (ulong)index] = source[index];
            }
            return true;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
            => desiredAddress;

        public bool TryAllocateAtOrAbove(
            ulong desiredAddress,
            ulong size,
            bool executable,
            ulong alignment,
            out ulong actualAddress)
        {
            actualAddress = (desiredAddress + alignment - 1) & ~(alignment - 1);
            return true;
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            return false;
        }
    }
}
