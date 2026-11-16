using System.Buffers.Binary;

namespace Svfm.Core.Hashing;

/// <summary>
/// CityHash64 version 1.0.x, the variant the Fox Engine (and GzsTool / SnakeBite) use.
/// Later CityHash releases changed the short-input paths, so this must not be swapped
/// for a newer library.
/// </summary>
public static class CityHash
{
    private const ulong K0 = 0xc3a5c85c97cb3127UL;
    private const ulong K1 = 0xb492b66fbe98f273UL;
    private const ulong K2 = 0x9ae16a3b2f90404fUL;
    private const ulong K3 = 0xc949d7c7509e6557UL;

    private static ulong Fetch64(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt64LittleEndian(s[i..]);
    private static ulong Fetch32(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadUInt32LittleEndian(s[i..]);
    private static ulong Rotate(ulong v, int n) => n == 0 ? v : (v >> n) | (v << (64 - n));
    private static ulong ShiftMix(ulong v) => v ^ (v >> 47);

    public static ulong Hash128To64(ulong u, ulong v)
    {
        const ulong kMul = 0x9ddfea08eb382d69UL;
        ulong a = (u ^ v) * kMul;
        a ^= a >> 47;
        ulong b = (v ^ a) * kMul;
        b ^= b >> 47;
        return b * kMul;
    }

    private static ulong HashLen0To16(ReadOnlySpan<byte> s)
    {
        int n = s.Length;
        if (n > 8)
        {
            ulong a = Fetch64(s, 0);
            ulong b = Fetch64(s, n - 8);
            return Hash128To64(a, Rotate(b + (ulong)n, n)) ^ b;
        }
        if (n >= 4)
        {
            ulong a = Fetch32(s, 0);
            return Hash128To64((ulong)n + (a << 3), Fetch32(s, n - 4));
        }
        if (n > 0)
        {
            uint a = s[0], b = s[n >> 1], c = s[n - 1];
            uint y = a + (b << 8);
            uint z = (uint)n + (c << 2);
            return ShiftMix(y * K2 ^ z * K3) * K2;
        }
        return K2;
    }

    private static ulong HashLen17To32(ReadOnlySpan<byte> s)
    {
        int n = s.Length;
        ulong a = Fetch64(s, 0) * K1;
        ulong b = Fetch64(s, 8);
        ulong c = Fetch64(s, n - 8) * K2;
        ulong d = Fetch64(s, n - 16) * K0;
        return Hash128To64(Rotate(a - b, 43) + Rotate(c, 30) + d, a + Rotate(b ^ K3, 20) - c + (ulong)n);
    }

    private static (ulong, ulong) WeakHashLen32WithSeeds(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = Rotate(b + a + z, 21);
        ulong c = a;
        a += x;
        a += y;
        b += Rotate(a, 44);
        return (a + z, b + c);
    }

    private static (ulong, ulong) WeakHashLen32WithSeeds(ReadOnlySpan<byte> s, int i, ulong a, ulong b) =>
        WeakHashLen32WithSeeds(Fetch64(s, i), Fetch64(s, i + 8), Fetch64(s, i + 16), Fetch64(s, i + 24), a, b);

    private static ulong HashLen33To64(ReadOnlySpan<byte> s)
    {
        int n = s.Length;
        ulong z = Fetch64(s, 24);
        ulong a = Fetch64(s, 0) + ((ulong)n + Fetch64(s, n - 16)) * K0;
        ulong b = Rotate(a + z, 52);
        ulong c = Rotate(a, 37);
        a += Fetch64(s, 8);
        c += Rotate(a, 7);
        a += Fetch64(s, 16);
        ulong vf = a + z;
        ulong vs = b + Rotate(a, 31) + c;
        a = Fetch64(s, 16) + Fetch64(s, n - 32);
        z = Fetch64(s, n - 8);
        b = Rotate(a + z, 52);
        c = Rotate(a, 37);
        a += Fetch64(s, n - 24);
        c += Rotate(a, 7);
        a += Fetch64(s, n - 16);
        ulong wf = a + z;
        ulong ws = b + Rotate(a, 31) + c;
        ulong r = ShiftMix((vf + ws) * K2 + (wf + vs) * K0);
        return ShiftMix(r * K0 + vs) * K2;
    }

    public static ulong Hash64(ReadOnlySpan<byte> s)
    {
        int n = s.Length;
        if (n <= 32)
            return n <= 16 ? HashLen0To16(s) : HashLen17To32(s);
        if (n <= 64)
            return HashLen33To64(s);

        ulong x = Fetch64(s, 0);
        ulong y = Fetch64(s, n - 16) ^ K1;
        ulong z = Fetch64(s, n - 56) ^ K0;
        var v = WeakHashLen32WithSeeds(s, n - 64, (ulong)n, y);
        var w = WeakHashLen32WithSeeds(s, n - 32, (ulong)n * K1, K0);
        z += ShiftMix(v.Item2) * K1;
        x = Rotate(z + x, 39) * K1;
        y = Rotate(y, 33) * K1;

        int len = (n - 1) & ~63;
        int i = 0;
        do
        {
            x = Rotate(x + y + v.Item1 + Fetch64(s, i + 16), 37) * K1;
            y = Rotate(y + v.Item2 + Fetch64(s, i + 48), 42) * K1;
            x ^= w.Item2;
            y ^= v.Item1;
            z = Rotate(z ^ w.Item1, 33);
            v = WeakHashLen32WithSeeds(s, i, v.Item2 * K1, x + w.Item1);
            w = WeakHashLen32WithSeeds(s, i + 32, z + w.Item2, y);
            (z, x) = (x, z);
            i += 64;
            len -= 64;
        } while (len != 0);

        return Hash128To64(Hash128To64(v.Item1, w.Item1) + ShiftMix(y) * K1 + z, Hash128To64(v.Item2, w.Item2) + x);
    }

    public static ulong Hash64WithSeeds(ReadOnlySpan<byte> s, ulong seed0, ulong seed1) =>
        Hash128To64(Hash64(s) - seed0, seed1);
}
