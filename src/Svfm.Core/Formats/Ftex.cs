using System.Buffers.Binary;
using System.IO.Compression;

namespace Svfm.Core.Formats;

/// <summary>Where one mip level lives: which .N.ftexs file, at what offset, in how many zlib chunks.</summary>
public sealed record FtexMip(int Index, int FileNumber, int Offset, int UncompressedSize, int StoredSize, int ChunkCount)
{
    public int Width(FtexTexture t) => Math.Max(1, t.Width >> Index);
    public int Height(FtexTexture t) => Math.Max(1, t.Height >> Index);
}

/// <summary>
/// Fox texture header (.ftex). The pixel data is in .1.ftexs, .2.ftexs ... next to it, or in a
/// texture pack (.pftxs), which holds the same parts. Large textures keep only their small mips in
/// the pack; the big ones are loose files in the texture archives.
/// </summary>
public sealed class FtexTexture
{
    public byte[] Header { get; }
    public ushort PixelFormat => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(8));
    public int Width => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(0x0A));
    public int Height => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(0x0C));
    public int MipCount => Header[0x10];
    public int FtexsFileCount => Header[0x20];
    public IReadOnlyList<FtexMip> Mips { get; }

    private FtexTexture(byte[] header, List<FtexMip> mips)
    {
        Header = header;
        Mips = mips;
    }

    public static FtexTexture Read(ReadOnlySpan<byte> d)
    {
        try { return ReadChecked(d); } catch (Exception e) when (Damaged.Is(e)) { throw Damaged.Error("texture (.ftex)", e); }
    }

    private static FtexTexture ReadChecked(ReadOnlySpan<byte> d)
    {
        if (!d[..4].SequenceEqual("FTEX"u8)) throw new InvalidDataException("Not an .ftex file.");
        int mipCount = d[0x10];
        var mips = new List<FtexMip>(mipCount);
        for (int i = 0; i < mipCount; i++)
        {
            var e = d.Slice(0x40 + i * 16, 16);
            mips.Add(new FtexMip(
                Index: e[12],
                FileNumber: e[13],
                Offset: (int)BinaryPrimitives.ReadUInt32LittleEndian(e),
                UncompressedSize: (int)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]),
                StoredSize: (int)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                ChunkCount: BinaryPrimitives.ReadUInt16LittleEndian(e[14..])));
        }
        return new FtexTexture(d[..(0x40 + mipCount * 16)].ToArray(), mips);
    }

    /// <summary>The block data of one mip (still DXT-compressed), unpacked from its .ftexs file.</summary>
    public static byte[] ReadMipData(FtexMip mip, ReadOnlySpan<byte> ftexs)
    {
        if (mip.ChunkCount == 0) return ftexs.Slice(mip.Offset, mip.UncompressedSize).ToArray();
        var output = new byte[mip.UncompressedSize];
        int written = 0;
        for (int c = 0; c < mip.ChunkCount; c++)
        {
            var t = ftexs.Slice(mip.Offset + c * 8, 8);
            int stored = BinaryPrimitives.ReadUInt16LittleEndian(t);
            int raw = BinaryPrimitives.ReadUInt16LittleEndian(t[2..]);
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(t[4..]);
            bool uncompressed = (off & 0x80000000) != 0;
            var src = ftexs.Slice(mip.Offset + (int)(off & 0x7FFFFFFF), stored);
            if (uncompressed)
            {
                src.CopyTo(output.AsSpan(written));
                written += src.Length;
            }
            else
            {
                using var z = new ZLibStream(new MemoryStream(src.ToArray()), CompressionMode.Decompress);
                z.ReadExactly(output.AsSpan(written, raw));
                written += raw;
            }
        }
        return output;
    }

    /// <summary>
    /// Decodes the largest mip that the given .ftexs files contain (key = file number) to RGBA.
    /// </summary>
    public RgbaImage DecodeLargestAvailable(IReadOnlyDictionary<int, byte[]> ftexs, int smallestIndexWanted = 0)
    {
        foreach (var m in Mips.OrderBy(m => m.Index))
        {
            if (m.Index < smallestIndexWanted || !ftexs.TryGetValue(m.FileNumber, out var file)) continue;
            return Decode(m, file);
        }
        throw new InvalidDataException("None of this texture's mips is in the files given.");
    }

    public RgbaImage Decode(FtexMip mip, ReadOnlySpan<byte> ftexs)
    {
        try { return DecodeChecked(mip, ftexs); }
        catch (NotSupportedException e) { throw new InvalidDataException(e.Message, e); } // Not damaged: the message names the format.
        catch (Exception e) when (Damaged.Is(e)) { throw Damaged.Error("texture (.ftexs)", e); }
    }

    private RgbaImage DecodeChecked(FtexMip mip, ReadOnlySpan<byte> ftexs)
    {
        byte[] blocks = ReadMipData(mip, ftexs);
        int w = mip.Width(this), h = mip.Height(this);
        return PixelFormat switch
        {
            0 => RgbaImage.FromBgra(w, h, blocks),
            2 => Dxt.DecodeBc1(blocks, w, h),
            3 => Dxt.DecodeBc2(blocks, w, h),
            4 => Dxt.DecodeBc3(blocks, w, h),
            _ => throw new NotSupportedException($"ftex pixel format {PixelFormat} is not supported yet."),
        };
    }
}
