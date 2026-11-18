using System.Net;
using System.Text.RegularExpressions;

namespace Blueline.Core.Teams;

/// <summary>One colour as the site shows it: name, the RGB of its swatch, the colour code line (PANTONE ...), and a note such as Supporting or Metallic.</summary>
public sealed record TruColorSwatch(string Name, int Rgb, string? Code, string? Note, bool Estimated)
{
    public string Hex => GearColours.Hex(Rgb);
    public bool IsSupporting => Note?.Contains("Supporting", StringComparison.OrdinalIgnoreCase) == true;
    public override string ToString() => $"{Name} #{Hex}{(Note != null ? $" ({Note})" : "")}";
}

/// <summary>A labelled row of swatches, such as PRIMARY COLORS or GRADIENT USAGE.</summary>
public sealed record TruColorGroup(string Label, IReadOnlyList<TruColorSwatch> Swatches);

/// <summary>The colours a team used over some seasons. Seasons are start years: 2018 is 2018-2019. To is null for "through present".</summary>
public sealed record TruColorEra(string Years, string Summary, int? From, int? To, IReadOnlyList<TruColorGroup> Groups)
{
    /// <summary>The primary row: the first group labelled PRIMARY..., else the first group.</summary>
    public TruColorGroup? Main => Groups.FirstOrDefault(g => g.Label.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase)) ?? Groups.FirstOrDefault();

    /// <summary>The primary row's colours in the site's order: primary first, then secondary, then the rest.</summary>
    public IReadOnlyList<TruColorSwatch> Colours => Main?.Swatches ?? [];

    public bool Covers(int season) => From is { } from && from <= season && (To ?? int.MaxValue) >= season;
    public override string ToString() => $"{Years}: {Summary}";
}

/// <summary>A part of a team's record: FRANCHISE COLORS, REVERSE RETRO UNIFORM COLORS, MASCOT ARTWORK and so on.</summary>
public sealed record TruColorSection(string Name, IReadOnlyList<TruColorEra> Eras);

public sealed record TruColorTeam(string Name, string Years, bool Active, int? From, int? To, IReadOnlyList<TruColorSection> Sections)
{
    public const string FranchiseSection = "FRANCHISE COLORS";

    public TruColorSection? Franchise => Sections.FirstOrDefault(s => s.Name.Equals(FranchiseSection, StringComparison.OrdinalIgnoreCase));

    /// <summary>The newest franchise colours.</summary>
    public TruColorEra? Current => Franchise?.Eras.OrderByDescending(e => e.From ?? int.MinValue).FirstOrDefault();

    /// <summary>The franchise colours worn in a season (start year), or null when the team did not exist then.</summary>
    public TruColorEra? EraFor(int season) => Franchise?.Eras.Where(e => e.Covers(season)).OrderByDescending(e => e.From).FirstOrDefault();

    /// <summary>Title case of the site's capitals, for showing.</summary>
    public string DisplayName => TruColorSite.TitleCase(Name);
    public override string ToString() => $"{DisplayName} ({Years})";
}

/// <summary>A league on the site's hockey index. Url is null for leagues the site lists but has no page for yet.</summary>
public sealed record TruColorLeague(string Name, string Group, string Years, string? Url)
{
    /// <summary>The page's slug, such as american-hockey-league-official-colors-1940-1941-through-present.</summary>
    public string? Key => Url?.TrimEnd('/') is { } u ? u[(u.LastIndexOf('/') + 1)..] : null;
    public override string ToString() => $"{Name} ({Years})";
}

/// <summary>The site answered 403 or 429. Never retried.</summary>
public sealed class TruColorRefusedException(HttpStatusCode code)
    : Exception($"trucolor.net is refusing requests right now ({(int)code}). Try again later.");

/// <summary>
/// Reads team colours from TruColor (trucolor.net), a WordPress site that renders on the server. The hockey index lists the
/// leagues; each league is one page holding every team as nested collapsible blocks (there are no per-team pages):
/// team, then section (FRANCHISE COLORS, ...), then era ("2018-2019 through present: Orange, Navy, ..."), then labelled rows
/// of swatches. robots.txt allows everything (checked 2026-11-17). Polite by design: one request at a time at least 2 s
/// apart, 403/429 never retried, pages cached on disk, and a generic browser user agent with nothing about the user in it.
/// </summary>
public sealed class TruColorSite : IDisposable
{
    public const string Site = "https://www.trucolor.net";
    public const string HockeyIndex = Site + "/team-colors/hockey/";
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(2);

    public static string DefaultCache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blueline", "trucolor");

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _last = DateTime.MinValue;

    /// <param name="cacheFolder">Where pages are kept. Never inside the repository or %APPDATA% in tests.</param>
    /// <param name="maxAge">How old a cached page may be before it is fetched again.</param>
    public TruColorSite(string cacheFolder, HttpMessageHandler? handler = null, TimeSpan? maxAge = null)
    {
        CacheFolder = cacheFolder;
        MaxAge = maxAge ?? TimeSpan.FromDays(30);
        _http = new HttpClient(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,*/*;q=0.8");
    }

    public string CacheFolder { get; }
    public TimeSpan MaxAge { get; }

    /// <summary>Requests made so far (for logs and tests).</summary>
    public int Requests { get; private set; }

    public async Task<IReadOnlyList<TruColorLeague>> ListLeaguesAsync(bool refresh = false, CancellationToken cancel = default) =>
        ParseIndex(await GetPageAsync(HockeyIndex, refresh, cancel));

    public async Task<IReadOnlyList<TruColorTeam>> ListTeamsAsync(TruColorLeague league, bool refresh = false, CancellationToken cancel = default) =>
        ParseLeague(await GetPageAsync(league.Url ?? throw new InvalidOperationException($"The site has no page for {league.Name} yet."), refresh, cancel));

    /// <summary>A page's text, from the cache when it is there and young enough. Written as .part and renamed.</summary>
    public async Task<string> GetPageAsync(string url, bool refresh = false, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(CacheFolder);
        var file = Path.Combine(CacheFolder, CacheName(url));
        if (!refresh && File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MaxAge)
            return await File.ReadAllTextAsync(file, cancel);
        var bytes = await GetBytesAsync(url, cancel);
        var part = file + ".part";
        await File.WriteAllBytesAsync(part, bytes, cancel);
        File.Move(part, file, true);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>When the cached copy of a page was fetched, or null when it is not cached.</summary>
    public DateTime? CachedAt(string url) =>
        File.Exists(Path.Combine(CacheFolder, CacheName(url))) ? File.GetLastWriteTime(Path.Combine(CacheFolder, CacheName(url))) : null;

    public static string CacheName(string url)
    {
        var path = new Uri(url).AbsolutePath.Trim('/');
        var name = path.Length == 0 ? "index" : path.Replace('/', '_');
        return string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".html";
    }

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
                        throw new TruColorRefusedException(response.StatusCode);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync(cancel);
                }
                catch (HttpRequestException e) when (e.StatusCode == null && attempt < 3)
                {
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

    // ---- Parsing

    private static readonly Regex IndexToken = new(
        @"<h5>(?<group>.*?)</h5>|<li>\s*<strong>(?:<a\b[^>]*?href=""(?<url>[^""]+)""[^>]*>)?(?<name>[^<]+)(?:</a>)?</strong>\s*(?<years>\([^)<]*\))?",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.ExplicitCapture);

    /// <summary>The leagues on the hockey index, in its order. A league whose name stands alone (the NHL) is the parent of the linked pages under it.</summary>
    public static IReadOnlyList<TruColorLeague> ParseIndex(string html)
    {
        var start = html.IndexOf("<div class=\"entry clearfix\">", StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidDataException("This is not the TruColor hockey index: its list of leagues is missing.");
        var end = html.IndexOf("</div>", start + 10, StringComparison.Ordinal);
        var body = html[start..(end < 0 ? html.Length : end)];
        var list = new List<TruColorLeague>();
        string group = "", parent = "", parentYears = "";
        foreach (Match m in IndexToken.Matches(body))
        {
            if (m.Groups["group"].Success)
            {
                group = TitleCase(Text(m.Groups["group"].Value));
                continue;
            }
            var name = Text(m.Groups["name"].Value).Replace(" Official Colors", "", StringComparison.OrdinalIgnoreCase).Trim();
            var years = m.Groups["years"].Success ? m.Groups["years"].Value.Trim('(', ')', ' ') : "";
            var url = m.Groups["url"].Success ? WebUtility.HtmlDecode(m.Groups["url"].Value) : null;
            if (url == null)
            {
                (parent, parentYears) = (name, years);
                // A parent with linked pages under it is not a league itself; one with none is a league the site has no page for.
                if (!body.AsSpan(m.Index + m.Length).TrimStart().StartsWith("<ul>"))
                    list.Add(new TruColorLeague(name, group, years, null));
                continue;
            }
            if (years.Length == 0 && parent.Length > 0)
                list.Add(new TruColorLeague($"{parent}: {name}", group, parentYears, url));
            else
                list.Add(new TruColorLeague(name, group, years, url));
        }
        return list;
    }

    private static readonly Regex LeagueToken = new(
        @"<h5>(?<h5>.*?)</h5>" +
        @"|class=""collapseomatic[^""]*""\s+id=""[^""]*""\s+title=""(?<title>[^""]*)""" +
        @"|<span style=""font-size:\s*9pt;"">(?<label>.*?)</span>" +
        @"|class=""acb-box""\s+style=""background-color:\s*#(?<rgb>[0-9A-Fa-f]{6})" +
        @"|<div style=""line-height:\s*0\.425cm;"">(?<line>.*?)</div>" +
        @"|<div style=""padding-left:[^""]*"">(?<note>.*?)</div>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.ExplicitCapture);

    private static readonly Regex TeamTitle = new(@"^\s*(?:&#\d+;\s*|[^\w<]\s*)?<strong>(?<name>.*?)</strong>\s*(?<years>.*)$", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.ExplicitCapture);
    private static readonly Regex EraTitle = new(@"^(?<years>[^<]*?):\s*<strong>(?<summary>.*?)</strong>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.ExplicitCapture);
    private static readonly Regex Seasons = new(@"(\d{4})\s*-\s*\d{2,4}", RegexOptions.Compiled);

    /// <summary>Every team on a league page, active ones first as the page has them.</summary>
    public static IReadOnlyList<TruColorTeam> ParseLeague(string html)
    {
        var teams = new List<TruColorTeam>();
        // Built as mutable lists, then frozen into records.
        List<(string Name, List<(string Years, string Summary, List<(string Label, List<TruColorSwatch> Swatches)> Groups)> Eras)>? sections = null;
        List<(string Label, List<TruColorSwatch> Swatches)>? groups = null;
        List<TruColorSwatch>? swatches = null;
        (string Name, string Years, bool Active)? team = null;
        var historical = false;
        int? rgb = null;
        var lines = new List<string>();
        string? note = null;

        void EndSwatch()
        {
            if (rgb is not { } value || swatches == null)
            {
                (rgb, note) = (null, null);
                lines.Clear();
                return;
            }
            var name = lines.Count > 0 ? lines[0] : "";
            var code = lines.Skip(1).FirstOrDefault(l => !l.StartsWith('#') && !Regex.IsMatch(l, @"^R\d") && !Regex.IsMatch(l, @"^C\d"));
            var estimated = lines.Any(l => l.Contains('*')) || note?.Contains('*') == true;
            swatches.Add(new TruColorSwatch(TitleCase(name.Replace("*", "").Trim()), value, code?.Replace("*", "").Trim(), note, estimated));
            (rgb, note) = (null, null);
            lines.Clear();
        }

        void EndTeam()
        {
            EndSwatch();
            if (team is not { } t || sections == null)
                return;
            var (from, to) = ParseSeasons(t.Years);
            teams.Add(new TruColorTeam(t.Name, t.Years, t.Active, from, to, sections.Select(s => new TruColorSection(s.Name,
                s.Eras.Select(e =>
                {
                    var (ef, et) = ParseSeasons(e.Years);
                    return new TruColorEra(e.Years, e.Summary, ef, et, e.Groups.Where(g => g.Swatches.Count > 0).Select(g => new TruColorGroup(g.Label, g.Swatches)).ToList());
                }).ToList())).ToList()));
            team = null;
        }

        foreach (Match m in LeagueToken.Matches(html))
        {
            if (m.Groups["h5"].Success)
            {
                var heading = Text(m.Groups["h5"].Value);
                if (heading.Contains("FRANCHISE RECORDS", StringComparison.OrdinalIgnoreCase))
                {
                    EndTeam();
                    historical = !heading.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase);
                }
                continue;
            }
            if (m.Groups["title"].Success)
            {
                EndSwatch();
                // The title attribute holds raw markup: <strong>NAME</strong> (years).
                var title = m.Groups["title"].Value;
                if (TeamTitle.Match(title) is { Success: true } t)
                {
                    EndTeam();
                    var years = Text(t.Groups["years"].Value).Trim('(', ')', ' ');
                    team = (Text(t.Groups["name"].Value), years, !historical);
                    sections = [];
                    groups = null;
                    swatches = null;
                }
                else if (EraTitle.Match(title) is { Success: true } e && sections is { Count: > 0 })
                {
                    groups = [];
                    swatches = null;
                    sections[^1].Eras.Add((Text(e.Groups["years"].Value), Text(e.Groups["summary"].Value), groups));
                }
                else if (sections != null)
                {
                    sections.Add((Text(title), []));
                    groups = null;
                    swatches = null;
                }
                continue;
            }
            if (m.Groups["label"].Success)
            {
                EndSwatch();
                if (groups != null)
                {
                    swatches = [];
                    groups.Add((Text(m.Groups["label"].Value), swatches));
                }
                continue;
            }
            if (m.Groups["rgb"].Success)
            {
                EndSwatch();
                if (groups != null && swatches == null)
                {
                    // Swatches with no label of their own.
                    swatches = [];
                    groups.Add(("COLORS", swatches));
                }
                rgb = Convert.ToInt32(m.Groups["rgb"].Value, 16);
                continue;
            }
            if (m.Groups["line"].Success && rgb != null)
            {
                lines.Add(Text(m.Groups["line"].Value));
                continue;
            }
            if (m.Groups["note"].Success && rgb != null)
                note = Text(m.Groups["note"].Value) is { Length: > 0 } n ? n : null;
        }
        EndTeam();
        return teams;
    }

    /// <summary>"2018-2019 through present" is (2018, null); "1999-2000 through 2022-2023" is (1999, 2022); "2013-2014" is (2013, 2013).</summary>
    public static (int? From, int? To) ParseSeasons(string years)
    {
        var found = Seasons.Matches(years).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        if (found.Count == 0)
            return (null, null);
        return (found[0], years.Contains("present", StringComparison.OrdinalIgnoreCase) ? null : found[^1]);
    }

    private static string Text(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")), @"\s+", " ").Trim();

    /// <summary>"ST. LOUIS BLUES" as "St. Louis Blues"; keeps short all-capital words such as HC or 67'S readable.</summary>
    public static string TitleCase(string text)
    {
        var lower = text.ToLowerInvariant();
        var chars = lower.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (char.IsLetter(chars[i]) && (i == 0 || !char.IsLetter(chars[i - 1]) && chars[i - 1] is not ('\'' or '’')))
                chars[i] = char.ToUpperInvariant(chars[i]);
        var result = new string(chars);
        return Regex.Replace(result, @"\b(Hc|Ec|Sc|Usa|Ahl|Echl|Nhl|Ohl|Whl|Qmjhl|Sphl|Ii)\b", m => m.Value.ToUpperInvariant());
    }
}
