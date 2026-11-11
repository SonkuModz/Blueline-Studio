using System.Diagnostics;
using Blueline.Formats.Audio;

namespace Blueline.Core;

public sealed record SxEncodeResult(byte[] Sample, int Quality, int Attempts);

/// <summary>
/// Runs EA's sx.exe, which the user supplies; Blueline neither bundles nor reimplements these codecs.
/// sx resolves its codec DLLs from its own folder, so it runs from there.
/// </summary>
public sealed class SxTool(string executable)
{
    /// <summary>sx's VBR scale runs 0 (smallest) to 100 (best).</summary>
    private static readonly int[] QualityLadder = [90, 75, 60, 45, 30, 15, 5];

    public string Executable { get; } = executable;

    public static string? FlagFor(SoundSample sample) => sample.Codec switch
    {
        0xC => "-eaopus",
        0x9 => "-easpeex",
        0x5 => "-ealayer3",
        _ => null,
    };

    /// <summary>Decodes one sample's block chain to a WAV file at <paramref name="wavPath"/>.</summary>
    public async Task DecodeAsync(byte[] sample, string wavPath, CancellationToken cancel = default)
    {
        var temp = TempFolder();
        try
        {
            var sps = Path.Combine(temp, "sample.sps");
            await File.WriteAllBytesAsync(sps, sample, cancel);
            var produced = Path.Combine(temp, "out.wav");
            await RunAsync(cancel, "-wave", sps, $"-={produced}");
            if (!File.Exists(produced))
                throw new InvalidOperationException("sx.exe did not produce a WAV. This codec may not be decodable by this copy of sx.");
            File.Move(produced, wavPath, overwrite: true);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    public async Task<byte[]> EncodeAsync(string wavPath, string codecFlag, int quality, CancellationToken cancel = default)
    {
        var temp = TempFolder();
        try
        {
            // sx appends to the name it is given: "-=x.sps" writes x.sps.sps and x.sps.sph.
            var requested = Path.Combine(temp, "out.sps");
            await RunAsync(cancel, "-sndplayer", "-fileformatversion1", codecFlag, "-playlocstream", $"-vbr{quality}", wavPath, $"-={requested}");
            var produced = requested + ".sps";
            if (!File.Exists(produced))
                throw new InvalidOperationException("sx.exe did not produce a sample. The WAV may be in a format it cannot read.");
            return await File.ReadAllBytesAsync(produced, cancel);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    /// <summary>Steps down in quality until the sample fits, or returns null if even the lowest does not.</summary>
    public async Task<SxEncodeResult?> EncodeToFitAsync(string wavPath, string codecFlag, int maxBytes,
        IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var attempts = 0;
        foreach (var quality in QualityLadder)
        {
            attempts++;
            progress?.Report($"Encoding at quality {quality}");
            var bytes = await EncodeAsync(wavPath, codecFlag, quality, cancel);
            if (bytes.Length <= maxBytes)
                return new SxEncodeResult(bytes, quality, attempts);
        }
        return null;
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Blueline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task RunAsync(CancellationToken cancel, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Executable,
            WorkingDirectory = Path.GetDirectoryName(Executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("sx.exe could not be started.");
        try
        {
            // sx writes a long progress bar; it blocks if these pipes are not drained.
            var output = process.StandardOutput.ReadToEndAsync(cancel);
            var errors = process.StandardError.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel);
            await Task.WhenAll(output, errors);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }
    }
}
