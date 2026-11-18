using Blueline.Formats.Audio;

namespace Blueline.Core;

public sealed record OpusEncodeResult(byte[] Sample, int Kilobits, double Seconds);

/// <summary>
/// Decodes and encodes EA Opus samples (codec 0xC, the horns and most banks) through the user's FFmpeg and its
/// libopus, by way of <see cref="EaOpus"/>. Needs no sx.exe.
/// </summary>
public sealed class OpusTool(FfmpegTool ffmpeg)
{
    /// <summary>Stepped down until a replacement fits its slot. The stock horns run at about 130 kbit/s.</summary>
    private static readonly int[] BitrateLadder = [160, 128, 112, 96, 80, 64, 56, 48, 40, 32, 24];

    public static bool CanHandle(SoundSample sample) => sample.Codec == EaOpus.Codec;

    public async Task DecodeAsync(byte[] sample, string wavPath, CancellationToken cancel = default)
    {
        var temp = TempFolder();
        try
        {
            var ogg = Path.Combine(temp, "sample.ogg");
            await File.WriteAllBytesAsync(ogg, EaOpus.ToOgg(sample), cancel);
            var wav = Path.Combine(temp, "out.wav");
            await ffmpeg.RunAudioAsync(wav, cancel, "-i", ogg, "-map_metadata", "-1", "-bitexact", "-c:a", "pcm_s16le", wav);
            File.Move(wav, wavPath, overwrite: true);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    /// <summary>
    /// Encodes any audio FFmpeg reads (WAV, MP3, OGG, FLAC...) into an EA Opus chain shaped like <paramref name="donor"/>
    /// (its channel count and header), stepping the bitrate down until it fits <paramref name="maxBytes"/>.
    /// Returns null when even the lowest bitrate is too big.
    /// </summary>
    public async Task<OpusEncodeResult?> EncodeToFitAsync(string input, byte[] donor, int maxBytes, double? maxSeconds = null,
        IProgress<string>? progress = null, CancellationToken cancel = default, string? filter = null)
    {
        var chain = EaOpus.Read(donor);
        var temp = TempFolder();
        try
        {
            foreach (var kilobits in BitrateLadder)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report($"Encoding at {kilobits} kbit/s");
                var ogg = Path.Combine(temp, $"{kilobits}.ogg");
                List<string> arguments = ["-i", input, "-vn", "-ac", chain.Channels.ToString(), "-ar", "48000"];
                if (maxSeconds is { } seconds)
                    arguments.AddRange(["-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                if (filter != null)
                    arguments.AddRange(["-af", filter]);
                arguments.AddRange(["-c:a", "libopus", "-b:a", $"{kilobits}k", "-vbr", "constrained", "-frame_duration", "20",
                    // lowdelay is libopus's CELT-only mode: every stock horn packet is CELT fullband 20 ms (TOC 0xFC).
                    "-application", "lowdelay", "-map_metadata", "-1", "-f", "ogg", ogg]);
                await ffmpeg.RunAudioAsync(ogg, cancel, [.. arguments]);
                var sample = EaOpus.FromOgg(await File.ReadAllBytesAsync(ogg, cancel), chain.Header);
                if (sample.Length <= maxBytes)
                {
                    var made = EaOpus.Read(sample);
                    return new OpusEncodeResult(sample, kilobits, (double)made.TotalSamples / EaOpus.SampleRate);
                }
            }
            return null;
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Blueline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
