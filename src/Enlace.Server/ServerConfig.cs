using Enlace.Protocol;
using Enlace.Windows;
using Microsoft.Win32;

namespace Enlace.Server;

internal sealed class ServerConfig
{
    private const string File = "servidor.json";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "InfollopAssistServidor";

    public int Port { get; set; } = Rfb.DefaultPort;
    public string? ProtectedPassword { get; set; }
    public bool ViewOnly { get; set; }
    public int MaxClipboardFilesMB { get; set; } = 2048;

    public string? Password => Secret.Unprotect(ProtectedPassword);

    public static ServerConfig Load() => AppPaths.LoadJson<ServerConfig>(File);
    public void Save() => AppPaths.SaveJson(File, this);

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
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunName, false);
        }
    }
}
