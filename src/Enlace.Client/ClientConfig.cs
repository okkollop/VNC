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

    public List<SavedHost> Hosts { get; set; } = new();
    public bool SyncClipboardFiles { get; set; } = true;
    public bool FitToWindow { get; set; } = true;
    public int MaxClipboardFilesMB { get; set; } = 2048;

    public static ClientConfig Load() => AppPaths.LoadJson<ClientConfig>(File);
    public void Save() => AppPaths.SaveJson(File, this);

    public void Remember(string host, string? password)
    {
        Hosts.RemoveAll(h => string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase));
        Hosts.Insert(0, new SavedHost { Host = host, ProtectedPassword = password == null ? null : Secret.Protect(password) });
        if (Hosts.Count > 15) Hosts.RemoveRange(15, Hosts.Count - 15);
        Save();
    }

    /// <summary>"equipo", "equipo:5901", "192.168.1.5::5900" o "[fe80::1]:5900".</summary>
    public static bool TryParseHost(string input, out string host, out int port)
    {
        input = input.Trim();
        host = input;
        port = Rfb.DefaultPort;
        if (input.Length == 0) return false;

        if (input.StartsWith('['))
        {
            int end = input.IndexOf(']');
            if (end < 0) return false;
            host = input[1..end];
            var rest = input[(end + 1)..];
            if (rest.StartsWith(':')) return int.TryParse(rest.TrimStart(':'), out port) && port is > 0 and < 65536;
            return rest.Length == 0;
        }

        int dbl = input.IndexOf("::", StringComparison.Ordinal);
        if (dbl > 0 && input.IndexOf(':') == dbl && input.LastIndexOf(':') == dbl + 1)
        {
            host = input[..dbl];
            return int.TryParse(input[(dbl + 2)..], out port) && port is > 0 and < 65536;
        }

        int colon = input.IndexOf(':');
        if (colon > 0 && colon == input.LastIndexOf(':'))
        {
            host = input[..colon];
            if (!int.TryParse(input[(colon + 1)..], out int p)) return false;
            // Estilo VNC: ":1" significa pantalla 1 = puerto 5901
            port = p < 100 ? Rfb.DefaultPort + p : p;
            return port is > 0 and < 65536;
        }
        return true; // nombre o IPv6 sin puerto
    }
}
