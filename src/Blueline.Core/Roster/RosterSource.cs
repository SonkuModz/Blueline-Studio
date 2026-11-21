using System.Text;
using Blueline.Core.Teams;
using Blueline.Formats.Tdb;
using Blueline.Formats.Textures;

namespace Blueline.Core.Roster;

/// <summary>Opens the roster the editor works on: the project's nhl_game.db (else the game's), or a roster save to compare with.</summary>
public static class RosterSource
{
    public const string PortraitFolder = "nhl/ui/kettle_assets/playerportraits/";

    /// <summary>The menu portrait of an art id.</summary>
    public static string PortraitPath(int artId) => $"{PortraitFolder}p{artId}.gnf";

    /// <summary>
    /// Encodes a picture as the portrait for an art id, in the format of that id's own portrait, or of another player's when
    /// the id has none yet (a new file, which the build adds as it does any project file).
    /// </summary>
    public static (string Path, byte[] Gnf, bool Resized) ImportPortrait(VirtualFileSystem vfs, int artId, RgbaImage picture,
        BCnEncoder.Encoder.CompressionQuality quality = BCnEncoder.Encoder.CompressionQuality.Balanced)
    {
        var path = PortraitPath(artId);
        var template = vfs.Find(path)
            ?? vfs.Files.FirstOrDefault(f => f.Path.StartsWith(PortraitFolder, StringComparison.OrdinalIgnoreCase) && f.Extension == ".gnf")
            ?? throw new InvalidDataException("The game has no player portraits to copy the format from.");
        var original = vfs.ReadContent(template);
        if (TexturePipeline.ImportBlocker(original) is { } blocker)
            throw new NotSupportedException(blocker);
        var result = TexturePipeline.Import(original, picture, quality);
        return (path, result.Gnf, result.Resized);
    }

    public static string Meta(VirtualFileSystem vfs) =>
        Encoding.UTF8.GetString(vfs.ReadContent(vfs.Find(TeamCatalog.MetaPath) ?? throw new InvalidDataException($"The game has no {TeamCatalog.MetaPath}.")));

    /// <summary>The database the project holds, or the game's when the project has none (or no project is given).</summary>
    public static byte[] DatabaseBytes(VirtualFileSystem vfs, ModProject? project) =>
        project != null && project.TryGet(TeamCatalog.DatabasePath, out var stored) ? project.ReadBlob(stored)
        : vfs.ReadContent(vfs.Find(TeamCatalog.DatabasePath) ?? throw new InvalidDataException($"The game has no {TeamCatalog.DatabasePath}."));

    public static TdbFile Parse(byte[] data, string meta)
    {
        var file = TdbFile.Parse((byte[])data.Clone());
        file.ApplyMeta(meta);
        return file;
    }

    /// <summary>The project's (or game's) roster, with its year shift found against the stock database.</summary>
    public static RosterBook Open(VirtualFileSystem vfs, ModProject? project)
    {
        var meta = Meta(vfs);
        var book = RosterBook.Open(Parse(DatabaseBytes(vfs, project), meta));
        if (vfs.ReadStock(TeamCatalog.DatabasePath) is { } stock)
            book.YearShift = RosterBook.DetectYearShift(RosterBook.Open(Parse(stock, meta)), book);
        return book;
    }

    /// <summary>A roster save's own roster, read only, with the same year shift as <paramref name="like"/>.</summary>
    public static RosterBook OpenSave(VirtualFileSystem vfs, string saveFolder, RosterBook? like = null)
    {
        var book = RosterBook.Open(Parse(RosterSave.Read(saveFolder).Database, Meta(vfs)));
        book.YearShift = like?.YearShift ?? 0;
        return book;
    }

    /// <summary>The value lists, with names from eng_us.db and the commentary names from the playernames table.</summary>
    public static RosterChoices Choices(VirtualFileSystem vfs, RosterBook book)
    {
        byte[]? Read(string path) => vfs.Find(path) is { } file ? vfs.ReadContent(file) : null;
        LocDictionary? loc = null;
        try
        {
            if (Read(SlotNames.DictionaryPath) is { } dict)
                loc = new LocDictionary(dict);
        }
        catch (InvalidDataException)
        {
        }
        var teams = book.Teams.Values.OrderBy(t => t.Index).Select(t => ((long)t.Index, $"{t.Name}{(t.Abbreviation.Length > 0 ? $" ({t.Abbreviation})" : "")}")).Prepend((-1L, "None"));
        var choices = RosterChoices.Load(Read, loc, teams);
        if (book.File.Tables.FirstOrDefault(t => t.Label == "playernames") is { } names)
        {
            var audio = names.Fields.FirstOrDefault(f => f.Label == "audioid");
            var name = names.Fields.FirstOrDefault(f => f.Label == "name");
            var first = names.Fields.FirstOrDefault(f => f.Label == "isfirstname");
            if (audio != null && name != null)
                choices.Put(RosterChoices.PlayByPlay, Enumerable.Range(0, names.Count)
                    .Where(r => first == null || RosterBook.Raw(names, r, first) == 0)
                    .Select(r => (RosterBook.Raw(names, r, audio), ((string)names.Read(r, name)).Trim()))
                    .GroupBy(p => p.Item1).Select(g => g.First()).OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase));
        }
        return choices;
    }

    /// <summary>
    /// Finds players by game_id, "First Last", or last name (case ignored). More than one match is returned as is, so the
    /// caller can ask which.
    /// </summary>
    public static IReadOnlyList<PlayerSummary> Find(RosterBook book, string who)
    {
        if (long.TryParse(who, out var id) && book.Summary(id) is { } byId)
            return [byId];
        var all = book.Players();
        var full = all.Where(p => p.Name.Equals(who, StringComparison.OrdinalIgnoreCase)).ToList();
        if (full.Count > 0)
            return full;
        return all.Where(p => p.LastName.Equals(who, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
