// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Logging;

namespace SharpEmu.Core.Cpu;

/// <summary>
/// Registry for the main executable's PT_TLS initialization image. AMD64 uses
/// TLS Variant II: the static TLS block sits immediately below the thread
/// pointer, so every thread must see the module's .tdata bytes at
/// [fs - BlockSize, fs - BlockSize + filesz) with the .tbss tail zeroed.
/// SharpEmu's guest modules are statically linked, so a single main-module
/// template (no DTV/__tls_get_addr machinery) is sufficient.
/// </summary>
public static class GuestTlsImage
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("Cpu");

    private sealed record Template(byte[] InitImage, ulong MemorySize, ulong Alignment, ulong BlockSize);

    private static volatile Template? _template;

    /// <summary>True when a PT_TLS template with a non-zero memory size is registered.</summary>
    public static bool HasImage => _template is { BlockSize: > 0 };

    /// <summary>Copy of the registered .tdata initialization bytes.</summary>
    public static byte[] InitImage => _template is { } template ? (byte[])template.InitImage.Clone() : [];

    /// <summary>PT_TLS p_memsz (tdata + tbss) of the registered template.</summary>
    public static ulong MemorySize => _template?.MemorySize ?? 0;

    /// <summary>PT_TLS p_align of the registered template.</summary>
    public static ulong Alignment => _template?.Alignment ?? 1;

    /// <summary>
    /// Variant II static block size: the thread-pointer-relative offset of the
    /// module's TLS block, i.e. the block occupies [tp - BlockSize, tp).
    /// </summary>
    public static ulong BlockSize => _template?.BlockSize ?? 0;

    /// <summary>
    /// Registers the main module's PT_TLS template. <paramref name="vaddrBias"/>
    /// is the segment's p_vaddr; only its congruence modulo the alignment
    /// matters for the Variant II offset calculation.
    /// </summary>
    public static void Set(ReadOnlySpan<byte> initImage, ulong memorySize, ulong alignment, ulong vaddrBias = 0)
    {
        var normalizedAlignment = alignment == 0 ? 1UL : alignment;
        if ((normalizedAlignment & (normalizedAlignment - 1)) != 0)
        {
            throw new InvalidDataException($"PT_TLS alignment 0x{alignment:X} is not a power of two.");
        }

        if (memorySize > int.MaxValue || (ulong)initImage.Length > memorySize)
        {
            throw new InvalidDataException("PT_TLS template size is invalid or exceeds the supported process limit.");
        }

        var blockSize = memorySize == 0
            ? 0UL
            : CalculateStaticOffset(0, memorySize, normalizedAlignment, vaddrBias & (normalizedAlignment - 1));
        _template = new Template(initImage.ToArray(), memorySize, normalizedAlignment, blockSize);
    }

    /// <summary>Clears the registered template.</summary>
    public static void Reset()
    {
        _template = null;
    }

    /// <summary>
    /// FreeBSD/AMD64 Variant II offset selection: the smallest offset with
    /// offset - previousOffset &gt;= size and (-offset) % align == alignmentBias.
    /// </summary>
    public static ulong CalculateStaticOffset(ulong previousOffset, ulong size, ulong alignment, ulong alignmentBias)
    {
        var result = checked(previousOffset + size + alignment - 1);
        return result - ((result + alignmentBias) & (alignment - 1));
    }

    /// <summary>
    /// Seeds one thread's static TLS block below <paramref name="tlsBase"/>:
    /// zeroes [tlsBase - BlockSize, tlsBase) and copies the initialization
    /// image to tlsBase - BlockSize, leaving the .tbss tail zero. Returns
    /// false only when the mapped TLS region rejects the write; an oversized
    /// template is logged and skipped rather than corrupting nearby memory.
    /// </summary>
    public static bool TrySeedThreadBlock(ICpuMemory memory, ulong tlsBase, ulong mappedPrefixSize)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (_template is not { BlockSize: > 0 } template)
        {
            return true;
        }

        if (template.BlockSize > mappedPrefixSize || template.BlockSize > tlsBase)
        {
            Log.Error(
                $"PT_TLS static block 0x{template.BlockSize:X} bytes exceeds the mapped " +
                $"0x{mappedPrefixSize:X}-byte TLS prefix below 0x{tlsBase:X16}; leaving TLS unseeded.");
            return true;
        }

        var block = new byte[template.BlockSize];
        template.InitImage.CopyTo(block, 0);
        return memory.TryWrite(tlsBase - template.BlockSize, block);
    }
}
