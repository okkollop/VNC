namespace Enlace.Protocol;

/// <summary>Un equipo guardado en la libreta del visor.</summary>
public sealed class BookEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>Carpeta del equipo ("" = sin carpeta).</summary>
    public string Folder { get; set; } = "";
    /// <summary>Contraseña cifrada por la aplicación (opaca para esta clase).</summary>
    public string? ProtectedPassword { get; set; }
    public DateTime? LastConnected { get; set; }
    /// <summary>Identificador de origen al importar (Uuid de RealVNC) para no duplicar.</summary>
    public string? ImportId { get; set; }
}

public sealed record ImportResult(int Added, int Updated, int Skipped);

/// <summary>Libreta de direcciones: equipos agrupados en carpetas (un equipo, una carpeta).</summary>
public sealed class AddressBook
{
    public List<BookEntry> Entries { get; set; } = new();
    /// <summary>Carpetas creadas a mano (pueden estar vacías).</summary>
    public List<string> Folders { get; set; } = new();

    public IReadOnlyList<string> AllFolders() =>
        Folders.Concat(Entries.Select(e => e.Folder))
               .Where(f => f.Length > 0)
               .Distinct(StringComparer.CurrentCultureIgnoreCase)
               .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
               .ToList();

    public void AddFolder(string name)
    {
        name = name.Trim();
        if (name.Length > 0 && !AllFolders().Contains(name, StringComparer.CurrentCultureIgnoreCase)) Folders.Add(name);
    }

    public void RenameFolder(string from, string to)
    {
        to = to.Trim();
        foreach (var e in Entries.Where(e => string.Equals(e.Folder, from, StringComparison.CurrentCultureIgnoreCase))) e.Folder = to;
        Folders.RemoveAll(f => string.Equals(f, from, StringComparison.CurrentCultureIgnoreCase));
        AddFolder(to);
    }

    /// <summary>Elimina la carpeta; sus equipos pasan a "sin carpeta".</summary>
    public void DeleteFolder(string name) => RenameFolder(name, "");

    public ImportResult Import(IEnumerable<RealVncEntry> items)
    {
        int added = 0, updated = 0, skipped = 0;
        foreach (var it in items)
        {
            if (string.IsNullOrWhiteSpace(it.Host)) { skipped++; continue; }
            var existing = Entries.FirstOrDefault(e => it.Uuid != null && e.ImportId == it.Uuid)
                           ?? Entries.FirstOrDefault(e => string.Equals(e.Host, it.Host, StringComparison.OrdinalIgnoreCase) &&
                                                          string.Equals(e.Name, it.Name, StringComparison.CurrentCultureIgnoreCase));
            if (existing != null)
            {
                existing.Name = it.Name;
                existing.Host = it.Host;
                existing.Folder = it.Folder;
                existing.ImportId ??= it.Uuid;
                if (it.LastConnected > existing.LastConnected || existing.LastConnected == null) existing.LastConnected = it.LastConnected;
                updated++;
            }
            else
            {
                Entries.Add(new BookEntry { Name = it.Name, Host = it.Host, Folder = it.Folder, LastConnected = it.LastConnected, ImportId = it.Uuid });
                added++;
            }
        }
        return new ImportResult(added, updated, skipped);
    }
}

public sealed record RealVncEntry(string Name, string Host, string Folder, DateTime? LastConnected, string? Uuid);

/// <summary>
/// Lee los archivos .vnc que exporta RealVNC Viewer (texto "Clave=Valor").
/// Se usan FriendlyName, Host, Labels (carpeta), ConnTime y Uuid; el resto se ignora.
/// </summary>
public static class RealVncImport
{
    public static RealVncEntry? Parse(string text, string? fallbackName = null)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('[') || line.StartsWith(';') || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        if (!d.TryGetValue("Host", out var host) || host.Length == 0) return null;

        string name = d.TryGetValue("FriendlyName", out var n) && n.Length > 0 ? n : fallbackName ?? host;
        string folder = d.TryGetValue("Labels", out var l) ? l.Split(',')[0].Trim() : "";
        DateTime? when = null;
        if (d.TryGetValue("ConnTime", out var t) &&
            DateTime.TryParse(t, System.Globalization.CultureInfo.InvariantCulture,
                              System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            when = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        d.TryGetValue("Uuid", out var uuid);
        return new RealVncEntry(name, host, folder, when, string.IsNullOrEmpty(uuid) ? null : uuid);
    }

    /// <summary>Lee archivos .vnc sueltos y/o carpetas que los contengan (recursivo).</summary>
    public static List<RealVncEntry> ReadPaths(IEnumerable<string> paths, List<string>? errors = null)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) files.AddRange(Directory.EnumerateFiles(p, "*.vnc", SearchOption.AllDirectories));
            else if (File.Exists(p)) files.Add(p);
        }
        var result = new List<RealVncEntry>();
        foreach (var f in files)
        {
            try
            {
                var e = Parse(File.ReadAllText(f), Path.GetFileNameWithoutExtension(f));
                if (e != null) result.Add(e);
                else errors?.Add($"{Path.GetFileName(f)}: no tiene dirección");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors?.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }
        return result;
    }
}
