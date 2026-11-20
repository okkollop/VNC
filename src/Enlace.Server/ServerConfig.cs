using System.Text.Json;
using Enlace.Protocol;
using Enlace.Windows;
using Microsoft.Win32;

namespace Enlace.Server;

/// <summary>Configuración del servidor, común a todo el equipo (modo usuario y modo servicio).</summary>
internal sealed class ServerConfig
{
    private const string FileName = "servidor.json";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "InfollopAssistServidor";

    public int Port { get; set; } = Rfb.DefaultPort;
    /// <summary>Contraseña cifrada con DPAPI del equipo.</summary>
    public string? MachinePassword { get; set; }
    public bool ViewOnly { get; set; }
    public int MaxClipboardFilesMB { get; set; } = 2048;
    public bool AutoUpdate { get; set; } = true;
    public string UpdateUrl { get; set; } = Updater.DefaultBaseUrl;

    public string? Password => Secret.UnprotectMachine(MachinePassword);
    public void SetPassword(string pw) => MachinePassword = Secret.ProtectMachine(pw);

    public static string FilePath => Path.Combine(AppPaths.MachineConfigDir, FileName);

    public static ServerConfig Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        // Migración desde la configuración por usuario de versiones anteriores
        var cfg = new ServerConfig();
        try
        {
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Enlace", FileName);
            if (File.Exists(old))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(old));
                var r = doc.RootElement;
                if (r.TryGetProperty("ProtectedPassword", out var p) && Secret.Unprotect(p.GetString()) is { } pw) cfg.SetPassword(pw);
                if (r.TryGetProperty("ViewOnly", out var v)) cfg.ViewOnly = v.GetBoolean();
                if (r.TryGetProperty("MaxClipboardFilesMB", out var m)) cfg.MaxClipboardFilesMB = m.GetInt32();
                cfg.Save();
            }
        }
        catch { }
        return cfg;
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    public ServerConfig Clone() => JsonSerializer.Deserialize<ServerConfig>(JsonSerializer.Serialize(this))!;

    /// <summary>Arranque automático en modo usuario (no se usa con el servicio).</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --bandeja");
            else k.DeleteValue(RunName, false);
        }
    }
}
