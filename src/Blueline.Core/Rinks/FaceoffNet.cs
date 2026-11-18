using System.Net;
using System.Text.RegularExpressions;

namespace Blueline.Core.Rinks;

/// <summary>One rink in a TheFaceoff.net league list, such as "Toronto Maple Leafs 2027".</summary>
public sealed record FaceoffRink(string Title, string PageUrl, string? ThumbnailId);

/// <summary>A picture embedded in a rink page, with the size the page states for it.</summary>
public sealed record FaceoffImage(string MediaId, int Width, int Height, string? Link = null)
{
    public string Url => $"https://static.wixstatic.com/media/{MediaId}";
    public string ThumbnailUrl(int size = 256) => $"{Url}/v1/fill/w_{size},h_{size},al_c,q_85/thumb.jpg";
    public bool IsLayoutShape => Width >= 2000 && Math.Abs((double)Width / Height - FaceoffNet.LayoutAspect) <= 0.02;
}

public sealed record FaceoffLayout(FaceoffRink Rink, string RinkName, FaceoffImage Image);

/// <summary>The site answered 403 or 429: it wants us to stop for a while. Never retried.</summary>
public sealed class FaceoffRefusedException(HttpStatusCode code)
    : Exception($"TheFaceoff.net is refusing requests right now ({(int)code}). Try again later, or download the rink picture in a browser and use that file.");

/// <summary>
/// Reads TheFaceoff.net (a Wix site that renders on the server) with plain GETs: a league's rink list, a rink page's
/// embedded image list, and the full-size original from the Wix CDN. See references/faceoff-net-images.md.
/// Polite by design: one request at a time at least 1.5 s apart, 403/429 never retried, everything cached, and a
/// generic browser user agent with nothing about the user in it. Pictures stay local; they are for personal use
/// with credit to the site, and are never packed into anything Blueline ships.
/// </summary>
public sealed class FaceoffNet : IDisposable
{
    public const double LayoutAspect = 5000.0 / 2236.0;
    public const string Site = "https://www.thefaceoff.net";
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(1.5);

    private static readonly Regex ListEntry = new(
        "title=\"([^\"]+)\"><a data-testid=\"linkElement\" href=\"(https://www\\.thefaceoff\\.net/center-ice/[0-9a-f-]+)\"(.{0,1500}?)(84911f_[0-9a-f]{32}~mv2\\.\\w+)",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex PagerId = new("(comp-[a-z0-9]+)_page=2", RegexOptions.Compiled);
    // An image entry in the page's data. The reference doc (2026) saw "width","height","uri"; in November 2026 the
    // site writes "height","width",...,"uri" and adds the image's link. Both orders are read.
    private static readonly Regex PageImage = new(
        "\\{\"(?<a>width|height)\":(?<av>\\d+),\"(?<b>width|height)\":(?<bv>\\d+),(?<rest>[^{}]{0,300}?)\"uri\":\"(?<id>84911f_[0-9a-f]{32}~mv2\\.\\w+)\"(?<tail>[^{}]{0,200}?\\{\"href\":\"(?<href>[^\"]+)\")?",
        RegexOptions.Compiled);
    private static readonly Regex PageTitle = new("<title>([^<]*)</title>", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _last = DateTime.MinValue;
    private readonly Dictionary<string, List<FaceoffRink>> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pagesRead = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _pagers = new(StringComparer.Ordinal);

    /// <param name="cacheFolder">Where downloaded pictures are kept. Never inside the repository or %APPDATA% in tests.</param>
    public FaceoffNet(string cacheFolder, HttpMessageHandler? handler = null)
    {
        CacheFolder = cacheFolder;
        _http = new HttpClient(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,image/*,*/*;q=0.8");
    }

    public string CacheFolder { get; }

    /// <summary>Requests made so far (for logs and tests).</summary>
    public int Requests { get; private set; }

    public static string LeagueUrl(string league) => $"{Site}/ice-{league.ToLowerInvariant()}";

    /// <summary>The newest rinks of a league, reading more list pages only as needed (8 rinks per page).</summary>
    public async Task<IReadOnlyList<FaceoffRink>> ListAsync(string league, int pages, CancellationToken cancel = default)
    {
        league = league.ToLowerInvariant();
        if (!_lists.TryGetValue(league, out var list))
            _lists[league] = list = [];
        while (_pagesRead.GetValueOrDefault(league) < pages)
        {
            var page = _pagesRead.GetValueOrDefault(league) + 1;
            string url;
            if (page == 1)
                url = LeagueUrl(league);
            else if (_pagers.GetValueOrDefault(league) is { } pager)
                url = $"{LeagueUrl(league)}?{pager}_page={page}";
            else
                break; // no further pages
            var html = await GetTextAsync(url, cancel);
            if (page == 1)
                _pagers[league] = PagerId.Match(html) is { Success: true } m ? m.Groups[1].Value : null;
            var before = list.Count;
            foreach (Match m in ListEntry.Matches(html))
            {
                var rink = new FaceoffRink(WebUtility.HtmlDecode(m.Groups[1].Value).Trim(), m.Groups[2].Value, m.Groups[4].Value);
                if (!list.Any(r => r.PageUrl == rink.PageUrl))
                    list.Add(rink);
            }
            _pagesRead[league] = page;
            if (list.Count == before || !html.Contains($"_page={page + 1}", StringComparison.Ordinal))
            {
                _pagers[league] = null; // the list ends here
                break;
            }
        }
        return list;
    }

    /// <summary>
    /// A team's current game-night rink: the newest entry titled "NAME YEAR" for any of the names, skipping practice
    /// rinks, outdoor games and other specials. Reads up to <paramref name="maxPages"/> list pages.
    /// </summary>
    public async Task<FaceoffRink?> FindTeamRinkAsync(string league, IReadOnlyList<string> names, int maxPages = 6, CancellationToken cancel = default)
    {
        for (var pages = 1; pages <= maxPages; pages++)
        {
            var list = await ListAsync(league, pages, cancel);
            if (MatchTeam(list, names) is { } found)
                return found;
            if (_pagers.GetValueOrDefault(league.ToLowerInvariant()) == null && _pagesRead.GetValueOrDefault(league.ToLowerInvariant()) < pages)
                break;
        }
        return null;
    }

    /// <summary>
    /// The first rink whose title is exactly one of the names followed by a year, and maybe "(1st)"/"(2nd)" for a club
    /// that changed rinks during a season (the list is newest first, so the later one wins).
    /// </summary>
    public static FaceoffRink? MatchTeam(IEnumerable<FaceoffRink> rinks, IReadOnlyList<string> names)
    {
        var wanted = names.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        foreach (var rink in rinks)
        {
            var m = Regex.Match(rink.Title, @"^(.*?)\s+(\d{4})(?:[-/]\d{2,4})?(?:\s*\((?:1st|2nd|3rd|\d+th)\))?$");
            if (m.Success && wanted.Contains(Normalize(m.Groups[1].Value)))
                return rink;
        }
        return null;
    }

    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var chars = decomposed.Where(c => char.IsLetterOrDigit(c)).Select(char.ToLowerInvariant);
        return new string(chars.ToArray());
    }

    /// <summary>Reads a rink page and picks its top-down layout: the first picture with the 5000x2236 shape.</summary>
    public async Task<FaceoffLayout?> GetLayoutAsync(FaceoffRink rink, CancellationToken cancel = default)
    {
        var html = await GetTextAsync(rink.PageUrl, cancel);
        var images = ParseImages(html);
        // The page also shows other years' layouts linking to their own pages; prefer the one linking to this page.
        var own = images.Where(i => i.IsLayoutShape && i.Link != null && SamePage(i.Link, rink.PageUrl)).ToList();
        var name = PageTitle.Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups[1].Value) : rink.Title;
        var cut = name.IndexOf(" | ", StringComparison.Ordinal);
        if (cut > 0)
            name = name[..cut];
        name = name.Replace("Center Ice and Rink Layout", "", StringComparison.OrdinalIgnoreCase).Trim(' ', '-', '|');
        var layout = own.FirstOrDefault() ?? images.FirstOrDefault(i => i.IsLayoutShape && i.Link == null) ?? images.FirstOrDefault(i => i.IsLayoutShape);
        return layout != null ? new FaceoffLayout(rink, name.Length > 0 ? name : rink.Title, layout) : null;
    }

    private static bool SamePage(string link, string page) =>
        link.Replace("\\/", "/", StringComparison.Ordinal).TrimEnd('/').Equals(page.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<FaceoffImage> ParseImages(string html) =>
        PageImage.Matches(html).Select(m =>
        {
            var a = int.Parse(m.Groups["av"].Value);
            var b = int.Parse(m.Groups["bv"].Value);
            var (width, height) = m.Groups["a"].Value == "width" ? (a, b) : (b, a);
            return new FaceoffImage(m.Groups["id"].Value, width, height, m.Groups["href"].Success ? m.Groups["href"].Value.Replace("\\/", "/", StringComparison.Ordinal) : null);
        }).DistinctBy(i => i.MediaId).ToList();

    /// <summary>The full-size original, from the cache when it is already there. Written as .part and renamed.</summary>
    public async Task<string> DownloadAsync(FaceoffImage image, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(CacheFolder);
        var file = Path.Combine(CacheFolder, image.MediaId.Replace('~', '_'));
        if (File.Exists(file))
            return file;
        var bytes = await GetBytesAsync(image.Url, cancel);
        var part = file + ".part";
        await File.WriteAllBytesAsync(part, bytes, cancel);
        File.Move(part, file, true);
        return file;
    }

    private async Task<string> GetTextAsync(string url, CancellationToken cancel) =>
        System.Text.Encoding.UTF8.GetString(await GetBytesAsync(url, cancel));

    /// <summary>The one gate every request goes through.</summary>
    private async Task<byte[]> GetBytesAsync(string url, CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var wait = _last + Gap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancel);
                _last = DateTime.UtcNow;
                Requests++;
                try
                {
                    using var response = await _http.GetAsync(url, cancel);
                    _last = DateTime.UtcNow;
                    if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                        throw new FaceoffRefusedException(response.StatusCode);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync(cancel);
                }
                catch (HttpRequestException e) when (e.StatusCode == null && attempt < 3)
                {
                    // A dropped connection, not an answer: wait a little longer and try again.
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancel);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
