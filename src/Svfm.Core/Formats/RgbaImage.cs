namespace Svfm.Core.Formats;

/// <summary>A plain 8-bit RGBA picture (row-major, 4 bytes per pixel).</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height, byte[]? pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4) throw new ArgumentException("pixel buffer size");
    }

    public static RgbaImage FromBgra(int w, int h, ReadOnlySpan<byte> bgra)
    {
        var img = new RgbaImage(w, h);
        for (int i = 0; i < w * h; i++)
        {
            img.Pixels[i * 4] = bgra[i * 4 + 2];
            img.Pixels[i * 4 + 1] = bgra[i * 4 + 1];
            img.Pixels[i * 4 + 2] = bgra[i * 4];
            img.Pixels[i * 4 + 3] = bgra[i * 4 + 3];
        }
        return img;
    }

    public Span<byte> Pixel(int x, int y) => Pixels.AsSpan((y * Width + x) * 4, 4);

    public RgbaImage Crop(int x, int y, int w, int h)
    {
        var o = new RgbaImage(w, h);
        for (int row = 0; row < h; row++)
            Pixels.AsSpan(((y + row) * Width + x) * 4, w * 4).CopyTo(o.Pixels.AsSpan(row * w * 4));
        return o;
    }
}

/// <summary>BC1/BC2/BC3 (DXT1/3/5) block decoding.</summary>
public static class Dxt
{
    public static RgbaImage DecodeBc1(ReadOnlySpan<byte> data, int w, int h) => Decode(data, w, h, 8, alphaMode: 0);
    public static RgbaImage DecodeBc2(ReadOnlySpan<byte> data, int w, int h) => Decode(data, w, h, 16, alphaMode: 2);
    public static RgbaImage DecodeBc3(ReadOnlySpan<byte> data, int w, int h) => Decode(data, w, h, 16, alphaMode: 3);

    private static RgbaImage Decode(ReadOnlySpan<byte> data, int w, int h, int blockSize, int alphaMode)
    {
        var img = new RgbaImage(w, h);
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[16];
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            int o = (by * bw + bx) * blockSize;
            if (o + blockSize > data.Length) return img;
            var block = data.Slice(o, blockSize);
            var colorBlock = alphaMode == 0 ? block : block[8..];
            alphas.Fill(255);
            if (alphaMode == 2)
                for (int i = 0; i < 16; i++) alphas[i] = (byte)(((block[i / 2] >> (4 * (i % 2))) & 0xF) * 17);
            else if (alphaMode == 3)
                DecodeBc3Alpha(block, alphas);

            ushort c0 = (ushort)(colorBlock[0] | colorBlock[1] << 8);
            ushort c1 = (ushort)(colorBlock[2] | colorBlock[3] << 8);
            Expand565(c0, colors[..4]);
            Expand565(c1, colors.Slice(4, 4));
            bool fourColor = c0 > c1 || alphaMode != 0;
            for (int k = 0; k < 3; k++)
            {
                int a = colors[k], b = colors[4 + k];
                if (fourColor)
                {
                    colors[8 + k] = (byte)((2 * a + b) / 3);
                    colors[12 + k] = (byte)((a + 2 * b) / 3);
                }
                else
                {
                    colors[8 + k] = (byte)((a + b) / 2);
                    colors[12 + k] = 0;
                }
            }
            colors[3] = colors[7] = colors[11] = 255;
            colors[15] = fourColor ? (byte)255 : (byte)0;
            uint bits = (uint)(colorBlock[4] | colorBlock[5] << 8 | colorBlock[6] << 16 | colorBlock[7] << 24);
            for (int i = 0; i < 16; i++)
            {
                int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                if (x >= w || y >= h) continue;
                int idx = (int)((bits >> (2 * i)) & 3);
                var px = img.Pixel(x, y);
                colors.Slice(idx * 4, 3).CopyTo(px);
                px[3] = alphaMode == 0 ? colors[idx * 4 + 3] : alphas[i];
            }
        }
        return img;
    }

    private static void Expand565(ushort c, Span<byte> rgba)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
        rgba[3] = 255;
    }

    private static void DecodeBc3Alpha(ReadOnlySpan<byte> block, Span<byte> alphas)
    {
        int a0 = block[0], a1 = block[1];
        Span<int> table = stackalloc int[8];
        table[0] = a0;
        table[1] = a1;
        if (a0 > a1)
            for (int i = 1; i < 7; i++) table[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (int i = 1; i < 5; i++) table[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            table[6] = 0;
            table[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)block[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) alphas[i] = (byte)table[(int)((bits >> (3 * i)) & 7)];
    }
}
