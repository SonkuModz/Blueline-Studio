using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Blueline.Formats.Video;

namespace Blueline.Core;

public enum VideoTrack { Picture, Matte }

public sealed record VideoStreamInfo(int Width, int Height, string Codec, string Rate, string? Language);

public sealed record VideoInfo(string Container, double Seconds, long Bytes, IReadOnlyList<VideoStreamInfo> Videos, string? AudioCodec, int AudioChannels, int AudioRate, bool HasVp6Matte, bool HasVp6Sound)
{
    public bool IsVp6 => Container.StartsWith("ea", StringComparison.Ordinal);
    public bool IsMatroska => Container.StartsWith("matroska", StringComparison.Ordinal);
    public bool HasMatte => HasVp6Matte || Videos.Count > 1;
    public VideoStreamInfo Picture => Videos[0];

    public double FramesPerSecond
    {
        get
        {
            var parts = Picture.Rate.Split('/');
            return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var top)
                && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var bottom) && bottom != 0 ? top / bottom : 0;
        }
    }

    public string Summary =>
        $"{Picture.Width}x{Picture.Height}, {FramesPerSecond:F2} fps, {Seconds:F1} s, {Picture.Codec.ToUpperInvariant()}"
        + (AudioCodec != null ? $", {AudioCodec} {AudioChannels} ch" : HasVp6Sound ? ", EA sound (not previewed)" : ", no sound")
        + (HasMatte ? ", with matte" : "");
}

public sealed record GameVideoResult(byte[] Content, double SourceSeconds, double TargetSeconds, bool AddedSilence);

/// <summary>
/// Runs the user's FFmpeg, which Blueline does not bundle, for everything to do with video: reading what a video
/// holds, making previews Windows can play, and encoding replacements. See RESEARCH section 6.8.
/// </summary>
public sealed class FfmpegTool(string executable, string probe)
{
    private const int GameKeyFrameInterval = 128;
    private const int MatteBitsPerSecond = 1_200_000;
    private const int MinimumVideoBits = 800_000;
    private const int AudioBitsPerChannel = 64_000;
    private const double VideoShareOfBits = 0.9;

    /// <summary>The game's files have no cues and only split a cluster when its 16-bit block times would overflow.</summary>
    private const string ClusterMilliseconds = "32000";
    private const string ClusterBytes = "1073741824";

    /// <summary>Converting to YUV squeezes a matte into 16-235, but the game's mattes use the full 0-255.</summary>
    private const string MatteFullRange = "lutyuv=y=(val-16)*255/219:u=128:v=128";

    public string Executable { get; } = executable;
    public string Probe { get; } = probe;

    /// <summary>The chosen ffmpeg.exe if there is one, otherwise the one on the PATH or installed by winget.</summary>
    public static FfmpegTool? Locate(string? configured)
    {
        var folders = new List<string>();
        if (configured is { Length: > 0 } && File.Exists(configured))
            folders.Add(Path.GetDirectoryName(configured)!);
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));

        foreach (var folder in folders)
        {
            var (ffmpeg, ffprobe) = (Path.Combine(folder, "ffmpeg.exe"), Path.Combine(folder, "ffprobe.exe"));
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                return new FfmpegTool(ffmpeg, ffprobe);
        }
        return null;
    }

    public async Task<VideoInfo> ProbeAsync(string file, CancellationToken cancel = default)
    {
        var (output, errors, code) = await RunAsync(Probe, null, null, cancel, "-v", "error", "-print_format", "json", "-show_format", "-show_streams", file);
        if (code != 0)
            throw new InvalidDataException("FFmpeg could not read this video: " + LastLine(errors));

        using var json = JsonDocument.Parse(output);
        var videos = new List<VideoStreamInfo>();
        string? audioCodec = null;
        var (channels, rate) = (0, 0);
        foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            var codec = Text(stream, "codec_name") ?? "unknown";
            switch (Text(stream, "codec_type"))
            {
                case "video":
                    var frameRate = Text(stream, "avg_frame_rate") is { } average && !average.StartsWith('0') ? average : Text(stream, "r_frame_rate") ?? "0/1";
                    var language = stream.TryGetProperty("tags", out var tags) ? Text(tags, "language") : null;
                    videos.Add(new VideoStreamInfo(stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32(), codec, frameRate, language));
                    break;
                case "audio" when audioCodec == null:
                    audioCodec = codec;
                    channels = stream.TryGetProperty("channels", out var count) ? count.GetInt32() : 0;
                    rate = int.TryParse(Text(stream, "sample_rate"), CultureInfo.InvariantCulture, out var hertz) ? hertz : 0;
                    break;
            }
        }
        if (videos.Count == 0)
            throw new InvalidDataException("This file has no video track.");

        var format = json.RootElement.GetProperty("format");
        var seconds = double.TryParse(Text(format, "duration"), CultureInfo.InvariantCulture, out var length) ? length : 0;
        var head = new byte[256];
        await using (var stream = File.OpenRead(file))
            await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancel);
        return new VideoInfo(Text(format, "format_name") ?? "", seconds, new FileInfo(file).Length, videos, audioCodec, channels, rate, Vp6File.HasMatte(head), Vp6File.HasSound(head));
    }

    /// <summary>
    /// Writes an H.264 and AAC MP4. It always reads from the start: the game's files decode cleanly that way but give
    /// errors when entered at a seek point.
    /// </summary>
    /// <param name="videoIndex">Which video track to use.</param>
    /// <param name="maxWidth">Shrinks to this width if wider. Null keeps full size and uses better quality.</param>
    public async Task MakeMp4Async(string source, string output, int videoIndex, bool withAudio, int? maxWidth, double seconds, Action<double>? progress = null, CancellationToken cancel = default)
    {
        await RequireEncodersAsync(cancel, "libx264", "aac");
        var temporary = Path.Combine(Path.GetDirectoryName(output)!, $"{Guid.NewGuid():N}.partial.mp4");
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-y", "-i", source, "-map", $"0:v:{videoIndex}" };
        if (withAudio)
            arguments.AddRange(["-map", "0:a:0?", "-c:a", "aac", "-ac", "2", "-b:a", "160k"]);
        if (maxWidth is { } width)
            arguments.AddRange(["-vf", $"scale='min({width.ToString(CultureInfo.InvariantCulture)},iw)':-2"]);
        arguments.AddRange(["-c:v", "libx264", "-preset", maxWidth == null ? "veryfast" : "ultrafast", "-crf", maxWidth == null ? "18" : "23",
            "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-progress", "pipe:1", temporary]);
        try
        {
            await RunToFileAsync(temporary, seconds, progress, cancel, arguments);
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Encodes any video to match a game Matroska file: VP8 and Vorbis in WebM, the same size, rate, tracks and
    /// channels. A file with a matte gets one from the source's transparency, or solid white if it has none.
    /// The result is read back and refused if it does not match.
    /// </summary>
    public async Task<GameVideoResult> ConvertForGameAsync(string source, VideoInfo target, Action<double>? progress = null, CancellationToken cancel = default)
    {
        if (!target.IsMatroska)
            throw new NotSupportedException("Only the game's .mkv videos can be encoded by FFmpeg.");
        await RequireEncodersAsync(cancel, "libvpx", "libvorbis");

        var input = await ProbeAsync(source, cancel);
        var (width, height) = (target.Picture.Width, target.Picture.Height);
        var fit = $"[0:v:0]fps={target.Picture.Rate},scale={width}:{height}:force_original_aspect_ratio=decrease,format=rgba,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black@0";
        var graph = target.Videos.Count > 1
            ? fit + $",split[c][a];[c]format=yuv420p[v];[a]alphaextract,format=yuv420p,{MatteFullRange}[m]"
            : fit + ",format=yuv420p[v]";

        var folder = Path.Combine(Path.GetTempPath(), "Blueline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var output = Path.Combine(folder, "out.mkv");
            var addSilence = target.AudioCodec != null && input.AudioCodec == null;
            var arguments = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-y", "-i", source };
            if (addSilence)
                arguments.AddRange(["-f", "lavfi", "-t", input.Seconds.ToString("F3", CultureInfo.InvariantCulture), "-i", $"anullsrc=sample_rate={Number(target.AudioRate)}"]);

            arguments.AddRange(["-filter_complex", graph, "-map", "[v]"]);
            if (target.Videos.Count > 1)
                arguments.AddRange(["-map", "[m]"]);
            if (target.AudioCodec != null)
                arguments.AddRange(["-map", addSilence ? "1:a:0" : "0:a:0", "-c:a", "libvorbis", "-q:a", "5", "-ac", Number(target.AudioChannels), "-ar", Number(target.AudioRate)]);
            else
                arguments.Add("-an");

            var totalBits = target.Seconds > 0 ? target.Bytes * 8 / target.Seconds : MinimumVideoBits;
            var overhead = target.AudioChannels * AudioBitsPerChannel + (target.Videos.Count > 1 ? MatteBitsPerSecond : 0);
            var videoBits = Math.Max(MinimumVideoBits, (long)(totalBits * VideoShareOfBits) - overhead);
            arguments.AddRange(["-c:v", "libvpx", "-deadline", "good", "-cpu-used", "4", "-auto-alt-ref", "0", "-g", Number(GameKeyFrameInterval),
                "-threads", Number(Math.Clamp(Environment.ProcessorCount, 1, 16)), "-b:v:0", Number(videoBits)]);
            if (target.Videos.Count > 1)
                arguments.AddRange(["-b:v:1", Number(MatteBitsPerSecond)]);
            for (var i = 0; i < target.Videos.Count; i++)
                arguments.AddRange([$"-metadata:s:v:{Number(i)}", $"language={target.Videos[i].Language ?? "eng"}"]);
            if (target.AudioCodec != null)
                arguments.AddRange(["-metadata:s:a:0", "language=eng"]);
            arguments.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-f", "webm",
                "-cluster_time_limit", ClusterMilliseconds, "-cluster_size_limit", ClusterBytes, "-progress", "pipe:1", output]);

            await RunToFileAsync(output, input.Seconds, progress, cancel, arguments);
            var made = await ProbeAsync(output, cancel);
            Validate(made, target);
            return new GameVideoResult(await File.ReadAllBytesAsync(output, cancel), input.Seconds, target.Seconds, addSilence);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void Validate(VideoInfo made, VideoInfo target)
    {
        var sameVideo = made.Videos.Count == target.Videos.Count
            && made.Videos.Zip(target.Videos).All(p => p.First.Width == p.Second.Width && p.First.Height == p.Second.Height && p.First.Codec == p.Second.Codec);
        if (!sameVideo || made.AudioCodec != target.AudioCodec || made.AudioChannels != target.AudioChannels)
            throw new InvalidDataException($"The encoded video does not match the game's. Made: {made.Summary}. Needed: {target.Summary}.");
    }

    private async Task RunToFileAsync(string output, double seconds, Action<double>? progress, CancellationToken cancel, List<string> arguments)
    {
        void OnLine(string line)
        {
            if (seconds > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                progress?.Invoke(Math.Clamp(microseconds / 1_000_000.0 / seconds, 0, 1));
        }

        var (_, errors, code) = await RunAsync(Executable, OnLine, null, cancel, [.. arguments]);
        if (code != 0 || !File.Exists(output))
            throw new InvalidOperationException("FFmpeg could not convert this video: " + LastLine(errors));
    }

    private async Task RequireEncodersAsync(CancellationToken cancel, params string[] names)
    {
        var (output, _, _) = await RunAsync(Executable, null, null, cancel, "-hide_banner", "-encoders");
        foreach (var name in names)
            if (!output.Contains($" {name} ", StringComparison.Ordinal))
                throw new NotSupportedException($"This FFmpeg has no {name} encoder. A full build of FFmpeg has it.");
    }

    private static async Task<(string Output, string Errors, int Code)> RunAsync(string program, Action<string>? onLine, string? workingFolder, CancellationToken cancel, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = program,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingFolder ?? "",
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{Path.GetFileName(program)} could not be started.");
        try
        {
            var collected = new StringBuilder();
            var reader = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(cancel) is { } line)
                {
                    collected.AppendLine(line);
                    onLine?.Invoke(line);
                }
            }, cancel);
            var errors = process.StandardError.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel);
            await reader;
            return (collected.ToString(), await errors, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(true);
            throw;
        }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string LastLine(string errors) =>
        errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no details given.";
}
