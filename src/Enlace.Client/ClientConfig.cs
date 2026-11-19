using Enlace.Protocol;
using Enlace.Windows;

namespace Enlace.Client;

internal sealed class SavedHost
{
    public string Host { get; set; } = "";
    public string? ProtectedPassword { get; set; }
}

internal sealed class ClientConfig
{
    private const string File = "visor.json";

    /// <summary>Formato antiguo (sólo últimos equipos): se pasa a la libreta al cargar.</summary>
    public List<SavedHost>? Hosts { get; set; }
    public AddressBook Book { get; set; } = new();
    public bool SyncClipboardFiles { get; set; } = true;
    public bool FitToWindow { get; set; } = true;
    public int MaxClipboardFilesMB { get; set; } = 2048;

    public static ClientConfig Load()
    {
        var c = AppPaths.LoadJson<ClientConfig>(File);
        if (c.Hosts is { Count: > 0 })
        {
            foreach (var h in c.Hosts)
                c.Book.Entries.Add(new BookEntry { Name = h.Host, Host = h.Host, ProtectedPassword = h.ProtectedPassword });
            c.Hosts = null;
            c.Save();
        }
        return c;
    }

    public void Save() => AppPaths.SaveJson(File, this);

    /// <summary>"equipo", "equipo:5901", "192.168.1.5::5900" o "[fe80::1]:5900".</summary>
    public static bool TryParseHost(string input, out string host, out int port) => HostAddress.TryParse(input, out host, out port);
}
