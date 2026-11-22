// ==================================================================================================
// Jatayu Engine - Core Platform
// Module: Settings.cs
// Role: User-facing runtime game and graphics settings serialized to JSON.
// ==================================================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using Jatayu.Core.Logging;

namespace Jatayu.Core.Config;

/// <summary>
/// User-configurable game and graphics settings.
/// </summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public int ResolutionWidth { get; set; } = 1920;
    public int ResolutionHeight { get; set; } = 1080;
    public bool VSync { get; set; } = false;
    public bool Fullscreen { get; set; } = false;
    public float FieldOfView { get; set; } = 60.0f;
    public float MaxDrawDistance { get; set; } = 8000.0f;
    public float MouseSensitivity { get; set; } = 1.0f;
    public float MasterVolume { get; set; } = 1.0f;
    public string ShadowQuality { get; set; } = "High";
    public string TextureQuality { get; set; } = "High";

    /// <summary>
    /// Loads settings from the specified JSON file, or returns default settings if the file does not exist.
    /// </summary>
    public static Settings Load(string filePath = "settings.json")
    {
        try
        {
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize<Settings>(json, s_jsonOptions);
                if (loaded != null)
                {
                    Log.Info($"Loaded user settings from '{filePath}'");
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to load settings from '{filePath}': {ex.Message}. Falling back to defaults.");
        }

        var defaults = new Settings();
        defaults.Save(filePath);
        return defaults;
    }

    /// <summary>
    /// Serializes current settings to disk.
    /// </summary>
    public void Save(string filePath = "settings.json")
    {
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(this, s_jsonOptions);
            File.WriteAllText(filePath, json);
            Log.Info($"Saved settings to '{filePath}'");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save settings to '{filePath}': {ex.Message}");
        }
    }
}
