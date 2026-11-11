using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Blueline.Core.Emulator;

/// <summary>The settings file a shadPS4 build reads: older forks use config.toml, newer builds config.json.</summary>
public enum ShadConfigFormat { Toml, Json }

/// <summary>Where one shadPS4 build keeps its files, and how to change and read them.</summary>
public sealed class ShadInstall
{
    public const string BackupSuffix = ".blueline-original";

    public ShadInstall(string executable, string? appData = null)
    {
        Executable = executable;
        Folder = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? throw new ArgumentException("That is not a file path.", nameof(executable));
        // A "user" folder beside the working directory takes priority, and launches run from the exe's folder.
        var portable = Path.Combine(Folder, "user");
        UserFolder = Directory.Exists(portable) ? portable
            : Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "shadPS4");
    }

    public string Executable { get; }
    public string Folder { get; }
    public string UserFolder { get; }
    public ShadConfigFormat Format => DetectFormat(Executable);
    public IReadOnlyList<ShadSetting> Settings => Format == ShadConfigFormat.Json ? ShadSettings.Json : ShadSettings.All;
    public string ConfigPath => Path.Combine(UserFolder, Format == ShadConfigFormat.Json ? "config.json" : "config.toml");
    public string BackupPath => ConfigPath + BackupSuffix;
    public bool HasConfig => File.Exists(ConfigPath);
    public bool HasBackup => File.Exists(BackupPath);

    /// <summary>Whether this build can take settings for one game from a JSON file that the form can edit.</summary>
    public bool EditsGameConfig => Format == ShadConfigFormat.Json;

    /// <summary>The per-game JSON file for a title, whether or not it exists yet.</summary>
    public string GameConfigFile(string titleId) => Path.Combine(UserFolder, "custom_configs", titleId + ".json");

    /// <summary>The file holding settings just for this title, if any. It would override the shared settings.</summary>
    public string? GameConfigPath(string titleId)
    {
        var folder = Path.Combine(UserFolder, "custom_configs");
        return new[] { ".toml", ".json" }.Select(e => Path.Combine(folder, titleId + e)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Settings as the emulator would see them when <paramref name="path"/> is the file being edited. A per-game file
    /// lists only what it changes, so anything it leaves out comes from the shared file.
    /// </summary>
    public IConfigEditor ReadSettings(string? path = null)
    {
        path ??= ConfigPath;
        var shared = Open(File.ReadAllText(ConfigPath));
        return path != ConfigPath && File.Exists(path) ? new LayeredConfig(Open(File.ReadAllText(path)), shared) : shared;
    }

    /// <summary>
    /// Saves changed values to <paramref name="path"/>, or the shared file when null. The first save keeps a copy of
    /// how that file was; an empty copy means it did not exist.
    /// </summary>
    public void SaveSettings(IReadOnlyDictionary<ShadSetting, string> changes, string? path = null)
    {
        path ??= ConfigPath;
        foreach (var (setting, value) in changes)
            if (ShadSettings.Validate(setting, value) is { } problem)
                throw new ArgumentException(problem);

        var existed = File.Exists(path);
        var original = existed ? File.ReadAllText(path) : "{\r\n}";
        var config = Open(original);
        foreach (var (setting, value) in changes)
            ShadSettings.Write(config, setting, value);

        // Read the result back before it replaces anything.
        var check = Open(config.ToString()!);
        foreach (var (setting, value) in changes)
            if (ShadSettings.Read(check, setting) != value)
                throw new InvalidDataException($"{setting.Label} did not read back as written, so nothing was saved.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path + BackupSuffix))
            File.WriteAllText(path + BackupSuffix, existed ? original : "", new UTF8Encoding(false));
        config.Save(path);
    }

    public bool HasBackupFor(string path) => File.Exists(path + BackupSuffix);

    public void RestoreBackup(string? path = null)
    {
        path ??= ConfigPath;
        var backup = path + BackupSuffix;
        if (!File.Exists(backup))
            throw new FileNotFoundException("There is no saved copy of the original settings.", backup);
        if (new FileInfo(backup).Length > 0)
        {
            File.Copy(backup, path, overwrite: true);
            return;
        }
        // Blueline created this file, so "as it was" means gone.
        File.Delete(path);
        File.Delete(backup);
    }

    /// <summary>
    /// Whether the settings in effect keep file opens out of the log, which is how the Play tab sees mods load.
    /// Only JSON builds are checked; their levels run trace, debug, info, warning, error, critical.
    /// </summary>
    public bool LogHidesFileOpens(string? path = null)
    {
        if (Format != ShadConfigFormat.Json || !HasConfig)
            return false;
        try
        {
            var filter = ReadSettings(path).GetRaw("Log", "filter")?.Trim('"');
            return !string.IsNullOrWhiteSpace(filter) && !Regex.IsMatch(filter, @":(trace|debug|info)\b", RegexOptions.IgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false; // Not a reason to refuse a launch; the settings form reports the broken file.
        }
    }

    private IConfigEditor Open(string text) => Format == ShadConfigFormat.Json ? new JsonConfigEditor(text) : new TomlEditor(text);

    /// <summary>
    /// Builds share one settings folder, which can hold both files, so only the program shows which one it reads.
    /// The key below is in the JSON builds and not in the TOML ones. A missing program counts as TOML.
    /// </summary>
    private static ShadConfigFormat DetectFormat(string executable)
    {
        var file = new FileInfo(executable);
        if (!file.Exists)
            return ShadConfigFormat.Toml;
        return Formats.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc), _ =>
            File.ReadAllBytes(file.FullName).AsSpan().IndexOf("readbacks_mode"u8) >= 0 ? ShadConfigFormat.Json : ShadConfigFormat.Toml);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, long, DateTime), ShadConfigFormat> Formats = new();

    /// <summary>Play time the emulator has recorded for a title, or null if it has none.</summary>
    public string? PlayTime(string titleId)
    {
        var path = Path.Combine(UserFolder, "play_time.txt");
        if (!File.Exists(path))
            return null;
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == titleId)
                return parts[1];
        }
        return null;
    }

    /// <summary>The keyboard keys bound to each pad button, in file order. global.ini holds only hotkeys, so this reads default.ini.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> KeyboardBindings()
    {
        var path = Path.Combine(UserFolder, "input_config", "default.ini");
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;

        var inKeyboard = false;
        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (text.StartsWith('#'))
            {
                // Comments split the file into keyboard and controller sections.
                if (text.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) || text.Contains("Alternatives", StringComparison.OrdinalIgnoreCase))
                    inKeyboard = true;
                else if (text.Contains("Controller", StringComparison.OrdinalIgnoreCase))
                    inKeyboard = false;
                continue;
            }
            var equals = text.IndexOf('=');
            if (!inKeyboard || equals <= 0)
                continue;
            var button = text[..equals].Trim();
            var key = text[(equals + 1)..].Trim();
            result[button] = [.. result.GetValueOrDefault(button) ?? [], key];
        }
        return result;
    }

    /// <summary>Any shadPS4 running on this PC, including ones this app did not start.</summary>
    public static bool AnyRunning()
    {
        var running = Process.GetProcessesByName("shadPS4");
        foreach (var process in running)
            process.Dispose();
        return running.Length > 0;
    }
}

/// <summary>
/// One game launched from its eboot. Output goes straight to a log file through cmd, so the game keeps running,
/// and keeps logging, if this app closes. Runs from the emulator's folder, as the build's own Play.cmd does.
/// </summary>
public sealed class ShadSession : IDisposable
{
    private static readonly Regex ModArchive = new(@"open: path = \S*/(?<name>patch_(?:inxp|game)_99_[^\s/]+\.big)", RegexOptions.Compiled);
    private readonly Process _process;
    private readonly HashSet<string> _modsOpened = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recent = [];
    private readonly StringBuilder _partial = new();
    private long _position;

    private ShadSession(Process process, string logPath)
    {
        _process = process;
        LogPath = logPath;
        Started = DateTime.Now;
    }

    public const int RecentLimit = 300;

    public string LogPath { get; }
    public DateTime Started { get; }
    public bool Running => !_process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    public int ErrorLines { get; private set; }
    public IReadOnlyCollection<string> ModsOpened => _modsOpened;

    /// <summary>Recent log lines, without the network polling that otherwise fills the log.</summary>
    public IReadOnlyList<string> Recent => _recent;

    public static ShadSession Start(ShadInstall install, string gameFolder, string logFolder)
    {
        var eboot = Path.Combine(gameFolder, "eboot.bin");
        if (!File.Exists(install.Executable))
            throw new FileNotFoundException($"The emulator is not at {install.Executable}.");
        if (!File.Exists(eboot))
            throw new FileNotFoundException($"There is no eboot.bin in {gameFolder}.");

        Directory.CreateDirectory(logFolder);
        foreach (var old in new DirectoryInfo(logFolder).GetFiles("game-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(9))
        {
            try { old.Delete(); }
            catch (IOException) { /* Still open in another viewer; a later launch will catch it. */ }
        }
        var log = Path.Combine(logFolder, $"game-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var start = new ProcessStartInfo("cmd.exe")
        {
            // /s keeps the outer quotes simple: everything between the first and last quote is the command.
            Arguments = $"/s /c \"\"{install.Executable}\" \"{eboot}\" > \"{log}\" 2>&1\"",
            WorkingDirectory = install.Folder,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(start) ?? throw new IOException("Windows did not start the emulator.");
        return new ShadSession(process, log);
    }

    /// <summary>Reads what the emulator has logged since the last call. Returns whether anything was new.</summary>
    public bool Poll()
    {
        if (!File.Exists(LogPath))
            return false;
        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= _position)
            return false;

        stream.Position = _position;
        var buffer = new byte[stream.Length - _position];
        stream.ReadExactly(buffer);
        _position = stream.Length;

        _partial.Append(Encoding.UTF8.GetString(buffer));
        var text = _partial.ToString();
        var end = text.LastIndexOf('\n');
        if (end < 0)
            return false;
        _partial.Clear().Append(text.AsSpan(end + 1));

        foreach (var raw in text[..end].Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("<Error>", StringComparison.Ordinal) || line.Contains("<Critical>", StringComparison.Ordinal))
                ErrorLines++;
            if (ModArchive.Match(line) is { Success: true } match)
                _modsOpened.Add(match.Groups["name"].Value);
            if (line.Contains("sceNetEpoll", StringComparison.Ordinal))
                continue;
            _recent.Add(line);
        }
        if (_recent.Count > RecentLimit)
            _recent.RemoveRange(0, _recent.Count - RecentLimit);
        return true;
    }

    /// <summary>Ends the game this session started, and nothing else.</summary>
    public void Stop()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
    }

    public void Dispose() => _process.Dispose();
}
