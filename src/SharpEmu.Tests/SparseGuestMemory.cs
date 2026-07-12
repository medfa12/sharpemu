// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Tests;

/// <summary>Sparse byte-addressable guest memory backed by a dictionary.</summary>
internal sealed class SparseGuestMemory : ICpuMemory
{
    private readonly Dictionary<ulong, byte> _bytes = new();

    public void WriteUInt16(ulong address, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        Write(address, buffer);
    }

    public void WriteUInt32(ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Write(address, buffer);
    }

    public void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Write(address, buffer);
    }

    public void WriteWords(ulong address, params uint[] words)
    {
        for (var i = 0; i < words.Length; i++)
        {
            WriteUInt32(address + (ulong)(i * 4), words[i]);
        }
    }

    private void Write(ulong address, ReadOnlySpan<byte> source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            _bytes[address + (ulong)i] = source[i];
        }
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            if (!_bytes.TryGetValue(virtualAddress + (ulong)i, out var value))
            {
                return false;
            }

            destination[i] = value;
        }

        return true;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        Write(virtualAddress, source);
        return true;
    }
}
