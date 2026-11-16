using Svfm.Core.Formats;

namespace Svfm.Core.Conversion;

/// <summary>
/// Suit-list icons (EquipIcon/suit/ui_st_*_alp, 256×128). Rules measured on the vanilla icons:
/// <list type="bullet">
/// <item>All suit icons share one alpha mask (a soft-edged window); the colour is only inside the
/// rectangle x 60–195, y 30–97 (square corners) and black under the fade outside it.</item>
/// <item>Fatigues (camo) icons are a 1:1 crop of the full-size 2048 camo texture, no scaling or
/// rotation, with contrast raised about grey 86: out = 86.4 + 1.375 × (in − 86.4). Fitted on
/// seven vanilla icons; halves the error against the in-game icon on six of eight.</item>
/// </list>
/// </summary>
public static class SuitIcon
{
    public const int Width = 256, Height = 128;
    public const int WindowX = 60, WindowY = 30, WindowW = 136, WindowH = 68;

    /// <summary>Texture pixel under icon pixel (0,0) for the vanilla fatigues icons, by fova camo ID.</summary>
    public static readonly IReadOnlyDictionary<int, (int x, int y)> VanillaCropOrigin = new Dictionary<int, (int, int)>
    {
        [0] = (1805, 1516), [2] = (1805, 1516), [3] = (1805, 1516), [5] = (1805, 1516),
        [6] = (1548, 1414), [10] = (1073, 1271), [12] = (1265, 1345), [14] = (1053, 1282),
    };

    /// <summary>A plain-fabric spot on the shared fatigues UV layout, for camos without a vanilla icon.</summary>
    public static readonly (int x, int y) DefaultCropOrigin = (1265, 1345);

    public const float DefaultContrast = 1.375f, DefaultPivot = 86.4f, DefaultBrightness = 1.0f;

    /// <summary>
    /// The suit-icon frame's alpha, drawn from the measured rule (no vanilla file needed): opaque inside
    /// x 64–191, y 34–93, then a soft fade by distance from that rectangle. The curve below was fitted on
    /// the vanilla mask (identical on every suit icon checked); mean difference 1.7/255, the largest
    /// differences being DXT5 block artefacts in the game's copy.
    /// </summary>
    public static byte[] DrawnMask()
    {
        ReadOnlySpan<float> curve =
        [
            255.0f, 251.0f, 243.3f, 227.8f, 201.2f, 164.5f, 135.7f, 111.0f, 91.4f, 79.5f, 69.1f, 62.4f, 54.8f, 48.8f,
            43.4f, 38.0f, 33.9f, 29.3f, 26.1f, 23.3f, 20.3f, 18.3f, 16.1f, 14.6f, 11.6f, 9.1f, 6.0f, 4.2f, 4.1f, 2.5f,
            2.0f, 1.5f, 0f,
        ];
        var m = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float dx = Math.Max(Math.Max(64 - x, x - 191), 0), dy = Math.Max(Math.Max(34 - y, y - 93), 0);
            float d = MathF.Sqrt(dx * dx + dy * dy);
            int i = (int)d;
            float a = i + 1 < curve.Length ? curve[i] + (curve[i + 1] - curve[i]) * (d - i) : 0;
            m[y * Width + x] = (byte)Math.Clamp((int)MathF.Round(a), 0, 255);
        }
        return m;
    }

    /// <summary>The alpha channel of a vanilla suit icon, used as the mask.</summary>
    public static byte[] MaskFrom(RgbaImage vanillaIcon)
    {
        if (vanillaIcon.Width != Width || vanillaIcon.Height != Height) throw new ArgumentException("not a 256×128 icon");
        var m = new byte[Width * Height];
        for (int i = 0; i < m.Length; i++) m[i] = vanillaIcon.Pixels[i * 4 + 3];
        return m;
    }

    /// <summary>Puts a 256×128 picture into the vanilla suit-icon frame.</summary>
    public static RgbaImage Frame(RgbaImage picture, byte[] mask)
    {
        if (picture.Width != Width || picture.Height != Height) picture = Resize.Box(picture, Width, Height);
        var o = new RgbaImage(Width, Height);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            var d = o.Pixel(x, y);
            bool inside = x >= WindowX && x < WindowX + WindowW && y >= WindowY && y < WindowY + WindowH;
            if (inside) picture.Pixel(x, y)[..3].CopyTo(d);
            d[3] = mask[y * Width + x];
        }
        return o;
    }

    /// <summary>
    /// An icon from a picture the user gives. A finished 256×128 icon (its own transparent frame) is used as it is;
    /// any other picture is scaled to cover the icon's picture window (2:1, centred crop) and put in the drawn frame.
    /// </summary>
    public static RgbaImage FromPicture(RgbaImage picture, byte[]? mask = null)
    {
        if (picture.Width == Width && picture.Height == Height && HasOwnFrame(picture)) return picture;
        float scale = MathF.Max((float)WindowW / picture.Width, (float)WindowH / picture.Height);
        int sw = Math.Max(WindowW, (int)MathF.Ceiling(picture.Width * scale)), sh = Math.Max(WindowH, (int)MathF.Ceiling(picture.Height * scale));
        var scaled = Resize.Bilinear(picture, sw, sh);
        var canvas = new RgbaImage(Width, Height);
        int ox = (sw - WindowW) / 2, oy = (sh - WindowH) / 2;
        for (int y = 0; y < WindowH; y++)
        for (int x = 0; x < WindowW; x++)
            scaled.Pixel(ox + x, oy + y).CopyTo(canvas.Pixel(WindowX + x, WindowY + y));
        return Frame(canvas, mask ?? DrawnMask());
    }

    /// <summary>True when the picture's corners are transparent and its centre opaque (an icon with its frame).</summary>
    private static bool HasOwnFrame(RgbaImage p) =>
        p.Pixel(0, 0)[3] < 32 && p.Pixel(Width - 1, Height - 1)[3] < 32 && p.Pixel(Width / 2, Height / 2)[3] > 224;

    /// <summary>Fatigues icon from a camo texture (any size; scaled to 2048 first).</summary>
    public static RgbaImage FromCamoTexture(RgbaImage texture, byte[] mask, (int x, int y)? origin = null,
        float contrast = DefaultContrast, float brightness = DefaultBrightness, float pivot = DefaultPivot)
    {
        if (texture.Width != 2048 || texture.Height != 2048) texture = Resize.Bilinear(texture, 2048, 2048);
        var (ox, oy) = origin ?? DefaultCropOrigin;
        ox = Math.Clamp(ox, 0, 2048 - Width);
        oy = Math.Clamp(oy, 0, 2048 - Height);
        var crop = texture.Crop(ox, oy, Width, Height);
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = (byte)Math.Clamp((int)MathF.Round((pivot + contrast * (v - pivot)) * brightness), 0, 255);
        for (int i = 0; i < crop.Pixels.Length; i++)
            if (i % 4 != 3) crop.Pixels[i] = lut[crop.Pixels[i]];
        return Frame(crop, mask);
    }

    /// <summary>The icon as game files (.ftex + .1/.2.ftexs), DXT5, texture type 1 like vanilla UI icons.</summary>
    public static FtexFiles ToGameFiles(RgbaImage icon) => FtexWriter.Write(icon, withAlpha: true, FtexKind.Linear);

    /// <summary>
    /// Recolours an existing icon for a recolour mod: the large areas whose hue lies in the kept
    /// range stay as they are; everything else becomes grey times <paramref name="darken"/>.
    /// Example: True Black Ocelot keeps the red scarf (hue 0–22°, saturation ≥ 0.55) and blackens the
    /// gear (darken 0.35); measured on the vanilla icon, the khaki straps sit at hue 22–36°, saturation 0.3–0.5.
    /// <list type="bullet">
    /// <item>Only connected kept areas of at least <paramref name="minArea"/> pixels survive, so
    /// stray brown-ish pixels on the gear are greyed too (the first test build had brown specks).</item>
    /// <item>Small holes inside a kept area (dark folds) are filled, and the edge is softened over a
    /// pixel so the boundary doesn't look cut out.</item>
    /// </list>
    /// Mapping colours automatically from the vanilla to the mod texture was tried and does not work:
    /// the icon is a lit render, so its colours don't match texture colours.
    /// </summary>
    public static RgbaImage RecolourKeepHue(RgbaImage icon, float hueFrom, float hueTo, float minSaturation = 0.55f,
        float darken = 0.35f, int minArea = 150)
    {
        int w = icon.Width, h = icon.Height;
        var keep = new bool[w * h];
        for (int p = 0; p < w * h; p++)
        {
            int i = p * 4;
            float r = icon.Pixels[i] / 255f, g = icon.Pixels[i + 1] / 255f, b = icon.Pixels[i + 2] / 255f;
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
            float sat = max <= 0 ? 0 : (max - min) / max;
            float hue = Hue(r, g, b, max, min);
            bool inHue = hueFrom <= hueTo ? hue >= hueFrom && hue <= hueTo : hue >= hueFrom || hue <= hueTo;
            // Bright highlights on the kept cloth are less saturated; dark khaki straps are not bright.
            keep[p] = inHue && max >= 0.15f && (sat >= minSaturation || (max >= 0.6f && sat >= minSaturation - 0.15f));
        }

        keep = DropSmallRegions(keep, w, h, minArea);
        keep = Erode(Dilate(keep, w, h, 2), w, h, 2);    // closing: fill dark folds inside the scarf
        keep = DropSmallRegions(keep, w, h, minArea);

        // Soft edge: average of the 3×3 neighbourhood.
        var weight = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int n = 0, k = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                n++;
                if (keep[yy * w + xx]) k++;
            }
            weight[y * w + x] = (float)k / n;
        }

        var o = new RgbaImage(w, h, (byte[])icon.Pixels.Clone());
        for (int p = 0; p < w * h; p++)
        {
            int i = p * 4;
            float r = icon.Pixels[i], g = icon.Pixels[i + 1], b = icon.Pixels[i + 2];
            float grey = (0.299f * r + 0.587f * g + 0.114f * b) * darken;
            float a = weight[p];
            o.Pixels[i] = (byte)Math.Clamp((int)MathF.Round(r * a + grey * (1 - a)), 0, 255);
            o.Pixels[i + 1] = (byte)Math.Clamp((int)MathF.Round(g * a + grey * (1 - a)), 0, 255);
            o.Pixels[i + 2] = (byte)Math.Clamp((int)MathF.Round(b * a + grey * (1 - a)), 0, 255);
        }
        return o;
    }

    private static bool[] DropSmallRegions(bool[] m, int w, int h, int minArea)
    {
        var o = new bool[m.Length];
        var seen = new bool[m.Length];
        var stack = new Stack<int>();
        var region = new List<int>();
        for (int s = 0; s < m.Length; s++)
        {
            if (!m[s] || seen[s]) continue;
            region.Clear();
            stack.Push(s);
            seen[s] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                region.Add(p);
                int x = p % w, y = p / w;
                foreach (int q in new[] { x > 0 ? p - 1 : -1, x < w - 1 ? p + 1 : -1, y > 0 ? p - w : -1, y < h - 1 ? p + w : -1 })
                    if (q >= 0 && m[q] && !seen[q]) { seen[q] = true; stack.Push(q); }
            }
            if (region.Count >= minArea) foreach (int p in region) o[p] = true;
        }
        return o;
    }

    private static bool[] Dilate(bool[] m, int w, int h, int r) => Morph(m, w, h, r, any: true);
    private static bool[] Erode(bool[] m, int w, int h, int r) => Morph(m, w, h, r, any: false);

    private static bool[] Morph(bool[] m, int w, int h, int r, bool any)
    {
        var o = new bool[m.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool v = !any;
            for (int dy = -r; dy <= r && v != any; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1);
                if (m[yy * w + xx] == any) { v = any; break; }
            }
            o[y * w + x] = v;
        }
        return o;
    }

    private static float Hue(float r, float g, float b, float max, float min)
    {
        float d = max - min;
        if (d <= 0) return 0;
        float h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }
}
