namespace Svfm.Core.Formats;

/// <summary>
/// BC1 (DXT1) and BC3 (DXT5) block encoder. Colour: principal-axis fit over the block, endpoints
/// at the extremes, one least-squares refinement, best of the refined and unrefined endpoints by
/// error. Alpha (BC3): 8-value ramp between the block's minimum and maximum.
/// </summary>
public static class DxtEncoder
{
    public static byte[] EncodeBc1(RgbaImage img) => Encode(img, bc3: false);
    public static byte[] EncodeBc3(RgbaImage img) => Encode(img, bc3: true);

    private static byte[] Encode(RgbaImage img, bool bc3)
    {
        int bw = Math.Max(1, (img.Width + 3) / 4), bh = Math.Max(1, (img.Height + 3) / 4);
        int size = bc3 ? 16 : 8;
        var o = new byte[bw * bh * size];
        Span<byte> block = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            for (int i = 0; i < 16; i++)
            {
                int x = Math.Min(bx * 4 + i % 4, img.Width - 1), y = Math.Min(by * 4 + i / 4, img.Height - 1);
                img.Pixel(x, y).CopyTo(block.Slice(i * 4, 4));
            }
            var dst = o.AsSpan((by * bw + bx) * size, size);
            if (bc3)
            {
                EncodeAlpha(block, dst[..8]);
                EncodeColor(block, dst[8..], allowThreeColor: false);
            }
            else EncodeColor(block, dst, allowThreeColor: false);
        }
        return o;
    }

    private static void EncodeAlpha(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        int min = 255, max = 0;
        for (int i = 0; i < 16; i++) { int a = px[i * 4 + 3]; min = Math.Min(min, a); max = Math.Max(max, a); }
        dst[0] = (byte)max;
        dst[1] = (byte)min;
        Span<int> ramp = stackalloc int[8];
        ramp[0] = max; ramp[1] = min;
        for (int i = 1; i < 7; i++) ramp[i + 1] = ((7 - i) * max + i * min) / 7;
        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = px[i * 4 + 3], best = 0, bestErr = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int e = Math.Abs(ramp[k] - a);
                if (e < bestErr) { bestErr = e; best = k; }
            }
            if (max == min) best = 0;
            bits |= (ulong)best << (3 * i);
        }
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (8 * i));
    }

    private static void EncodeColor(ReadOnlySpan<byte> px, Span<byte> dst, bool allowThreeColor)
    {
        _ = allowThreeColor;
        Span<float> c = stackalloc float[48];
        float mr = 0, mg = 0, mb = 0;
        for (int i = 0; i < 16; i++)
        {
            c[i * 3] = px[i * 4]; c[i * 3 + 1] = px[i * 4 + 1]; c[i * 3 + 2] = px[i * 4 + 2];
            mr += c[i * 3]; mg += c[i * 3 + 1]; mb += c[i * 3 + 2];
        }
        mr /= 16; mg /= 16; mb /= 16;

        // Covariance and principal axis (power iteration).
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        for (int i = 0; i < 16; i++)
        {
            float r = c[i * 3] - mr, g = c[i * 3 + 1] - mg, b = c[i * 3 + 2] - mb;
            xx += r * r; xy += r * g; xz += r * b; yy += g * g; yz += g * b; zz += b * b;
        }
        float ax = 1, ay = 1, az = 1;
        for (int it = 0; it < 8; it++)
        {
            float nx = xx * ax + xy * ay + xz * az, ny = xy * ax + yy * ay + yz * az, nz = xz * ax + yz * ay + zz * az;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-6f) { ax = 0.577f; ay = 0.577f; az = 0.577f; break; }
            ax = nx / len; ay = ny / len; az = nz / len;
        }
        float tmin = float.MaxValue, tmax = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float t = (c[i * 3] - mr) * ax + (c[i * 3 + 1] - mg) * ay + (c[i * 3 + 2] - mb) * az;
            tmin = MathF.Min(tmin, t); tmax = MathF.Max(tmax, t);
        }
        Span<float> e0 = [mr + ax * tmax, mg + ay * tmax, mb + az * tmax];
        Span<float> e1 = [mr + ax * tmin, mg + ay * tmin, mb + az * tmin];

        ushort a0 = To565(e0), a1 = To565(e1);
        Span<byte> idxA = stackalloc byte[16];
        long errA = Fit(c, a0, a1, idxA);

        // Least-squares refinement of the endpoints from the chosen indices.
        Refine(c, idxA, out var r0, out var r1);
        ushort b0 = To565(r0), b1 = To565(r1);
        Span<byte> idxB = stackalloc byte[16];
        long errB = Fit(c, b0, b1, idxB);

        if (errB < errA) { a0 = b0; a1 = b1; idxB.CopyTo(idxA); }
        Write(dst, a0, a1, idxA);
    }

    private static void Write(Span<byte> dst, ushort c0, ushort c1, Span<byte> idx)
    {
        // Four-colour mode needs c0 > c1; swapping the endpoints swaps the index meaning.
        if (c0 < c1)
        {
            (c0, c1) = (c1, c0);
            for (int i = 0; i < 16; i++) idx[i] = idx[i] switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 };
        }
        else if (c0 == c1)
        {
            idx.Clear();
        }
        dst[0] = (byte)c0; dst[1] = (byte)(c0 >> 8);
        dst[2] = (byte)c1; dst[3] = (byte)(c1 >> 8);
        uint bits = 0;
        for (int i = 0; i < 16; i++) bits |= (uint)idx[i] << (2 * i);
        dst[4] = (byte)bits; dst[5] = (byte)(bits >> 8); dst[6] = (byte)(bits >> 16); dst[7] = (byte)(bits >> 24);
    }

    private static long Fit(ReadOnlySpan<float> c, ushort c0, ushort c1, Span<byte> idx)
    {
        Span<int> pal = stackalloc int[12];
        Expand(c0, pal[..3]);
        Expand(c1, pal.Slice(3, 3));
        for (int k = 0; k < 3; k++)
        {
            pal[6 + k] = (2 * pal[k] + pal[3 + k]) / 3;
            pal[9 + k] = (pal[k] + 2 * pal[3 + k]) / 3;
        }
        long total = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0; long bestErr = long.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                float dr = c[i * 3] - pal[k * 3], dg = c[i * 3 + 1] - pal[k * 3 + 1], db = c[i * 3 + 2] - pal[k * 3 + 2];
                long e = (long)(dr * dr * 3 + dg * dg * 4 + db * db * 2);
                if (e < bestErr) { bestErr = e; best = k; }
            }
            idx[i] = (byte)best;
            total += bestErr;
        }
        return total;
    }

    private static void Refine(ReadOnlySpan<float> c, ReadOnlySpan<byte> idx, out float[] e0, out float[] e1)
    {
        // Solve for endpoints a, b minimising sum |w_a a + w_b b - x|^2 with weights per index.
        float aa = 0, ab = 0, bb = 0;
        Span<float> ax = stackalloc float[3];
        Span<float> bx = stackalloc float[3];
        for (int i = 0; i < 16; i++)
        {
            float wa = idx[i] switch { 0 => 1f, 1 => 0f, 2 => 2f / 3, _ => 1f / 3 };
            float wb = 1 - wa;
            aa += wa * wa; ab += wa * wb; bb += wb * wb;
            for (int k = 0; k < 3; k++) { ax[k] += wa * c[i * 3 + k]; bx[k] += wb * c[i * 3 + k]; }
        }
        float det = aa * bb - ab * ab;
        e0 = new float[3]; e1 = new float[3];
        if (MathF.Abs(det) < 1e-6f)
        {
            for (int k = 0; k < 3; k++) e0[k] = e1[k] = (ax[k] + bx[k]) / 16;
            return;
        }
        for (int k = 0; k < 3; k++)
        {
            e0[k] = (ax[k] * bb - bx[k] * ab) / det;
            e1[k] = (bx[k] * aa - ax[k] * ab) / det;
        }
    }

    private static ushort To565(ReadOnlySpan<float> rgb)
    {
        int r = Math.Clamp((int)MathF.Round(rgb[0] * 31 / 255), 0, 31);
        int g = Math.Clamp((int)MathF.Round(rgb[1] * 63 / 255), 0, 63);
        int b = Math.Clamp((int)MathF.Round(rgb[2] * 31 / 255), 0, 31);
        return (ushort)(r << 11 | g << 5 | b);
    }

    private static void Expand(ushort c, Span<int> rgb)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        rgb[0] = (r << 3) | (r >> 2); rgb[1] = (g << 2) | (g >> 4); rgb[2] = (b << 3) | (b >> 2);
    }
}
