using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Enlace.Windows;

/// <summary>Nombre, versión y copyright que se muestran en los programas.</summary>
public static class Brand
{
    public const string Product = "InfollopAssist";
    public const string ServerName = "InfollopAssist Servidor";
    public const string ViewerName = "InfollopAssist Visor";
    public const string Copyright = "© 2026 Infollop. Todos los derechos reservados.";

    public static Version Version =>
        typeof(Brand).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";

    /// <summary>Logo grande (PNG) para las pantallas de "Acerca de".</summary>
    public static Image? Logo()
    {
        using var s = typeof(Brand).Assembly.GetManifestResourceStream("logo.png");
        return s == null ? null : Image.FromStream(new MemoryStream(ReadAll(s)));
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

public static class AppIcon
{
    private static Icon? _icon;

    public static Icon Get()
    {
        if (_icon != null) return _icon;
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("infollopassist.ico");
        _icon = s != null ? new Icon(s) : SystemIcons.Application;
        return _icon;
    }

    public static Icon Small() => new(Get(), SystemInformation.SmallIconSize);
}

/// <summary>Contraseñas guardadas cifradas con DPAPI (sólo las puede leer el mismo usuario de Windows).</summary>
public static class Secret
{
    private static readonly byte[] Entropy = "Enlace.v1"u8.ToArray();

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }
}

public static class AppPaths
{
    public static string ConfigDir
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var d = Path.Combine(appData, "InfollopAssist");
            if (!Directory.Exists(d))
            {
                Directory.CreateDirectory(d);
                // Versiones anteriores (Enlace): conservar libreta y configuración
                var old = Path.Combine(appData, "Enlace");
                if (Directory.Exists(old))
                    foreach (var f in Directory.GetFiles(old)) File.Copy(f, Path.Combine(d, Path.GetFileName(f)), false);
            }
            return d;
        }
    }

    public static string ReceivedFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "InfollopAssist");

    public static string ClipboardFolder => Path.Combine(Path.GetTempPath(), "InfollopAssist", "Portapapeles");

    /// <summary>Borra copias temporales de archivos pegados de hace más de un día.</summary>
    public static void CleanClipboardFolder()
    {
        try
        {
            if (!Directory.Exists(ClipboardFolder)) return;
            foreach (var d in Directory.GetDirectories(ClipboardFolder))
                if (Directory.GetCreationTime(d) < DateTime.Now.AddDays(-1))
                    Directory.Delete(d, true);
        }
        catch { }
    }

    public static T LoadJson<T>(string file) where T : new()
    {
        try
        {
            var p = Path.Combine(ConfigDir, file);
            if (File.Exists(p)) return JsonSerializer.Deserialize<T>(File.ReadAllText(p)) ?? new T();
        }
        catch { }
        return new T();
    }

    public static void SaveJson<T>(string file, T value)
    {
        var p = Path.Combine(ConfigDir, file);
        File.WriteAllText(p, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IEnumerable<string> LocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var a in ni.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return a.Address.ToString();
        }
    }

    public static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.#} MB",
        _ => $"{b / 1073741824.0:0.##} GB",
    };
}
