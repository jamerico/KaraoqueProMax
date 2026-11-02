using System.Globalization;
using System.Text;

namespace KaraokeCore;

public sealed record Song(string Collection, string Code, string Artist, string Title, string Path, string Note)
{
    public string Display => $"{Artist} – {Title}";
    internal string Key = "";
}

/// <summary>Songs from the decrypted folders' _catalogo.csv, searchable by name (accent- and case-insensitive).</summary>
public sealed class Catalog
{
    public List<Song> Songs { get; } = [];

    public static Catalog Load(IEnumerable<string> folders)
    {
        var c = new Catalog();
        foreach (var folder in folders)
        {
            var csv = System.IO.Path.Combine(folder, "_catalogo.csv");
            if (!File.Exists(csv)) continue;
            string coll = System.IO.Path.GetFileName(folder).Replace("_decrypted", "");
            var lines = File.ReadAllLines(csv, Encoding.UTF8);
            var head = Split(lines[0]);
            int iCode = head.IndexOf("codigo"), iFile = head.IndexOf("arquivo"), iArt = head.IndexOf("interprete"), iTit = head.IndexOf("titulo"), iObs = head.IndexOf("observacao");
            foreach (var line in lines.Skip(1))
            {
                var f = Split(line);
                if (f.Count <= iTit || string.IsNullOrEmpty(f[iFile])) continue;
                var path = System.IO.Path.Combine(folder, f[iFile]);
                var s = new Song(coll, f[iCode], f[iArt], f[iTit], path, iObs >= 0 && iObs < f.Count ? f[iObs] : "");
                s.Key = Normalize($"{s.Artist} {s.Title} {s.Code}");
                c.Songs.Add(s);
            }
        }
        return c;
    }

    /// <summary>Every word of the query must appear; title matches rank above artist matches.</summary>
    public IEnumerable<Song> Search(string query, int max = 300)
    {
        var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        return Songs.Where(s => words.All(w => s.Key.Contains(w)))
            .OrderBy(s => Normalize(s.Title).StartsWith(words[0]) ? 0 : Normalize(s.Artist).StartsWith(words[0]) ? 1 : 2)
            .ThenBy(s => s.Artist).ThenBy(s => s.Title).Take(max);
    }

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return sb.ToString();
    }

    static List<string> Split(string line)
    {
        var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ';') { r.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        r.Add(sb.ToString());
        return r;
    }
}
