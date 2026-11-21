using Blueline.Formats.Tdb;

namespace Blueline.Core.Teams;

/// <summary>
/// A kit's own name in the jersey select. The game builds the key <c>VINTAGE_&lt;style&gt;_&lt;art&gt;_&lt;variant&gt;</c>
/// (format string in eboot.bin, beside the kit-type keys) and shows that string in place of the slot's label when the
/// language table has it. Seen in game 2026-11-20: a key added for a new slot (art 900, variant 12) showed as its name.
/// Key lookup is by hash, which ignores case, so stock keys are found whatever their spelling.
/// </summary>
public static class SlotNames
{
    /// <summary>The English table, the one the game reads with the default language.</summary>
    public const string DictionaryPath = "nhl/data/kettle/loc/eng_us.db";

    public static string KeyOf(int style, int artId, int variant) => $"VINTAGE_{style}_{artId}_{variant}";

    public static string KeyOf(UniformSlot slot) => KeyOf(slot.Style, slot.ArtId, slot.Variant);

    private static LocRow? Find(LocDictionary dictionary, string key)
    {
        var hash = LocDictionary.KeyHash(key);
        return dictionary.Rows.FirstOrDefault(r => r.Hash == hash);
    }

    /// <summary>The kit's own name, or null when the game shows the slot's label.</summary>
    public static string? NameOf(LocDictionary dictionary, int style, int artId, int variant) =>
        Find(dictionary, KeyOf(style, artId, variant))?.Text is { Length: > 0 } text ? text : null;

    public static string? NameOf(LocDictionary dictionary, UniformSlot slot) => NameOf(dictionary, slot.Style, slot.ArtId, slot.Variant);

    /// <summary>Gives a kit its own name: changes the stock string when there is one, else adds the key. Returns whether a key was added.</summary>
    public static bool SetName(LocDictionary dictionary, int style, int artId, int variant, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A kit name cannot be empty.");
        var key = KeyOf(style, artId, variant);
        if (Find(dictionary, key) is { Key: { } existing })
        {
            dictionary.SetText(existing, name.Trim());
            return false;
        }
        dictionary.AddKey(key, name.Trim());
        return true;
    }
}
