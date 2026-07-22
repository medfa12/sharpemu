// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;

namespace SharpEmu.Libs.Compression;

internal static class PngCodec
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal readonly record struct Metadata(uint Width, uint Height, byte BitDepth, byte ColorType, byte Interlace)
    {
        public ushort SceColorSpace => ColorType switch { 0 => 2, 2 => 3, 3 => 4, 4 => 18, 6 => 19, _ => 0 };
    }

    internal static bool TryReadMetadata(ReadOnlySpan<byte> png, out Metadata metadata)
    {
        metadata = default;
        if (png.Length < 33 || !png[..8].SequenceEqual(Signature)) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(png[8..]) != 13 || !png.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        var width = BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
        if (width == 0 || height == 0) return false;
        metadata = new Metadata(width, height, png[24], png[25], png[28]);
        return metadata.SceColorSpace != 0;
    }

    internal static bool TryDecodeRgba(ReadOnlySpan<byte> png, out Metadata metadata, out byte[] rgba)
    {
        rgba = [];
        if (!TryReadMetadata(png, out metadata) || metadata.BitDepth != 8 || metadata.Interlace != 0) return false;

        var idat = new MemoryStream();
        byte[] palette = [];
        byte[] transparency = [];
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (length > int.MaxValue || offset + 12L + length > png.Length) return false;
            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, (int)length);
            if (type.SequenceEqual("IDAT"u8)) idat.Write(data);
            else if (type.SequenceEqual("PLTE"u8)) palette = data.ToArray();
            else if (type.SequenceEqual("tRNS"u8)) transparency = data.ToArray();
            offset += checked((int)length + 12);
            if (type.SequenceEqual("IEND"u8)) break;
        }

        var channels = metadata.ColorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0) return false;
        var rowBytesLong = (long)metadata.Width * channels;
        var rawSizeLong = (rowBytesLong + 1) * metadata.Height;
        var outputSizeLong = (long)metadata.Width * metadata.Height * 4;
        if (rowBytesLong > int.MaxValue || rawSizeLong > int.MaxValue || outputSizeLong > int.MaxValue) return false;

        byte[] raw;
        try
        {
            idat.Position = 0;
            using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
            using var decompressed = new MemoryStream((int)rawSizeLong);
            zlib.CopyTo(decompressed);
            raw = decompressed.ToArray();
        }
        catch (InvalidDataException)
        {
            return false;
        }
        if (raw.Length < rawSizeLong) return false;

        var rowBytes = (int)rowBytesLong;
        var recon = new byte[checked(rowBytes * (int)metadata.Height)];
        for (var y = 0; y < (int)metadata.Height; y++)
        {
            var rawRow = y * (rowBytes + 1);
            var row = y * rowBytes;
            var filter = raw[rawRow];
            if (filter > 4) return false;
            for (var x = 0; x < rowBytes; x++)
            {
                var encoded = raw[rawRow + 1 + x];
                var left = x >= channels ? recon[row + x - channels] : 0;
                var up = y > 0 ? recon[row - rowBytes + x] : 0;
                var upperLeft = y > 0 && x >= channels ? recon[row - rowBytes + x - channels] : 0;
                recon[row + x] = filter switch
                {
                    0 => encoded,
                    1 => unchecked((byte)(encoded + left)),
                    2 => unchecked((byte)(encoded + up)),
                    3 => unchecked((byte)(encoded + ((left + up) >> 1))),
                    4 => unchecked((byte)(encoded + Paeth(left, up, upperLeft))),
                    _ => encoded
                };
            }
        }

        rgba = new byte[(int)outputSizeLong];
        var pixelCount = checked((int)((long)metadata.Width * metadata.Height));
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var source = pixel * channels;
            var destination = pixel * 4;
            switch (metadata.ColorType)
            {
                case 0:
                    rgba[destination] = rgba[destination + 1] = rgba[destination + 2] = recon[source];
                    rgba[destination + 3] = 255;
                    break;
                case 2:
                    recon.AsSpan(source, 3).CopyTo(rgba.AsSpan(destination));
                    rgba[destination + 3] = 255;
                    break;
                case 3:
                    var paletteIndex = recon[source];
                    var paletteOffset = paletteIndex * 3;
                    if (paletteOffset + 2 >= palette.Length) return false;
                    palette.AsSpan(paletteOffset, 3).CopyTo(rgba.AsSpan(destination));
                    rgba[destination + 3] = paletteIndex < transparency.Length ? transparency[paletteIndex] : (byte)255;
                    break;
                case 4:
                    rgba[destination] = rgba[destination + 1] = rgba[destination + 2] = recon[source];
                    rgba[destination + 3] = recon[source + 1];
                    break;
                case 6:
                    recon.AsSpan(source, 4).CopyTo(rgba.AsSpan(destination));
                    break;
            }
        }
        return true;
    }

    internal static byte[] Encode(ReadOnlySpan<byte> pixels, uint width, uint height, uint pitch, bool rgba, bool bgr, int compressionLevel)
    {
        var channels = rgba ? 4 : 3;
        var rowBytes = checked((int)width * channels);
        var raw = new byte[checked((rowBytes + 1) * (int)height)];
        for (var y = 0; y < (int)height; y++)
        {
            var inputRow = checked(y * (int)pitch);
            var outputRow = y * (rowBytes + 1) + 1;
            for (var x = 0; x < (int)width; x++)
            {
                var source = inputRow + x * 4;
                var destination = outputRow + x * channels;
                raw[destination] = pixels[source + (bgr ? 2 : 0)];
                raw[destination + 1] = pixels[source + 1];
                raw[destination + 2] = pixels[source + (bgr ? 0 : 2)];
                if (rgba) raw[destination + 3] = pixels[source + 3];
            }
        }

        using var compressed = new MemoryStream();
        var level = compressionLevel == 0 ? CompressionLevel.NoCompression : compressionLevel < 6 ? CompressionLevel.Fastest : CompressionLevel.SmallestSize;
        using (var zlib = new ZLibStream(compressed, level, leaveOpen: true)) zlib.Write(raw);

        using var png = new MemoryStream();
        png.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;
        ihdr[9] = rgba ? (byte)6 : (byte)2;
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        var estimate = left + up - upperLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceUp = Math.Abs(estimate - up);
        var distanceUpperLeft = Math.Abs(estimate - upperLeft);
        return distanceLeft <= distanceUp && distanceLeft <= distanceUpperLeft ? left : distanceUp <= distanceUpperLeft ? up : upperLeft;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        var crc = Crc32(type, data);
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        output.Write(length);
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = uint.MaxValue;
        foreach (var value in first) crc = UpdateCrc(crc, value);
        foreach (var value in second) crc = UpdateCrc(crc, value);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }
}
