using System.Globalization;
using System.Text;

namespace KaraokeCore;

public sealed record Song(string Collection, string Code, string Artist, string Title, string Path, string Note)
{
    public string Display => Artist.Length > 0 ? $"{Artist} – {Title}" : Title;
    internal string Key = "";
}

/// <summary>
/// Songs from any number of folders, searchable by name (accent- and case-insensitive).
/// A folder with a <c>_catalogo.csv</c> is read from that file; any other folder is scanned (with subfolders) for
/// video files, and artist/title come from the file names ("Code - Artist - Title", "Artist - Title" or just the title).
/// </summary>
public sealed class Catalog
{
    public const string CatalogFile = "_catalogo.csv";
    public static readonly string[] VideoExtensions = [".avi", ".mp4", ".mkv", ".mpg", ".mpeg", ".vob", ".wmv", ".mov", ".webm", ".m4v", ".flv"];

    public List<Song> Songs { get; } = [];

    public static Catalog Load(IEnumerable<string> folders)
    {
        var c = new Catalog();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var csv = System.IO.Path.Combine(folder, CatalogFile);
            if (File.Exists(csv)) c.LoadCsv(folder, csv);
            else c.Scan(folder);
        }
        return c;
    }

    void Add(Song s) { s.Key = Normalize($"{s.Artist} {s.Title} {s.Code}"); Songs.Add(s); }

    void LoadCsv(string folder, string csv)
    {
        string coll = CollectionName(folder);
        var lines = File.ReadAllLines(csv, Encoding.UTF8);
        if (lines.Length == 0) return;
        var head = Split(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int iCode = head.IndexOf("codigo"), iFile = head.IndexOf("arquivo"), iArt = head.IndexOf("interprete"), iTit = head.IndexOf("titulo"), iObs = head.IndexOf("observacao");
        if (iFile < 0) return;
        string Get(List<string> f, int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";
        foreach (var line in lines.Skip(1))
        {
            var f = Split(line);
            var file = Get(f, iFile);
            if (file.Length == 0) continue;
            var path = System.IO.Path.Combine(folder, file);
            var (code, artist, title) = ParseName(System.IO.Path.GetFileNameWithoutExtension(file));
            Add(new Song(coll, Get(f, iCode) is { Length: > 0 } c ? c : code, Get(f, iArt) is { Length: > 0 } a ? a : artist,
                         Get(f, iTit) is { Length: > 0 } t ? t : title, path, Get(f, iObs)));
        }
    }

    void Scan(string folder)
    {
        string coll = CollectionName(folder);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
        }
        catch { return; }
        foreach (var p in files)
        {
            if (!VideoExtensions.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant())) continue;
            var (code, artist, title) = ParseName(System.IO.Path.GetFileNameWithoutExtension(p));
            Add(new Song(coll, code, artist, title, p, ""));
        }
    }

    static string CollectionName(string folder) =>
        System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } n ? n.Replace("_decrypted", "") : folder;

    /// <summary>"01004 - 3 Doors Down - Here Without You" → (01004, 3 Doors Down, Here Without You);
    /// "Artist - Title" → ("", Artist, Title); anything else → ("", "", name).</summary>
    public static (string Code, string Artist, string Title) ParseName(string name)
    {
        name = name.Replace('_', ' ').Trim();
        var parts = name.Split(" - ", 3, StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && parts[0].All(char.IsDigit)) return (parts[0], parts[1], parts[2]);
        if (parts.Length == 3) return ("", parts[0], parts[1] + " - " + parts[2]);
        if (parts.Length == 2 && parts[0].All(char.IsDigit)) return (parts[0], "", parts[1]);
        if (parts.Length == 2) return ("", parts[0], parts[1]);
        return ("", "", name);
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
