using System.Buffers.Binary;

namespace Svfm.Core.Archives;

/// <summary>
/// The two QAR (.dat) obfuscation layers. Ported from GzsTool 0.5.3 (MIT, Atvaark):
/// Decrypt1Stream, Decrypt2Stream, QarFile.DecryptSectionList. Both layers are XOR,
/// so the same functions encrypt and decrypt. Each runs over a whole entry at once
/// (GzsTool's stream versions give the same result only for 8-byte-aligned reads).
/// </summary>
internal static class QarCrypto
{
    internal static readonly uint[] HeaderXor = [0x41441043, 0x11C22050, 0xD05608C3, 0x532C7319];

    private static readonly uint[] Layer1Table =
    [
        0xBB8ADEDB, 0x65229958, 0x08453206, 0x88121302,
        0x4C344955, 0x2C02F10C, 0x4887F823, 0xF3818583,
    ];

    public const uint Layer2Magic8 = 0xA0F8EFE6;
    public const uint Layer2Magic16 = 0xE3F8EFE6;

    public static int Layer2HeaderSize(uint magic) => magic switch
    {
        Layer2Magic8 => 8,
        Layer2Magic16 => 16,
        _ => 0,
    };

    /// <summary>Decrypts the entry table that follows the 32-byte archive header.</summary>
    public static ulong[] DecryptSectionList(ReadOnlySpan<byte> data, int count, uint version)
    {
        var result = new ulong[count];
        if (version != 2)
        {
            for (int i = 0; i < count; i++)
            {
                int o1 = i * 8, o2 = o1 + 4;
                uint a = BinaryPrimitives.ReadUInt32LittleEndian(data[o1..]) ^ HeaderXor[(i + o1 / 5) % 4];
                uint b = BinaryPrimitives.ReadUInt32LittleEndian(data[o2..]) ^ HeaderXor[(i + o2 / 5) % 4];
                result[i] = (ulong)b << 32 | a;
            }
        }
        else
        {
            uint xor = 0xA2C18EC3;
            for (int i = 0; i < count; i++)
            {
                int o1 = i * 8, o2 = o1 + 4;
                uint s1 = BinaryPrimitives.ReadUInt32LittleEndian(data[o1..]);
                uint s2 = BinaryPrimitives.ReadUInt32LittleEndian(data[o2..]);
                uint a = s1 ^ HeaderXor[(xor + (uint)(o1 / 5)) % 4];
                uint b = s2 ^ HeaderXor[(xor + (uint)(o2 / 5)) % 4];
                result[i] = (ulong)b << 32 | a;
                int rotation = (int)(b / 256) % 19;
                uint rotated = (a >> rotation) | (a << (32 - rotation));
                xor ^= rotated;
            }
        }
        return result;
    }

    /// <summary>Layer 1, keyed by the entry's MD5 and the low 32 bits of its hash. In place.</summary>
    public static void Layer1(Span<byte> data, uint version, ReadOnlySpan<byte> md5, uint hashLow)
    {
        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(md5[(int)(hashLow % 2 * 8)..]);
        uint seedLow = (uint)seed, seedHigh = (uint)(seed >> 32);
        int blocks = data.Length / 8;

        for (int i = 0; i < blocks; i++)
        {
            int o = i * 8;
            int index = version != 2
                ? (int)(2 * ((hashLow + (uint)(o / 11)) % 4))
                : 2 * (int)((hashLow + seed + (ulong)(o / 11)) % 4);
            uint x1 = Layer1Table[index], x2 = Layer1Table[index + 1];
            if (version == 2) { x1 ^= seedLow; x2 ^= seedHigh; }
            BinaryPrimitives.WriteUInt32LittleEndian(data[o..], BinaryPrimitives.ReadUInt32LittleEndian(data[o..]) ^ x1);
            BinaryPrimitives.WriteUInt32LittleEndian(data[(o + 4)..], BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 4)..]) ^ x2);
        }

        int tail = blocks * 8;
        for (int off = tail; off < data.Length; off++)
        {
            int blockStart = off - off % 8;
            int index = version != 2
                ? (int)(2 * ((hashLow + (uint)(blockStart / 11)) % 4))
                : 2 * (int)((hashLow + seed + (ulong)(blockStart / 11)) % 4);
            int k = off % 8;
            uint mask = k < 4 ? Layer1Table[index] : Layer1Table[index + 1];
            byte m = (byte)(mask >> (8 * (k % 4)));
            if (version == 2) m ^= (byte)((k < 4 ? seedLow : seedHigh) >> (8 * (k % 4)));
            data[off] ^= m;
        }
    }

    /// <summary>Layer 2 (present when the data starts with a magic). The last 0-3 bytes stay as they are. In place.</summary>
    public static void Layer2(Span<byte> data, uint key)
    {
        uint mul = 278 * key;
        uint blockKey = key | ((key ^ 25974) << 16);
        int words = data.Length / 4;
        for (int i = 0; i < words; i++)
        {
            var w = data.Slice(i * 4, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(w, BinaryPrimitives.ReadUInt32LittleEndian(w) ^ blockKey);
            blockKey = mul + 48828125 * blockKey;
        }
    }
}
