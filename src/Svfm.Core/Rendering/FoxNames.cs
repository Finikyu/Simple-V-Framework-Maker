using Svfm.Core.Hashing;

namespace Svfm.Core.Rendering;

/// <summary>
/// Names that vanilla models store only as StrCode64 hashes: material texture roles and the
/// player skeleton's bones. Used to find the base-colour slot and the framing bones.
/// </summary>
public static class FoxNames
{
    public static readonly string[] TextureRoles =
    [
        "Base_Tex_SRGB", "Base_Tex_LIN", "NormalMap_Tex_NRM", "SpecularMap_Tex_LIN", "Translucent_Tex_LIN",
        "Layer_Tex_SRGB", "LayerMask_Tex_LIN", "Dirty_Tex_LIN", "Base_Tex2_SRGB", "NormalMap_Tex2_NRM",
        "MetalnessMap_Tex_LIN", "RoughnessMap_Tex_LIN", "SubNormalMap_Tex_NRM", "CamouflageTexture_Tex_SRGB",
        "MatParamMap_Tex_LIN", "SpecularMap_Tex2_LIN", "Incidence_Tex_LIN", "Mask_Tex_LIN",
    ];

    private static readonly string[] BoneParts =
    [
        "WAIST", "HIP", "SPINE", "SPINE1", "SPINE2", "CHEST", "NECK", "HEAD", "PELVIS", "BELLY",
        "LTHIGH", "RTHIGH", "LKNEE", "RKNEE", "LFOOT", "RFOOT", "LTOE", "RTOE",
        "LSHOULDER", "RSHOULDER", "LCLAVICLE", "RCLAVICLE", "LUPPERARM", "RUPPERARM", "LFOREARM", "RFOREARM",
        "LHAND", "RHAND", "LELBOW", "RELBOW", "LWRIST", "RWRIST",
    ];

    private static readonly Lazy<Dictionary<ulong, string>> Known = new(() =>
    {
        var d = new Dictionary<ulong, string>();
        void Add(string s) => d.TryAdd(FoxHash.StrCode64(s), s);
        foreach (var r in TextureRoles) Add(r);
        for (int i = 0; i < 200; i++)
            foreach (var p in BoneParts) Add($"SKL_{i:000}_{p}");
        return d;
    });

    public static string? Lookup(ulong strCode64) => Known.Value.GetValueOrDefault(strCode64);
}
