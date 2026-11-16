using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Svfm.Core.Formats;

public enum FtexKind
{
    /// <summary>Colour (_bsm, _alp): texture type 3.</summary>
    Color = 3,
    /// <summary>Normal map (_nrm): texture type 9.</summary>
    Normal = 9,
    /// <summary>Linear data (_srm and others) and UI pictures such as EquipIcon icons: texture type 1.</summary>
    Linear = 1,
}

/// <summary>A written texture: the .ftex header and its .N.ftexs files (key = N).</summary>
public sealed record FtexFiles(byte[] Ftex, IReadOnlyDictionary<int, byte[]> Ftexs);

/// <summary>
/// Writes a texture as .ftex + .N.ftexs, with the layout of every shipped texture checked in the
/// research toolkit: mips run down until the larger side is 4 (256×128 → 7); textures under
/// 512 px keep mip 0 in .2.ftexs and the rest in .1; larger ones mip 0 in .3, mip 1 in .2, the
/// rest in .1; each mip is zlib-compressed in 16 KiB chunks (stored raw when that is smaller).
/// </summary>
public static class FtexWriter
{
    public static FtexFiles Write(RgbaImage image, bool withAlpha, FtexKind kind = FtexKind.Color)
    {
        var mips = new List<RgbaImage> { image };
        int w = image.Width, h = image.Height;
        while (Math.Max(w, h) > 4)
        {
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            mips.Add(Resize.Box(mips[^1], w, h));
        }
        var data = mips.Select(m => withAlpha ? DxtEncoder.EncodeBc3(m) : DxtEncoder.EncodeBc1(m)).ToList();
        bool big = Math.Max(image.Width, image.Height) >= 512;
        int FileOf(int i) => big ? (i == 0 ? 3 : i == 1 ? 2 : 1) : (i == 0 ? 2 : 1);

        var files = new Dictionary<int, MemoryStream>();
        var entries = new byte[mips.Count][];
        for (int i = mips.Count - 1; i >= 0; i--) // smallest first inside each file
        {
            int fn = FileOf(i);
            if (!files.TryGetValue(fn, out var f)) files[fn] = f = new MemoryStream();
            var (blob, chunks) = Chunk(data[i]);
            var e = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)f.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), (uint)data[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), (uint)blob.Length);
            e[12] = (byte)i;
            e[13] = (byte)fn;
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(14), (ushort)chunks);
            entries[i] = e;
            f.Write(blob);
        }

        var hdr = new byte[0x40 + 16 * mips.Count];
        "FTEX"u8.CopyTo(hdr);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), 0x4001EB85); // version 2.03
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(8), (ushort)(withAlpha ? 4 : 2));
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(0x0A), (ushort)image.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(0x0C), (ushort)image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(0x0E), 1);
        hdr[0x10] = (byte)mips.Count;
        hdr[0x11] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(0x12), 0x111);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x1C), 0x01000000u | (uint)kind);
        int fileCount = files.Keys.Max();
        hdr[0x20] = (byte)fileCount;
        hdr[0x21] = (byte)(fileCount - 1);
        MD5.HashData(data.SelectMany(d => d).ToArray()).CopyTo(hdr.AsSpan(0x30));
        for (int i = 0; i < mips.Count; i++) entries[i].CopyTo(hdr, 0x40 + 16 * i);
        return new FtexFiles(hdr, files.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    private static (byte[] blob, int chunks) Chunk(byte[] data)
    {
        const int size = 16384;
        int n = Math.Max(1, (data.Length + size - 1) / size);
        var table = new byte[8 * n];
        var body = new MemoryStream();
        for (int c = 0; c < n; c++)
        {
            var part = data.AsSpan(c * size, Math.Min(size, data.Length - c * size));
            var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true)) zs.Write(part);
            bool raw = z.Length >= part.Length;
            byte[] blob = raw ? part.ToArray() : z.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(c * 8), (ushort)blob.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(c * 8 + 2), (ushort)part.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(c * 8 + 4), (uint)(table.Length + body.Length) | (raw ? 0x80000000u : 0));
            body.Write(blob);
        }
        return ([.. table, .. body.ToArray()], n);
    }
}

/// <summary>Simple resampling for mips and crops.</summary>
public static class Resize
{
    /// <summary>Box filter (average of the covered source pixels), used for mips.</summary>
    public static RgbaImage Box(RgbaImage src, int w, int h)
    {
        var o = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int x0 = x * src.Width / w, x1 = Math.Max(x0 + 1, (x + 1) * src.Width / w);
            int y0 = y * src.Height / h, y1 = Math.Max(y0 + 1, (y + 1) * src.Height / h);
            int r = 0, g = 0, b = 0, a = 0, n = 0;
            for (int sy = y0; sy < y1; sy++)
            for (int sx = x0; sx < x1; sx++)
            {
                var p = src.Pixel(sx, sy);
                r += p[0]; g += p[1]; b += p[2]; a += p[3]; n++;
            }
            var d = o.Pixel(x, y);
            d[0] = (byte)(r / n); d[1] = (byte)(g / n); d[2] = (byte)(b / n); d[3] = (byte)(a / n);
        }
        return o;
    }

    /// <summary>Bilinear resampling, for scaling a texture to the size a crop rule expects.</summary>
    public static RgbaImage Bilinear(RgbaImage src, int w, int h)
    {
        var o = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        {
            float sy = Math.Max(0, (y + 0.5f) * src.Height / h - 0.5f);
            int y0 = Math.Min((int)sy, src.Height - 1), y1 = Math.Min(y0 + 1, src.Height - 1);
            float fy = sy - y0;
            for (int x = 0; x < w; x++)
            {
                float sx = Math.Max(0, (x + 0.5f) * src.Width / w - 0.5f);
                int x0 = Math.Min((int)sx, src.Width - 1), x1 = Math.Min(x0 + 1, src.Width - 1);
                float fx = sx - x0;
                var d = o.Pixel(x, y);
                for (int k = 0; k < 4; k++)
                {
                    float top = src.Pixel(x0, y0)[k] * (1 - fx) + src.Pixel(x1, y0)[k] * fx;
                    float bottom = src.Pixel(x0, y1)[k] * (1 - fx) + src.Pixel(x1, y1)[k] * fx;
                    d[k] = (byte)Math.Clamp((int)MathF.Round(top * (1 - fy) + bottom * fy), 0, 255);
                }
            }
        }
        return o;
    }
}
