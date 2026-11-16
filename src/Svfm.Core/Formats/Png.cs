using System.Buffers.Binary;
using System.IO.Compression;

namespace Svfm.Core.Formats;

/// <summary>Minimal PNG writer (8-bit RGBA, no filtering), for previews and icon backups.</summary>
public static class Png
{
    /// <summary>
    /// Reads a PNG (8- or 16-bit grey, grey+alpha, RGB, RGBA or palette; not interlaced) as RGBA, for pictures the user
    /// gives (icons).
    /// </summary>
    public static RgbaImage Decode(byte[] png)
    {
        try { return DecodeChecked(png); } catch (Exception e) when (Damaged.Is(e)) { throw Damaged.Error("picture (.png)", e); }
    }

    private static RgbaImage DecodeChecked(byte[] png)
    {
        ReadOnlySpan<byte> sig = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(sig)) throw new InvalidDataException("Not a PNG file.");
        int w = 0, h = 0, depth = 0, type = 0;
        byte[]? palette = null, trns = null;
        var idat = new MemoryStream();
        for (int p = 8; p + 8 <= png.Length;)
        {
            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            string kind = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            var data = png.AsSpan(p + 8, len);
            switch (kind)
            {
                case "IHDR":
                    w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data);
                    h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    depth = data[8]; type = data[9];
                    if (data[12] != 0) throw new InvalidDataException("Interlaced PNGs are not supported; save it without interlacing.");
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            if (kind == "IEND") break;
            p += 12 + len;
        }
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) throw new InvalidDataException("PNG size out of range.");
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG colour type {type} not supported.") };
        if (depth != 8 && !(depth == 16 && type != 3) && !(type == 3 && depth is 1 or 2 or 4))
            throw new InvalidDataException($"PNG bit depth {depth} not supported.");
        int bitsPerPixel = channels * depth;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        int stride = (w * bitsPerPixel + 7) / 8;
        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        var r = raw.ToArray();
        if (r.Length < (stride + 1) * h) throw new InvalidDataException("PNG image data is truncated.");
        var cur = new byte[stride];
        var prev = new byte[stride];
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        {
            int f = r[y * (stride + 1)];
            Array.Copy(r, y * (stride + 1) + 1, cur, 0, stride);
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? cur[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                cur[x] = (byte)(cur[x] + f switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException("Bad PNG filter."),
                });
            }
            for (int x = 0; x < w; x++)
            {
                var d = img.Pixel(x, y);
                int S(int ch) => depth == 16 ? cur[(x * channels + ch) * 2] : cur[x * channels + ch];
                int Full(int ch) => depth == 16 ? cur[(x * channels + ch) * 2] << 8 | cur[(x * channels + ch) * 2 + 1] : cur[x * channels + ch];
                // Grey and RGB name one clear colour in tRNS, a 16-bit value per channel.
                bool Clear(int n)
                {
                    if (trns == null || trns.Length < n * 2) return false;
                    for (int ch = 0; ch < n; ch++) if (Full(ch) != (trns[ch * 2] << 8 | trns[ch * 2 + 1])) return false;
                    return true;
                }
                switch (type)
                {
                    case 0: d[0] = d[1] = d[2] = (byte)S(0); d[3] = Clear(1) ? (byte)0 : (byte)255; break;
                    case 2: d[0] = (byte)S(0); d[1] = (byte)S(1); d[2] = (byte)S(2); d[3] = Clear(3) ? (byte)0 : (byte)255; break;
                    case 4: d[0] = d[1] = d[2] = (byte)S(0); d[3] = (byte)S(1); break;
                    case 6: d[0] = (byte)S(0); d[1] = (byte)S(1); d[2] = (byte)S(2); d[3] = (byte)S(3); break;
                    case 3:
                        int bit = x * depth, idx = (cur[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
                        if (palette == null || idx * 3 + 2 >= palette.Length) throw new InvalidDataException("PNG palette missing.");
                        d[0] = palette[idx * 3]; d[1] = palette[idx * 3 + 1]; d[2] = palette[idx * 3 + 2];
                        d[3] = trns != null && idx < trns.Length ? trns[idx] : (byte)255;
                        break;
                }
            }
            (prev, cur) = (cur, prev);
        }
        return img;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static byte[] Encode(RgbaImage img)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < img.Height; y++)
        {
            raw.WriteByte(0);
            raw.Write(img.Pixels, y * img.Width * 4, img.Width * 4);
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zs);

        var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        Chunk(o, "IHDR", ihdr);
        Chunk(o, "IDAT", z.ToArray());
        Chunk(o, "IEND", []);
        return o.ToArray();
    }

    private static void Chunk(Stream o, string type, byte[] data)
    {
        Span<byte> b4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b4, data.Length);
        o.Write(b4);
        byte[] typeAndData = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) typeAndData[i] = (byte)type[i];
        data.CopyTo(typeAndData, 4);
        o.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(b4, Crc32(typeAndData));
        o.Write(b4);
    }

    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
