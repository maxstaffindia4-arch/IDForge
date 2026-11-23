using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDForge.Core;

/// <summary>
/// Operator-tunable photo-placement ratios, persisted in
/// %LOCALAPPDATA%\IDForge\config.json so they can be changed without a rebuild.
/// The file is created with defaults on first run; // comments are allowed.
/// </summary>
public static class PhotoTuning
{
    private static readonly Settings _s = Load();

    public static float FaceHeightRatio => _s.FaceHeightRatio;
    public static float HeadTopRatio => _s.HeadTopRatio;
    public static float HairAboveFaceRatio => _s.HairAboveFaceRatio;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    private sealed class Settings
    {
        // PROVISIONAL — owner to calibrate on real photos before release.
        [JsonPropertyName("faceHeightRatio")] public float FaceHeightRatio { get; set; } = 0.48f;
        [JsonPropertyName("headTopRatio")] public float HeadTopRatio { get; set; } = 0.08f;
        [JsonPropertyName("hairAboveFaceRatio")] public float HairAboveFaceRatio { get; set; } = 0.55f;
    }

    private const string DefaultJson = """
        {
          // PROVISIONAL values — calibrate on real photos; edit and restart the app.
          "faceHeightRatio": 0.48,   // face height as fraction of photo-window height (was 0.58)
          "headTopRatio": 0.08,      // head top as fraction of window height
          "hairAboveFaceRatio": 0.55 // hair above detected face box, fraction of face height
        }
        """;

    private static string ConfigPath => Path.Combine(AppPaths.DataDir, "config.json");

    private static Settings Load()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            if (!File.Exists(ConfigPath))
                File.WriteAllText(ConfigPath, DefaultJson);
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath), JsonOpts);
            return s ?? new Settings();
        }
        catch { return new Settings(); }
    }
}
