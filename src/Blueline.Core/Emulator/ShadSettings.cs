using System.Globalization;

namespace Blueline.Core.Emulator;

public enum ShadSettingKind { Toggle, Choice, Number, Resolution }

public sealed record ShadOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One setting the Play tab offers. For a resolution, <see cref="Key"/> is width and <see cref="HeightKey"/> height.
/// <see cref="GameOverride"/> is false when the emulator ignores this key in a per-game file.
/// </summary>
public sealed record ShadSetting(
    string Group, string Section, string Key, string Label, string Hint, ShadSettingKind Kind,
    IReadOnlyList<ShadOption>? Options = null, int Min = 0, int Max = 0, string? HeightKey = null, bool GameOverride = true)
{
    public string Id => $"{Section}.{Key}";
}

/// <summary>The shadPS4 settings worth showing for NHL 19. Their keys and values are the ones each build writes.</summary>
public static class ShadSettings
{
    public const string Display = "Display";
    public const string Performance = "Performance";
    public const string System = "System";

    private static readonly IReadOnlyList<ShadOption> Resolutions =
        new[] { "1280x720", "1600x900", "1920x1080", "2560x1440", "3840x2160" }
        .Select(r => new ShadOption(r, r.Replace('x', '×'))).ToList();

    public static IReadOnlyList<ShadSetting> All { get; } =
    [
        new(Display, "GPU", "screenWidth", "Window size", "Size of the window on your desktop.", ShadSettingKind.Resolution, Resolutions, HeightKey: "screenHeight"),
        new(Display, "GPU", "internalScreenWidth", "Internal resolution", "The size the emulator reports for rendering.", ShadSettingKind.Resolution, Resolutions, HeightKey: "internalScreenHeight"),
        new(Display, "GPU", "FullscreenMode", "Display mode", "", ShadSettingKind.Choice,
            [new("Windowed", "Windowed"), new("Fullscreen", "Fullscreen"), new("Fullscreen (Borderless)", "Borderless")]),
        new(Display, "GPU", "presentMode", "Present mode", "Mailbox and Fifo use vsync. Immediate does not.", ShadSettingKind.Choice,
            [new("Mailbox", "Mailbox"), new("Fifo", "Fifo"), new("Immediate", "Immediate")]),
        new(Display, "GPU", "vblankFrequency", "Vblank frequency", "60 is the console's rate. The game's timing can depend on it.", ShadSettingKind.Number, Min: 30, Max: 480),
        new(Display, "GPU", "fpsLimiterEnabled", "FPS limiter", "Off means vblank frequency sets the pace.", ShadSettingKind.Toggle),
        new(Display, "GPU", "fpsLimit", "FPS limit", "Used only when the limiter is on.", ShadSettingKind.Number, Min: 10, Max: 480),
        new(Display, "GPU", "fsrEnabled", "FSR upscaling", "", ShadSettingKind.Toggle),
        new(Display, "GPU", "rcasEnabled", "RCAS sharpening", "", ShadSettingKind.Toggle),
        new(Display, "GPU", "rcasAttenuation", "RCAS attenuation", "", ShadSettingKind.Number, Min: 0, Max: 3000),
        new(Display, "GPU", "allowHDR", "Allow HDR", "", ShadSettingKind.Toggle),

        new(Performance, "GPU", "readbackSpeedMode", "Readback speed", "", ShadSettingKind.Choice,
            [new("0", "Disabled"), new("1", "Unsafe"), new("2", "Low"), new("3", "Default"), new("4", "Fast")]),
        new(Performance, "GPU", "readbackLinearImages", "Readback linear images", "", ShadSettingKind.Toggle),
        new(Performance, "GPU", "directMemoryAccess", "Direct memory access", "", ShadSettingKind.Toggle),
        new(Performance, "GPU", "copyGPUBuffers", "Copy GPU buffers", "", ShadSettingKind.Toggle),
        new(Performance, "GPU", "syncStorageImages", "Sync storage images", "A setting from this NHL-focused fork.", ShadSettingKind.Toggle),
        new(Performance, "GPU", "syncRenderTargetAliases", "Sync render target aliases", "A setting from this NHL-focused fork.", ShadSettingKind.Toggle),
        new(Performance, "GPU", "shaderSkipsEnabled", "Shader skips", "", ShadSettingKind.Toggle),
        new(Performance, "Vulkan", "pipelineCacheEnable", "Pipeline cache", "Keeps compiled shaders, so later launches are smoother.", ShadSettingKind.Toggle),
        new(Performance, "Vulkan", "pipelineCacheArchive", "Archive pipeline cache", "", ShadSettingKind.Toggle),
        new(Performance, "General", "extraDmemInMbytes", "Extra direct memory (MB)", "", ShadSettingKind.Number, Min: 0, Max: 8192),

        new(System, "General", "isPS4Pro", "PS4 Pro mode", "", ShadSettingKind.Toggle),
        new(System, "Settings", "consoleLanguage", "Console language", "Which language the game is told to use.", ShadSettingKind.Choice,
            [new("1", "English (US)"), new("18", "English (UK)"), new("2", "French"), new("22", "French (Canada)"), new("4", "German"), new("23", "Czech"), new("12", "Finnish"), new("8", "Russian"), new("13", "Swedish")]),
        new(System, "General", "volumeSlider", "Volume", "0 to 100.", ShadSettingKind.Number, Min: 0, Max: 100),
        new(System, "General", "muteEnabled", "Mute", "", ShadSettingKind.Toggle),
        new(System, "General", "isConnectedToNetwork", "Connected to network", "", ShadSettingKind.Toggle),
        new(System, "Input", "xCircleButtonSwap", "Swap ⊗ and ○", "", ShadSettingKind.Toggle),
        new(System, "Input", "backgroundControllerInput", "Controller works when unfocused", "Off means the window must be in front to take controller input.", ShadSettingKind.Toggle),
        new(System, "General", "pauseOnUnfocus", "Pause when unfocused", "", ShadSettingKind.Toggle),
        new(System, "Debug", "showFpsCounter", "Show FPS counter", "", ShadSettingKind.Toggle),
    ];

    private const string SharedOnly = "This build reads it only from config.json.";

    private static readonly IReadOnlyList<ShadOption> Languages =
        [new("1", "English (US)"), new("18", "English (UK)"), new("2", "French"), new("22", "French (Canada)"), new("4", "German"), new("23", "Czech"), new("12", "Finnish"), new("8", "Russian"), new("13", "Swedish")];

    /// <summary>
    /// For builds that keep settings in config.json and custom_configs. Every per-game key except the ones marked
    /// otherwise was seen in the emulator's "Game-specific overrides applied" log line; see RESEARCH section 4.3.
    /// </summary>
    public static IReadOnlyList<ShadSetting> Json { get; } =
    [
        new(Display, "GPU", "window_width", "Window size", "Size of the window on your desktop.", ShadSettingKind.Resolution, Resolutions, HeightKey: "window_height"),
        new(Display, "GPU", "internal_screen_width", "Internal resolution", SharedOnly, ShadSettingKind.Resolution, Resolutions, HeightKey: "internal_screen_height", GameOverride: false),
        new(Display, "GPU", "full_screen_mode", "Display mode", "", ShadSettingKind.Choice,
            [new("Windowed", "Windowed"), new("Fullscreen", "Fullscreen"), new("Fullscreen (Borderless)", "Borderless")]),
        new(Display, "GPU", "present_mode", "Present mode", "Mailbox and Fifo use vsync. Immediate does not.", ShadSettingKind.Choice,
            [new("Mailbox", "Mailbox"), new("Fifo", "Fifo"), new("Immediate", "Immediate")]),
        new(Display, "GPU", "vblank_frequency", "Vblank frequency", "60 is the console's rate. The game's timing can depend on it.", ShadSettingKind.Number, Min: 30, Max: 480),
        new(Display, "GPU", "rcas_attenuation", "RCAS attenuation", "", ShadSettingKind.Number, Min: 0, Max: 3000),
        new(Display, "GPU", "fsr_enabled", "FSR upscaling", "", ShadSettingKind.Toggle),
        new(Display, "GPU", "rcas_enabled", "RCAS sharpening", "", ShadSettingKind.Toggle),
        new(Display, "GPU", "hdr_allowed", "Allow HDR", "", ShadSettingKind.Toggle),
        new(Display, "General", "show_fps_counter", "Show FPS counter", SharedOnly, ShadSettingKind.Toggle, GameOverride: false),

        new(Performance, "GPU", "readbacks_mode", "Readbacks mode", "The emulator gives these levels no names here.", ShadSettingKind.Choice,
            [new("0", "Level 0"), new("1", "Level 1"), new("2", "Level 2")]),
        new(Performance, "General", "extra_dmem_in_mbytes", "Extra direct memory (MB)", "", ShadSettingKind.Number, Min: 0, Max: 8192),
        new(Performance, "General", "extra_fmem_in_mbytes", "Extra flexible memory (MB)", "", ShadSettingKind.Number, Min: 0, Max: 8192),
        new(Performance, "GPU", "readback_linear_images_enabled", "Readback linear images", "", ShadSettingKind.Toggle),
        new(Performance, "GPU", "direct_memory_access_enabled", "Direct memory access", "", ShadSettingKind.Toggle),
        new(Performance, "GPU", "copy_gpu_buffers", "Copy GPU buffers", "", ShadSettingKind.Toggle),
        new(Performance, "GPU", "inline_fetch_shader", "Inline fetch shader", "", ShadSettingKind.Toggle),
        new(Performance, "GPU", "fast_shader_math", "Fast shader math", "", ShadSettingKind.Toggle),
        new(Performance, "GPU", "patch_shaders", "Patch shaders", "", ShadSettingKind.Toggle),
        new(Performance, "Vulkan", "pipeline_cache_enabled", "Pipeline cache", "Keeps compiled shaders, so later launches are smoother.", ShadSettingKind.Toggle),
        new(Performance, "Vulkan", "pipeline_cache_archived", "Archive pipeline cache", "", ShadSettingKind.Toggle),

        new(System, "General", "console_language", "Console language", "Which language the game is told to use.", ShadSettingKind.Choice, Languages),
        new(System, "General", "volume_slider", "Volume", "0 to 100.", ShadSettingKind.Number, Min: 0, Max: 100),
        new(System, "Log", "filter", "Log detail", "The Play tab counts opened mods only at Everything or Info.", ShadSettingKind.Choice,
            [new("", "Everything"), new("*:info", "Info"), new("*:warning", "Warnings"), new("*:error", "Errors only")]),
        new(System, "General", "neo_mode", "PS4 Pro mode", "", ShadSettingKind.Toggle),
        new(System, "Network", "connected_to_network", "Connected to network", "Tools like Overtime need this on for online play.", ShadSettingKind.Toggle),
        new(System, "Input", "is_circle_enter", "○ confirms instead of ⊗", "", ShadSettingKind.Toggle),
        new(System, "Input", "background_controller_input", "Controller works when unfocused", "Off means the window must be in front to take controller input.", ShadSettingKind.Toggle),
    ];

    /// <summary>Reads a setting as the form shows it: `true`, `Mailbox`, `720`, or `1280x720`. Null when missing.</summary>
    public static string? Read(IConfigEditor config, ShadSetting setting)
    {
        var raw = config.GetRaw(setting.Section, setting.Key);
        if (raw == null)
            return null;
        if (setting.Kind != ShadSettingKind.Resolution)
            return raw.Trim('"');
        return config.GetRaw(setting.Section, setting.HeightKey!) is { } height ? $"{raw}x{height}" : null;
    }

    /// <summary>Why a value cannot be saved, in words for the user, or null when it is fine.</summary>
    public static string? Validate(ShadSetting setting, string value)
    {
        switch (setting.Kind)
        {
            case ShadSettingKind.Toggle:
                return value is "true" or "false" ? null : $"{setting.Label} must be on or off.";
            case ShadSettingKind.Number:
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= setting.Min && number <= setting.Max
                    ? null : $"{setting.Label} must be a whole number from {setting.Min} to {setting.Max}.";
            case ShadSettingKind.Resolution:
                var parts = value.Split('x');
                return parts.Length == 2 && parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size is >= 320 and <= 7680)
                    ? null : $"{setting.Label} must look like 1280x720.";
            default:
                // An empty value is fine only where the list offers it, as the log filter's "Everything" does.
                return !value.Contains('"') && (value.Length > 0 || setting.Options?.Any(o => o.Value == value) == true)
                    ? null : $"{setting.Label} has no value.";
        }
    }

    /// <summary>Puts one form value into the file, plus anything that must change with it.</summary>
    public static void Write(IConfigEditor config, ShadSetting setting, string value)
    {
        if (Validate(setting, value) is { } problem)
            throw new ArgumentException(problem);

        switch (setting.Kind)
        {
            case ShadSettingKind.Resolution:
                var parts = value.Split('x');
                config.SetRaw(setting.Section, setting.Key, parts[0]);
                config.SetRaw(setting.Section, setting.HeightKey!, parts[1]);
                break;
            case ShadSettingKind.Choice when !int.TryParse(value, out _):
                config.SetRaw(setting.Section, setting.Key, $"\"{value}\"");
                break;
            default:
                config.SetRaw(setting.Section, setting.Key, value);
                break;
        }

        // The file carries the display mode twice, and the two have to agree.
        var fullscreenKey = setting.Key switch { "FullscreenMode" => "Fullscreen", "full_screen_mode" => "full_screen", _ => null };
        if (fullscreenKey != null)
            config.SetRaw(setting.Section, fullscreenKey, value == "Windowed" ? "false" : "true");
    }
}
