using System.Text.Json;

namespace Blueline.Core;

/// <summary>Per-user preferences, stored under %APPDATA%\Blueline.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string? GameFolder { get; set; }
    public string? SxPath { get; set; }
    public string? EmulatorPath { get; set; }

    /// <summary>The user's ffmpeg.exe, when it is not on the PATH. Used for video.</summary>
    public string? FfmpegPath { get; set; }
    public List<string> RecentProjects { get; set; } = [];

    /// <summary>"dark" or "light". Dark is the default.</summary>
    public string Theme { get; set; } = UiPalette.DarkName;

    /// <summary>An image the user chose for behind the start screen. Null means the built-in rink artwork.</summary>
    public string? StartBackground { get; set; }

    /// <summary>Turns off page and start screen movement. Windows' own "animation effects" switch is also respected.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Blank jersey maps the user made, by brand and map, such as "adidas_nm". Used as a base in Jersey Maps.</summary>
    public Dictionary<string, string> JerseyBlanks { get; set; } = [];

    /// <summary>The full-ice rink model (a modded rink_ice_nhl.mcd) the user was given. Not shipped with Blueline.</summary>
    public string? FullIceModelPath { get; set; }

    /// <summary>Where TheFaceoff.net rink pictures are cached. Null means %LOCALAPPDATA%\Blueline\faceoff.</summary>
    public string? FaceoffCache { get; set; }

    public static string DefaultFaceoffCache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blueline", "faceoff");

    private static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blueline", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(File_) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(File_), Json) ?? new AppSettings() : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, JsonSerializer.Serialize(this, Json));
    }

    public void NoteRecentProject(string folder)
    {
        RecentProjects.RemoveAll(p => p.Equals(folder, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, folder);
        if (RecentProjects.Count > 8)
            RecentProjects.RemoveRange(8, RecentProjects.Count - 8);
        Save();
    }
}
