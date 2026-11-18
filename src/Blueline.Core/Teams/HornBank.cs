using Blueline.Formats.Audio;

namespace Blueline.Core.Teams;

public sealed record HornReplaceResult(byte[] Bank, SoundSample Sample, OpusEncodeResult Encoded);

/// <summary>
/// Edits the goal horn bank (<see cref="GoalHorns.BankPath"/>): 410 samples, nearly all stereo EA Opus at 48 kHz.
/// A replacement goes into the slot of the sample it replaces, so it has to fit that slot's bytes; the bank's own
/// index (an offset table at its end) is left as it is. Which sample a horn number plays is not decoded from the
/// patch yet; <see cref="LabelAsync"/> makes a bank where every sample says its own number, to find out by ear.
/// </summary>
public static class HornBank
{
    /// <summary>Speech is far quieter than a horn; this brings it near a horn's level so it carries over the crowd.</summary>
    private const string LabelLoudness = "dynaudnorm=p=0.95:m=30,volume=6dB,alimiter=limit=0.95";

    public static IReadOnlyList<SoundSample> Samples(byte[] bank) =>
        SoundBank.Scan(bank.Length, (offset, buffer) =>
        {
            var length = (int)Math.Min(buffer.Length, bank.Length - offset);
            bank.AsSpan((int)offset, length).CopyTo(buffer);
            return length;
        });

    public static byte[] Read(byte[] bank, SoundSample sample) => bank.AsSpan((int)sample.Offset, sample.Length).ToArray();

    /// <summary>Encodes <paramref name="audioFile"/> into sample <paramref name="index"/>'s slot and returns the new bank.</summary>
    public static async Task<HornReplaceResult> ReplaceAsync(OpusTool tool, byte[] bank, int index, string audioFile,
        IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var samples = Samples(bank);
        if (index < 0 || index >= samples.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"The bank has samples 0 to {samples.Count - 1}.");
        var sample = samples[index];
        if (!OpusTool.CanHandle(sample))
            throw new InvalidOperationException($"Sample {index} is {sample.CodecName}; only EA Opus samples can be replaced here.");
        var encoded = await tool.EncodeToFitAsync(audioFile, Read(bank, sample), sample.Length, null, progress, cancel)
            ?? throw new InvalidOperationException($"Even at the lowest bitrate this audio is bigger than sample {index}'s slot of {sample.Length:N0} bytes ({sample.Seconds:F1} s at the stock quality). Use a shorter clip or a longer horn's slot.");
        return new HornReplaceResult(SoundBank.ReplaceSample(bank, sample, encoded.Sample), sample, encoded);
    }

    /// <summary>
    /// Puts a spoken label into every EA Opus sample. <paramref name="label"/> writes a WAV that says the sample's
    /// number to the given path. A test bank only: install it, then score or preview horns to hear which sample plays.
    /// </summary>
    public static async Task<byte[]> LabelAsync(OpusTool tool, byte[] bank, Func<int, string, Task> label, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancel = default)
    {
        var samples = Samples(bank);
        var output = (byte[])bank.Clone();
        var temp = Path.Combine(Path.GetTempPath(), "Blueline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            for (var i = 0; i < samples.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report((i, samples.Count));
                var sample = samples[i];
                if (!OpusTool.CanHandle(sample))
                    continue;
                var wav = Path.Combine(temp, $"{i}.wav");
                await label(i, wav);
                var encoded = await tool.EncodeToFitAsync(wav, Read(bank, sample), sample.Length, sample.Seconds, null, cancel, LabelLoudness)
                    ?? throw new InvalidOperationException($"The label for sample {i} does not fit its {sample.Length:N0} bytes.");
                var slot = output.AsSpan((int)sample.Offset, sample.Length);
                slot.Clear();
                encoded.Sample.CopyTo(slot);
            }
            progress?.Report((samples.Count, samples.Count));
            return output;
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }
}
