using Blueline.Core.Teams;
using Blueline.Formats.Textures;
using BCnEncoder.Encoder;

namespace Blueline.Core.Rinks;

public sealed record FullIceResult(TeamInfo Team, string SlotPath, string SourceFile, string? RinkName, RinkMeasure? Measure,
    RgbaImage Source, RgbaImage Fitted, byte[] Gnf, RgbaImage Preview, string? Note = null)
{
    public bool IsPlain => Measure == null && RinkName == null && SourceFile.StartsWith("plain rink", StringComparison.Ordinal);
}

/// <summary>Fetches (or takes) a team's rink picture and turns it into its full-ice texture.</summary>
public static class FullIceJob
{
    /// <summary>Reads any picture: PNG and DDS directly, anything else (JPEG, WebP, AVIF) through FFmpeg.</summary>
    public static async Task<RgbaImage> LoadPictureAsync(string file, FfmpegTool? ffmpeg, CancellationToken cancel = default)
    {
        var head = new byte[8];
        using (var stream = File.OpenRead(file))
            _ = await stream.ReadAsync(head, cancel);
        if (PngFile.IsPng(head) || DdsFile.IsDds(head))
            return ImageLoader.Load(file);
        if (ffmpeg == null)
            throw new InvalidOperationException("FFmpeg is needed to read JPEG, WebP and AVIF pictures. Install it (winget install Gyan.FFmpeg) or choose it in Settings.");
        var png = Path.Combine(Path.GetTempPath(), "Blueline", "pictures", Guid.NewGuid().ToString("N") + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        try
        {
            await ffmpeg.ImageToPngAsync(file, png, cancel);
            return PngFile.Read(await File.ReadAllBytesAsync(png, cancel));
        }
        finally
        {
            File.Delete(png);
        }
    }

    /// <summary>Downloads (or takes from the cache) a team's current rink layout. Returns the file and the rink's name.</summary>
    public static async Task<(string File, FaceoffLayout Layout)> FetchAsync(FaceoffNet site, TeamInfo team, IProgress<string>? log = null, CancellationToken cancel = default)
    {
        var league = FaceoffTeams.LeagueFor(team) ?? throw new InvalidOperationException($"TheFaceoff.net has no list for the {team.League} league.");
        var names = FaceoffTeams.NamesFor(team);
        log?.Report($"Looking for {string.Join(" / ", names)} on {FaceoffNet.LeagueUrl(league)}");
        var rink = await site.FindTeamRinkAsync(league, names, cancel: cancel)
            ?? throw new KeyNotFoundException($"No rink titled \"{names[0]} YEAR\" in TheFaceoff.net's {league.ToUpperInvariant()} list. Paste the rink's link or use a picture file.");
        log?.Report($"Found {rink.Title}: {rink.PageUrl}");
        return await FetchRinkAsync(site, rink, log, cancel);
    }

    public static async Task<(string File, FaceoffLayout Layout)> FetchRinkAsync(FaceoffNet site, FaceoffRink rink, IProgress<string>? log = null, CancellationToken cancel = default)
    {
        var layout = await site.GetLayoutAsync(rink, cancel)
            ?? throw new InvalidDataException($"{rink.Title}'s page has no top-down rink layout (no 5000x2236 picture).");
        log?.Report($"Layout {layout.Image.Width}x{layout.Image.Height}: {layout.Image.Url}");
        var file = await site.DownloadAsync(layout.Image, cancel);
        return (file, layout);
    }

    /// <summary>Fits, encodes and decodes again for the preview, so the preview shows what the game will read.</summary>
    public static FullIceResult Build(TeamInfo team, RgbaImage source, string sourceFile, string? rinkName, FullIceFit fit, byte[] stockSlot,
        CompressionQuality quality = CompressionQuality.Balanced)
    {
        var measure = fit == FullIceFit.Layout ? FullIce.Measure(source) : null;
        var stock = GnfTexture.Parse(stockSlot);
        var fitted = FullIce.Fit(source, fit, stock.Info.Width, measure);
        var gnf = FullIce.Encode(stockSlot, fitted, quality);
        var decoded = GameTexture.TryOpen(gnf)!.DecodeMip(0);
        return new FullIceResult(team, FullIce.SlotPath(team.ArtId), sourceFile, rinkName, measure, source, fitted, gnf, FullIce.Preview(source, decoded));
    }

    /// <summary>
    /// A team's rink from TheFaceoff.net when it has a usable layout, otherwise the plain rink with its logo. A refusal
    /// from the site is passed on (the caller should stop asking it); every other miss falls back with a note.
    /// </summary>
    public static async Task<FullIceResult> AutoAsync(FaceoffNet site, TeamInfo team, Func<string, byte[]?> read, byte[] stockSlot, FfmpegTool? ffmpeg,
        IProgress<string>? log = null, CancellationToken cancel = default)
    {
        string? why;
        try
        {
            var (file, layout) = await FetchAsync(site, team, log, cancel);
            var source = await LoadPictureAsync(file, ffmpeg, cancel);
            var built = Build(team, source, file, layout.RinkName, FullIceFit.Layout, stockSlot);
            if (built.Measure is { Found: true })
                return built;
            why = $"{layout.RinkName}'s picture does not look like a rink layout ({built.Measure?.How})";
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidDataException or InvalidOperationException or HttpRequestException)
        {
            why = e.Message;
        }
        log?.Report($"{team.Name}: {why}. Using the plain rink.");
        return BuildPlain(team, read, stockSlot) with { Note = why };
    }

    /// <summary>
    /// The plain rink for a team with no picture: standard markings, the team's centre logo as the game shows it now
    /// (<paramref name="read"/> reads the game with any mods folder, never the project being written), and its red line.
    /// </summary>
    public static FullIceResult BuildPlain(TeamInfo team, Func<string, byte[]?> read, byte[] stockSlot, CompressionQuality quality = CompressionQuality.Balanced, bool teamArt = true)
    {
        var slot = FullIce.SlotPath(team.ArtId);
        var logoBytes = teamArt ? read(slot) : null;
        var logo = logoBytes != null ? GameTexture.TryOpen(logoBytes)?.DecodeMip(0) : null;
        var lineBytes = teamArt && team.CenterLine > 0 ? read(FullIce.CentreLinePath(team.CenterLine)) : null;
        var line = lineBytes != null ? GameTexture.TryOpen(lineBytes)?.DecodeMip(0) : null;
        var stock = GnfTexture.Parse(stockSlot);
        var drawn = PlainRink.Draw(logo, line, stock.Info.Width);
        var gnf = FullIce.Encode(stockSlot, drawn, quality);
        var decoded = GameTexture.TryOpen(gnf)!.DecodeMip(0);
        var how = $"plain rink, logo {(logo != null ? "from " + slot.Split('/')[^1] : "none")}, red line {(line != null ? "ice_cline_" + team.CenterLine : "plain")}";
        return new FullIceResult(team, slot, how, null, null, drawn, drawn, gnf, FullIce.Preview(null, decoded));
    }

    /// <summary>
    /// One rink for many teams: the game has no fallback (each arena reads its own team's slot), so the default goes in
    /// every slot, but it is encoded once per distinct slot format (nearly all are the same 1024 square) and the bytes
    /// shared, which takes seconds instead of minutes. Returns each slot path with its texture.
    /// </summary>
    public static List<(string Slot, byte[] Gnf)> Spread(RgbaImage fitted, IEnumerable<(string Slot, byte[] StockSlot)> slots, CompressionQuality quality = CompressionQuality.Balanced)
    {
        var encoded = new Dictionary<GnfInfo, byte[]>();
        var result = new List<(string, byte[])>();
        foreach (var (slot, stock) in slots)
        {
            var info = GnfTexture.Parse(stock).Info;
            if (!encoded.TryGetValue(info, out var gnf))
                encoded[info] = gnf = FullIce.Encode(stock, fitted, quality);
            result.Add((slot, gnf));
        }
        return result;
    }
}
