using Blueline.Core.Emulator;
using Blueline.Formats.Sony;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Blueline.Tests;

[TestClass]
public class PlayTests
{
    // Written the way the real file is: CRLF-free sections, arrays, and settings this tool does not know.
    private const string Sample = """
        [General]
        customCpuCores = []
        extraDmemInMbytes = 0
        userNames = ["shadPS4", "shadps4-2"]

        [GPU]
        Fullscreen = false
        FullscreenMode = "Windowed"
        internalScreenHeight = 720
        internalScreenWidth = 1280
        presentMode = "Mailbox"
        vblankFrequency = 60

        [GUI.ToolbarVisibility]
        [Settings]
        consoleLanguage = 1

        """;

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "blueline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static ShadSetting Setting(string key) => ShadSettings.All.First(s => s.Key == key);

    [TestMethod]
    public void RealParamSfo_ReadsTitleAndVersion()
    {
        var root = Path.GetDirectoryName(typeof(PlayTests).Assembly.Location)!;
        while (!File.Exists(Path.Combine(root, "Blueline.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new AssertInconclusiveException("Project root not found.");
        var sfo = ParamSfo.Parse(File.ReadAllBytes(Path.Combine(root, "NHL 19", "CUSA11126", "sce_sys", "param.sfo")));

        Assert.AreEqual("NHL® 19", sfo["TITLE"]);
        Assert.AreEqual("CUSA11126", sfo["TITLE_ID"]);
        Assert.AreEqual("01.60", sfo["APP_VER"]);
        Assert.AreEqual("UP0006-CUSA11126_00-NHLICEHOCKEY2019", sfo["CONTENT_ID"]);
    }

    [TestMethod]
    public void ParamSfo_RefusesOtherFiles()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ParamSfo.Parse(new byte[64]));
    }

    [TestMethod]
    public void Toml_ChangesOnlyTheLinesAsked()
    {
        var config = new TomlEditor(Sample);
        ShadSettings.Write(config, Setting("vblankFrequency"), "120");

        var before = Sample.Split('\n');
        var after = config.ToString().Split('\n');
        Assert.AreEqual(before.Length, after.Length);
        var changed = before.Zip(after).Where(p => p.First != p.Second).ToList();
        Assert.AreEqual(1, changed.Count);
        Assert.AreEqual("vblankFrequency = 120", changed[0].Second);
    }

    [TestMethod]
    public void Toml_KeepsWindowsLineEndings()
    {
        var text = Sample.Replace("\n", "\r\n");
        Assert.AreEqual(text, new TomlEditor(text).ToString());
    }

    [TestMethod]
    public void Toml_AddsMissingKeyInsideItsSection()
    {
        var config = new TomlEditor(Sample);
        config.SetRaw("GPU", "rcasEnabled", "true");

        Assert.AreEqual("true", config.GetRaw("GPU", "rcasEnabled"));
        Assert.IsNull(config.GetRaw("General", "rcasEnabled"));
        var lines = config.ToString().Split('\n').ToList();
        Assert.AreEqual(lines.IndexOf("vblankFrequency = 60") + 1, lines.IndexOf("rcasEnabled = true"));
    }

    [TestMethod]
    public void Toml_AddsMissingSection()
    {
        var config = new TomlEditor(Sample);
        config.SetRaw("Vulkan", "pipelineCacheEnable", "true");
        Assert.AreEqual("true", new TomlEditor(config.ToString()).GetRaw("Vulkan", "pipelineCacheEnable"));
    }

    [TestMethod]
    public void Settings_SameKeyInAnotherSectionIsNotTouched()
    {
        var config = new TomlEditor("[A]\nx = 1\n[B]\nx = 2\n");
        config.SetRaw("B", "x", "3");
        Assert.AreEqual("1", config.GetRaw("A", "x"));
        Assert.AreEqual("3", config.GetRaw("B", "x"));
    }

    [TestMethod]
    public void Settings_ResolutionWritesBothKeys()
    {
        var config = new TomlEditor(Sample);
        ShadSettings.Write(config, Setting("internalScreenWidth"), "1920x1080");
        Assert.AreEqual("1920", config.GetRaw("GPU", "internalScreenWidth"));
        Assert.AreEqual("1080", config.GetRaw("GPU", "internalScreenHeight"));
        Assert.AreEqual("1920x1080", ShadSettings.Read(config, Setting("internalScreenWidth")));
    }

    [TestMethod]
    public void Settings_DisplayModeKeepsFullscreenFlagInStep()
    {
        var config = new TomlEditor(Sample);
        ShadSettings.Write(config, Setting("FullscreenMode"), "Fullscreen (Borderless)");
        Assert.AreEqual("\"Fullscreen (Borderless)\"", config.GetRaw("GPU", "FullscreenMode"));
        Assert.AreEqual("true", config.GetRaw("GPU", "Fullscreen"));

        ShadSettings.Write(config, Setting("FullscreenMode"), "Windowed");
        Assert.AreEqual("false", config.GetRaw("GPU", "Fullscreen"));
    }

    [TestMethod]
    public void Settings_NumericChoiceIsNotQuoted()
    {
        var config = new TomlEditor(Sample);
        ShadSettings.Write(config, Setting("consoleLanguage"), "2");
        Assert.AreEqual("2", config.GetRaw("Settings", "consoleLanguage"));
    }

    [TestMethod]
    [DataRow("vblankFrequency", "abc")]
    [DataRow("vblankFrequency", "5")]
    [DataRow("vblankFrequency", "-60")]
    [DataRow("internalScreenWidth", "1920")]
    [DataRow("internalScreenWidth", "10x10")]
    [DataRow("fsrEnabled", "yes")]
    public void Settings_RefuseBadValues(string key, string value)
    {
        Assert.IsNotNull(ShadSettings.Validate(Setting(key), value));
        Assert.ThrowsExactly<ArgumentException>(() => ShadSettings.Write(new TomlEditor(Sample), Setting(key), value));
    }

    [TestMethod]
    public void Install_SaveKeepsOneBackupAndRestores()
    {
        var folder = TempFolder();
        try
        {
            var user = Path.Combine(folder, "appdata", "shadPS4");
            Directory.CreateDirectory(user);
            File.WriteAllText(Path.Combine(user, "config.toml"), Sample);
            var install = new ShadInstall(Path.Combine(folder, "emu", "shadPS4.exe"), Path.Combine(folder, "appdata"));

            install.SaveSettings(new Dictionary<ShadSetting, string> { [Setting("vblankFrequency")] = "120" });
            install.SaveSettings(new Dictionary<ShadSetting, string> { [Setting("vblankFrequency")] = "144" });
            Assert.AreEqual("144", TomlEditor.Load(install.ConfigPath).GetRaw("GPU", "vblankFrequency"));
            Assert.AreEqual(Sample, File.ReadAllText(install.BackupPath));

            install.RestoreBackup();
            Assert.AreEqual(Sample, File.ReadAllText(install.ConfigPath));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void Install_BadValueLeavesFileUntouched()
    {
        var folder = TempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "config.toml"), Sample);
            Directory.CreateDirectory(Path.Combine(folder, "emu", "user"));
            var user = Path.Combine(folder, "emu", "user");
            File.Move(Path.Combine(folder, "config.toml"), Path.Combine(user, "config.toml"));
            var install = new ShadInstall(Path.Combine(folder, "emu", "shadPS4.exe"));

            Assert.AreEqual(user, install.UserFolder);
            Assert.ThrowsExactly<ArgumentException>(() => install.SaveSettings(new Dictionary<ShadSetting, string> { [Setting("vblankFrequency")] = "nope" }));
            Assert.AreEqual(Sample, File.ReadAllText(install.ConfigPath));
            Assert.IsFalse(install.HasBackup);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // The real file's shape: CRLF, no final newline, a float that must keep its ".0", and keys this tool does not offer.
    private const string JsonSample =
        "{\r\n  \"General\": {\r\n    \"console_language\": 1,\r\n    \"install_dirs\": [\r\n      {\r\n        \"enabled\": true,\r\n        \"path\": \"C:\\\\Games\\\\NHL+19\"\r\n      }\r\n    ],\r\n    \"trophy_notification_duration\": 6.0\r\n  },\r\n" +
        "  \"GPU\": {\r\n    \"full_screen\": false,\r\n    \"full_screen_mode\": \"Windowed\",\r\n    \"internal_screen_height\": 720,\r\n    \"internal_screen_width\": 1280,\r\n    \"vblank_frequency\": 60,\r\n    \"window_height\": 720,\r\n    \"window_width\": 1280\r\n  },\r\n" +
        "  \"Log\": {\r\n    \"filter\": \"*:error\",\r\n    \"shadnet_webapi_server\": \"http://srv.example:31315\"\r\n  }\r\n}";

    private static ShadSetting JsonSetting(string key) => ShadSettings.Json.First(s => s.Key == key);

    private static string FakeExe(string folder, string marker)
    {
        var path = Path.Combine(folder, "emu", "shadPS4.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"MZ...{marker}...");
        return path;
    }

    [TestMethod]
    public void Json_RoundTripIsExact()
    {
        Assert.AreEqual(JsonSample, new JsonConfigEditor(JsonSample).ToString());
    }

    [TestMethod]
    public void Json_KeepsTheFilesOwnIndentation()
    {
        foreach (var text in new[] { "{\n    \"A\": {\n        \"b\": 1\n    }\n}\n", "{\n\t\"A\": {\n\t\t\"b\": 1\n\t}\n}\n" })
            Assert.AreEqual(text, new JsonConfigEditor(text).ToString());
    }

    [TestMethod]
    public void Json_RealCustomConfigRoundTripsExactly()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "shadPS4", "custom_configs", "CUSA11126.json");
        if (!File.Exists(path))
            Assert.Inconclusive("This PC has no custom config for NHL 19.");
        var text = File.ReadAllText(path);
        Assert.AreEqual(text, new JsonConfigEditor(text).ToString());
    }

    [TestMethod]
    public void Json_ChangesOnlyTheLinesAsked()
    {
        var config = new JsonConfigEditor(JsonSample);
        ShadSettings.Write(config, JsonSetting("vblank_frequency"), "120");

        var before = JsonSample.Split('\n');
        var after = config.ToString().Split('\n');
        Assert.AreEqual(before.Length, after.Length);
        var changed = before.Zip(after).Where(p => p.First != p.Second).ToList();
        Assert.AreEqual(1, changed.Count);
        Assert.AreEqual("    \"vblank_frequency\": 120,\r", changed[0].Second);
    }

    [TestMethod]
    public void Json_DisplayModeResolutionAndEmptyFilter()
    {
        var config = new JsonConfigEditor(JsonSample);
        ShadSettings.Write(config, JsonSetting("full_screen_mode"), "Fullscreen (Borderless)");
        ShadSettings.Write(config, JsonSetting("window_width"), "1920x1080");
        ShadSettings.Write(config, JsonSetting("filter"), "");

        Assert.AreEqual("\"Fullscreen (Borderless)\"", config.GetRaw("GPU", "full_screen_mode"));
        Assert.AreEqual("true", config.GetRaw("GPU", "full_screen"));
        Assert.AreEqual("1920", config.GetRaw("GPU", "window_width"));
        Assert.AreEqual("1080", config.GetRaw("GPU", "window_height"));
        Assert.AreEqual("", ShadSettings.Read(config, JsonSetting("filter")));
        Assert.IsNotNull(ShadSettings.Validate(JsonSetting("present_mode"), ""));
    }

    [TestMethod]
    public void Json_BrokenFileIsRefused()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => new JsonConfigEditor("{ not json"));
        Assert.ThrowsExactly<InvalidDataException>(() => new JsonConfigEditor("[1, 2]"));
    }

    [TestMethod]
    public void Install_RealBuildsReadTheirOwnFormat()
    {
        var root = Path.GetDirectoryName(typeof(PlayTests).Assembly.Location)!;
        while (!File.Exists(Path.Combine(root, "Blueline.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new AssertInconclusiveException("Project root not found.");
        var shadlix = Path.Combine(root, "ShadPS4", "Shadlix-NHL-2026-08-31", "shadPS4.exe");
        Assert.AreEqual(ShadConfigFormat.Toml, new ShadInstall(shadlix).Format);

        var boost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OvertimeRoster", "boost", "shadPS4.exe");
        if (File.Exists(boost))
            Assert.AreEqual(ShadConfigFormat.Json, new ShadInstall(boost).Format);
    }

    [TestMethod]
    public void Install_PicksFormatFromTheProgram()
    {
        var folder = TempFolder();
        try
        {
            var json = new ShadInstall(FakeExe(folder, "readbacks_mode"), Path.Combine(folder, "data"));
            Assert.AreEqual(ShadConfigFormat.Json, json.Format);
            Assert.AreEqual("config.json", Path.GetFileName(json.ConfigPath));
            Assert.AreSame(ShadSettings.Json, json.Settings);

            var toml = new ShadInstall(FakeExe(folder, "readbackSpeedMode long enough"), Path.Combine(folder, "data"));
            Assert.AreEqual(ShadConfigFormat.Toml, toml.Format);
            Assert.AreEqual("config.toml", Path.GetFileName(toml.ConfigPath));
            Assert.IsFalse(toml.EditsGameConfig);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void Install_GameFileLayersOverSharedAndSavesAlone()
    {
        var folder = TempFolder();
        try
        {
            var install = new ShadInstall(FakeExe(folder, "readbacks_mode"), Path.Combine(folder, "data"));
            Directory.CreateDirectory(install.UserFolder);
            File.WriteAllText(install.ConfigPath, JsonSample);
            var game = install.GameConfigFile("CUSA11126");
            Directory.CreateDirectory(Path.GetDirectoryName(game)!);
            const string partial = "{\r\n  \"GPU\": {\r\n    \"vblank_frequency\": 90\r\n  }\r\n}";
            File.WriteAllText(game, partial);

            var view = install.ReadSettings(game);
            Assert.AreEqual("90", ShadSettings.Read(view, JsonSetting("vblank_frequency")));
            Assert.AreEqual("1280x720", ShadSettings.Read(view, JsonSetting("window_width")));

            install.SaveSettings(new Dictionary<ShadSetting, string> { [JsonSetting("window_width")] = "1920x1080" }, game);
            Assert.AreEqual(JsonSample, File.ReadAllText(install.ConfigPath));
            Assert.AreEqual(partial, File.ReadAllText(game + ShadInstall.BackupSuffix));
            Assert.IsFalse(install.HasBackup);
            var saved = JsonConfigEditor.Load(game);
            Assert.AreEqual("90", saved.GetRaw("GPU", "vblank_frequency"));
            Assert.AreEqual("1920", saved.GetRaw("GPU", "window_width"));
            Assert.IsNull(saved.GetRaw("GPU", "full_screen"));

            install.RestoreBackup(game);
            Assert.AreEqual(partial, File.ReadAllText(game));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void Install_CreatesMissingGameFileAndRestoreRemovesIt()
    {
        var folder = TempFolder();
        try
        {
            var install = new ShadInstall(FakeExe(folder, "readbacks_mode"), Path.Combine(folder, "data"));
            Directory.CreateDirectory(install.UserFolder);
            File.WriteAllText(install.ConfigPath, JsonSample);
            var game = install.GameConfigFile("CUSA11126");

            Assert.AreEqual("60", ShadSettings.Read(install.ReadSettings(game), JsonSetting("vblank_frequency")));
            install.SaveSettings(new Dictionary<ShadSetting, string> { [JsonSetting("vblank_frequency")] = "120" }, game);
            Assert.AreEqual("120", JsonConfigEditor.Load(game).GetRaw("GPU", "vblank_frequency"));
            Assert.IsTrue(install.HasBackupFor(game));

            install.RestoreBackup(game);
            Assert.IsFalse(File.Exists(game));
            Assert.IsFalse(install.HasBackupFor(game));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void Install_BrokenGameFileIsLeftAlone()
    {
        var folder = TempFolder();
        try
        {
            var install = new ShadInstall(FakeExe(folder, "readbacks_mode"), Path.Combine(folder, "data"));
            Directory.CreateDirectory(install.UserFolder);
            File.WriteAllText(install.ConfigPath, JsonSample);
            var game = install.GameConfigFile("CUSA11126");
            Directory.CreateDirectory(Path.GetDirectoryName(game)!);
            File.WriteAllText(game, "{ broken");

            Assert.ThrowsExactly<InvalidDataException>(() => install.SaveSettings(new Dictionary<ShadSetting, string> { [JsonSetting("vblank_frequency")] = "120" }, game));
            Assert.AreEqual("{ broken", File.ReadAllText(game));
            Assert.IsFalse(install.HasBackupFor(game));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    [DataRow("*:error", true)]
    [DataRow("*:warning", true)]
    [DataRow("*:info", false)]
    [DataRow("*:warning Kernel.Fs:debug", false)]
    [DataRow("", false)]
    public void Install_KnowsWhenTheLogHidesFileOpens(string filter, bool hides)
    {
        var folder = TempFolder();
        try
        {
            var install = new ShadInstall(FakeExe(folder, "readbacks_mode"), Path.Combine(folder, "data"));
            Directory.CreateDirectory(install.UserFolder);
            File.WriteAllText(install.ConfigPath, $"{{\"Log\": {{\"filter\": \"{filter}\"}}}}");
            Assert.AreEqual(hides, install.LogHidesFileOpens());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestMethod]
    public void Install_ReadsPlayTimeAndKeyboardBindings()
    {
        var folder = TempFolder();
        try
        {
            var user = Path.Combine(folder, "user-data", "shadPS4");
            Directory.CreateDirectory(Path.Combine(user, "input_config"));
            File.WriteAllText(Path.Combine(user, "play_time.txt"), "CUSA37933 1:36:59 1791857529\nCUSA11126 0:10:59\n");
            File.WriteAllText(Path.Combine(user, "input_config", "default.ini"),
                "# Keyboard bindings\ncross = kp2\n# Alternatives for users without a keypad\ncross = n\noptions = enter\n# Controller bindings\ncross = cross\n");
            var install = new ShadInstall(Path.Combine(folder, "emu", "shadPS4.exe"), Path.Combine(folder, "user-data"));

            Assert.AreEqual("0:10:59", install.PlayTime("CUSA11126"));
            Assert.IsNull(install.PlayTime("CUSA00000"));
            var bindings = install.KeyboardBindings();
            CollectionAssert.AreEqual(new[] { "kp2", "n" }, bindings["cross"].ToArray());
            CollectionAssert.AreEqual(new[] { "enter" }, bindings["options"].ToArray());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
