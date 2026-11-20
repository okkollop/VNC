namespace Enlace.Protocol;

public static class HostAddress
{
    /// <summary>"equipo" (puerto 6900), "equipo:7000", "192.168.1.5::5900" o "[fe80::1]:6900".</summary>
    public static bool TryParse(string input, out string host, out int port)
    {
        input = input.Trim();
        host = input;
        port = Rfb.DefaultPort;
        if (input.Length == 0) return false;
        // IPv6 sin corchetes (p. ej. fe80::1): sin puerto. Sólo "nombre::puerto" o "IPv4::puerto" usan la forma VNC.
        int dc = input.IndexOf("::", StringComparison.Ordinal);
        bool vncDouble = dc > 0 && input.IndexOf(':') == dc && input.LastIndexOf(':') == dc + 1 &&
                         !input[..dc].All(Uri.IsHexDigit);
        if (!vncDouble && !input.StartsWith('[') && input.Count(c => c == ':') >= 2 && System.Net.IPAddress.TryParse(input, out _)) return true;

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
            port = p < 100 ? 5900 + p : p; // convención VNC: ":1" = pantalla 1 = 5901
            return port is > 0 and < 65536;
        }
        return true; // nombre o IPv6 sin puerto
    }
}
