using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Minimal PNG writer (8-bit RGBA, filter 0). Used by GDI CaptureFallback
/// so we do not take a System.Drawing dependency. Safe to unit-test.
/// </summary>
public static class PngRgbaWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void WriteBgra32(string path, int width, int height, ReadOnlySpan<byte> bgra, int stride)
        => File.WriteAllBytes(path, EncodeBgra32(width, height, bgra, stride));

    public static byte[] EncodeBgra32(int width, int height, ReadOnlySpan<byte> bgra, int stride)
        => Encode(width, height, bgra, stride, swapBlueRed: true);

    public static byte[] EncodeRgba32(int width, int height, ReadOnlySpan<byte> rgba, int stride)
        => Encode(width, height, rgba, stride, swapBlueRed: false);

    private static byte[] Encode(int width, int height, ReadOnlySpan<byte> pixels, int stride, bool swapBlueRed)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");
        if (stride < width * 4)
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride is shorter than one BGRA/RGBA row.");

        var rowBytes = width * 4;
        var raw = new byte[(rowBytes + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var dest = 1 + y * (rowBytes + 1);
            raw[y * (rowBytes + 1)] = 0; // filter None
            var src = y * stride;
            if (src + rowBytes > pixels.Length)
                throw new ArgumentException("Pixel buffer is shorter than height*stride.");
            if (!swapBlueRed)
            {
                pixels.Slice(src, rowBytes).CopyTo(raw.AsSpan(dest, rowBytes));
                continue;
            }

            for (var x = 0; x < width; x++)
            {
                var s = src + x * 4;
                var d = dest + x * 4;
                raw[d] = pixels[s + 2];
                raw[d + 1] = pixels[s + 1];
                raw[d + 2] = pixels[s];
                raw[d + 3] = pixels[s + 3];
            }
        }

        using var ms = new MemoryStream();
        ms.Write(Signature);
        WriteChunk(ms, "IHDR"u8, hdr =>
        {
            Span<byte> buf = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(buf, width);
            BinaryPrimitives.WriteInt32BigEndian(buf[4..], height);
            buf[8] = 8;
            buf[9] = 6; // RGBA
            buf[10] = 0;
            buf[11] = 0;
            buf[12] = 0;
            hdr.Write(buf);
        });

        WriteChunk(ms, "IDAT"u8, idat =>
        {
            using var zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true);
            zlib.Write(raw);
        });

        WriteChunk(ms, "IEND"u8, _ => { });
        return ms.ToArray();
    }

    private static void WriteChunk(Stream dest, ReadOnlySpan<byte> type, Action<Stream> writePayload)
    {
        using var payload = new MemoryStream();
        writePayload(payload);
        var data = payload.ToArray();

        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        dest.Write(len);
        dest.Write(type);
        dest.Write(data);

        var crc = Crc32(type, data);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        dest.Write(crcBuf);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
