using System.Security.Cryptography;
using Blueline.Formats.Video;

namespace Blueline.Core;

/// <summary>
/// Previews of game videos as MP4s that Windows can play, kept on disk by content so a video is only converted once.
/// The oldest are removed when the folder grows past its limit.
/// </summary>
public sealed class VideoPreviewCache(FfmpegTool tool, string folder, long maxBytes = VideoPreviewCache.DefaultLimit)
{
    public const long DefaultLimit = 3L << 30;
    private const int PreviewWidth = 1280;

    /// <summary>Bump when the preview settings change, so old previews are not reused.</summary>
    private const string Version = "1";

    public FfmpegTool Tool => tool;

    public async Task<VideoInfo> ProbeAsync(byte[] content, string extension, CancellationToken cancel = default)
    {
        var source = await WriteTemporaryAsync(content, extension, cancel);
        try
        {
            return await tool.ProbeAsync(source, cancel);
        }
        finally
        {
            File.Delete(source);
        }
    }

    /// <summary>The preview's file, making it if needed. A matte has no sound.</summary>
    public async Task<string> GetAsync(byte[] content, string extension, VideoTrack track, Action<double>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var name = $"{Convert.ToHexString(SHA1.HashData(content))}-{track}-{Version}.mp4";
        var preview = Path.Combine(folder, name);
        if (File.Exists(preview))
        {
            File.SetLastWriteTimeUtc(preview, DateTime.UtcNow);
            return preview;
        }

        var vp6Matte = track == VideoTrack.Matte && Vp6File.IsVp6(content);
        var source = await WriteTemporaryAsync(vp6Matte ? Vp6File.MatteAsVideo(content) : content, extension, cancel);
        try
        {
            var info = await tool.ProbeAsync(source, cancel);
            if (track == VideoTrack.Matte && !vp6Matte && info.Videos.Count < 2)
                throw new InvalidDataException("This video has no matte.");
            var videoIndex = track == VideoTrack.Matte && !vp6Matte ? 1 : 0;
            await tool.MakeMp4Async(source, preview, videoIndex, track == VideoTrack.Picture, PreviewWidth, info.Seconds, progress, cancel);
        }
        finally
        {
            File.Delete(source);
        }
        Trim(preview);
        return preview;
    }

    private async Task<string> WriteTemporaryAsync(byte[] content, string extension, CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{Guid.NewGuid():N}.source{extension}");
        await File.WriteAllBytesAsync(file, content, cancel);
        return file;
    }

    private void Trim(string keep)
    {
        var files = new DirectoryInfo(folder).GetFiles("*.mp4").OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var file in files.TakeWhile(_ => total > maxBytes))
        {
            if (file.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still being played. It goes next time.
            }
        }
    }
}
