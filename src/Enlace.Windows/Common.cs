using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Enlace.Windows;

public static class AppIcon
{
    private static Icon? _icon;

    public static Icon Get()
    {
        if (_icon != null) return _icon;
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("enlace.ico");
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
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Enlace");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string ReceivedFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Enlace");

    public static string ClipboardFolder => Path.Combine(Path.GetTempPath(), "Enlace", "Portapapeles");

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
