namespace Jatayu.Renderer.Shadows;

public readonly record struct ShadowQualityPreset(int Resolution, bool HighQualityFiltering, float FilterRadius)
{
    public static ShadowQualityPreset Standard => new(1024, false, 1.0f);
    public static ShadowQualityPreset High => new(2048, true, 1.6f);
    public static ShadowQualityPreset Ultra => new(2048, true, 2.4f);

    public static bool TryParse(string? value, out ShadowQualityPreset preset)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "LOW":
            case "STANDARD":
                preset = Standard;
                return true;
            case "HIGH":
                preset = High;
                return true;
            case "ULTRA":
                preset = Ultra;
                return true;
            default:
                preset = default;
                return false;
        }
    }
}
