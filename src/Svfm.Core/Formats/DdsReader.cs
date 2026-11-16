namespace Svfm.Core.Formats;

/// <summary>Reads the top mip of a .dds picture: DXT1/3/5 (BC1/2/3) or uncompressed 24/32-bit.</summary>
public static class DdsReader
{
    public static RgbaImage Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') throw new InvalidDataException("Not a DDS file.");
        int h = BitConverter.ToInt32(d[12..16]), w = BitConverter.ToInt32(d[16..20]);
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) throw new InvalidDataException("The DDS size is not valid.");
        uint pfFlags = BitConverter.ToUInt32(d[80..84]);
        string four = System.Text.Encoding.ASCII.GetString(d.Slice(84, 4));
        int bits = BitConverter.ToInt32(d[88..92]);
        uint rMask = BitConverter.ToUInt32(d[92..96]), gMask = BitConverter.ToUInt32(d[96..100]), bMask = BitConverter.ToUInt32(d[100..104]), aMask = BitConverter.ToUInt32(d[104..108]);
        var data = d[128..];
        if ((pfFlags & 4) != 0)
        {
            if (four == "DX10") throw new InvalidDataException("DDS files with a DX10 header aren't supported. Save it as DXT1 or DXT5, or use a PNG.");
            return four switch
            {
                "DXT1" => Dxt.DecodeBc1(data, w, h),
                "DXT3" => Dxt.DecodeBc2(data, w, h),
                "DXT5" => Dxt.DecodeBc3(data, w, h),
                _ => throw new InvalidDataException($"The DDS format {four} isn't supported. Use DXT1, DXT5 or uncompressed, or a PNG."),
            };
        }
        if ((pfFlags & 0x40) != 0 && (bits == 32 || bits == 24))
        {
            int bpp = bits / 8;
            var img = new RgbaImage(w, h);
            if (data.Length < w * h * bpp) throw new InvalidDataException("The DDS file is cut short.");
            for (int i = 0; i < w * h; i++)
            {
                uint px = 0; for (int k = 0; k < bpp; k++) px |= (uint)data[i * bpp + k] << (8 * k);
                img.Pixels[i * 4] = Channel(px, rMask); img.Pixels[i * 4 + 1] = Channel(px, gMask); img.Pixels[i * 4 + 2] = Channel(px, bMask);
                img.Pixels[i * 4 + 3] = aMask == 0 ? (byte)255 : Channel(px, aMask);
            }
            return img;
        }
        throw new InvalidDataException("This kind of DDS isn't supported. Use DXT1, DXT5 or uncompressed, or a PNG.");
    }

    private static byte Channel(uint px, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)(((px & mask) >> shift) * 255 / max);
    }
}
