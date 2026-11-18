using System.Buffers.Binary;
using System.Xml.Linq;

namespace Blueline.Formats.Tdb;

/// <summary>
/// The CRCValue a roster save carries in its file info, checked against both of the player's real saves. It is nine
/// 32-bit values, each written as six base-62 digits. Each value is the TDB CRC run over the sorted CRCs of a table's live
/// records, fed as little-endian u32s. Only tables whose allocation type has bit 0x2 and that have rows count. Value 0
/// covers every table in the "rosterupdate" save group, in the order the meta lists them. Values 1 to 8 are single tables.
/// </summary>
public static class RosterCrc
{
    private static readonly string[] SingleTables = ["VoEQ", "mHuy", "ubPc", "ZBac", "eGlu", "dSvy", "qiwI", "xjgw"];
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public const int Length = 54;

    public static string Compute(TdbFile file, string metaXml)
    {
        var document = XDocument.Parse(metaXml.TrimStart('﻿'));
        var order = document.Descendants().Where(e => e.Name.LocalName == "table")
            .Where(e => ((string?)e.Attribute("savegroups") ?? "").Split(',').Select(s => s.Trim()).Contains("rosterupdate"))
            .Select(e => (string?)e.Attribute("shortname")).OfType<string>().ToList();
        var tables = file.Tables.ToDictionary(t => t.ShortName, StringComparer.Ordinal);

        uint Feed(uint crc, string shortName)
        {
            if (!tables.TryGetValue(shortName, out var table))
                return crc;
            Span<byte> word = stackalloc byte[4];
            foreach (var record in RecordCrcs(file, table))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, record);
                crc = TdbCrc.Continue(crc, word);
            }
            return crc;
        }

        var all = 0xFFFFFFFFu;
        foreach (var name in order)
            all = Feed(all, name);
        var text = new System.Text.StringBuilder(Length);
        text.Append(Base62(all));
        foreach (var name in SingleTables)
            text.Append(Base62(Feed(0xFFFFFFFFu, name)));
        return text.ToString();
    }

    private static List<uint> RecordCrcs(TdbFile file, TdbTable table)
    {
        var crcs = new List<uint>();
        if ((table.AllocationType & 2) == 0 || table.Count == 0)
            return crcs;
        for (var r = 0; r < table.Count; r++)
        {
            var record = file.Data.AsSpan(table.RecordsAt + r * table.Stride, table.Stride);
            if ((record[^1] & 0x80) == 0)
                crcs.Add(TdbCrc.Compute(record));
        }
        crcs.Sort();
        return crcs;
    }

    private static string Base62(uint value)
    {
        Span<char> digits = stackalloc char[6];
        for (var i = 5; i >= 0; i--)
        {
            digits[i] = Digits[(int)(value % 62)];
            value /= 62;
        }
        return new string(digits);
    }
}
