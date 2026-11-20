using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Enlace.Windows;

namespace Enlace.Server;

/// <summary>
/// Comunicación bandeja (usuario) ⇄ agente (SYSTEM) en modo servicio: una línea JSON por mensaje.
/// </summary>
internal static class PipeProto
{
    public const string Name = "InfollopAssist.Agente";

    public static string Msg(string type, object? data = null)
    {
        var o = new JsonObject { ["t"] = type };
        if (data != null) o["d"] = JsonSerializer.SerializeToNode(data);
        return o.ToJsonString();
    }
}

/// <summary>Lado del agente: expone el <see cref="ServerCore"/> a la bandeja del usuario.</summary>
internal sealed class PipeHost
{
    private readonly ServerCore _core;
    private readonly object _lock = new();
    private StreamWriter? _out;

    public PipeHost(ServerCore core)
    {
        _core = core;
        _core.StatusChanged += () => Send("status", _core.Status);
        _core.Notify += (t, f) => Send("notify", new { text = t, folder = f });
        _core.ChatReceived += (id, from, text) => Send("chat", new { id, from, text });
        _core.RemoteText += t => Send("remoteText", t);
        _core.RemoteFiles += p => Send("remoteFiles", p);
    }

    private void Send(string type, object? data)
    {
        lock (_lock)
        {
            if (_out == null) return;
            try { _out.WriteLine(PipeProto.Msg(type, data)); _out.Flush(); }
            catch { _out = null; }
        }
    }

    private static PipeSecurity Security()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Sólo el usuario sentado delante del equipo (sesión interactiva)
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return ps;
    }

    public void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(PipeProto.Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, Security());
                pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = false };
                lock (_lock) _out = writer;
                Send("config", _core.Config);
                Send("status", _core.Status);
                string? line;
                while ((line = reader.ReadLine()) != null) Handle(line);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { ServiceMode.Log("Tubería: " + ex.Message); Thread.Sleep(500); }
            finally { lock (_lock) _out = null; }
        }
    }

    private void Handle(string line)
    {
        try
        {
            var o = JsonNode.Parse(line)!.AsObject();
            var d = o["d"];
            switch ((string?)o["t"])
            {
                case "status": Send("status", _core.Status); break;
                case "disconnectAll": _core.DisconnectAll(); break;
                case "chat": _core.SendChat((int)d!["id"]!, (string)d["from"]!, (string)d["text"]!); break;
                case "clipText": _core.SendClipboardText((string)d!); break;
                case "clipFiles": _core.SendClipboardFiles(d!.Deserialize<string[]>()!); break;
                case "saveConfig":
                    _core.SaveConfig(d!.Deserialize<ServerConfig>()!);
                    Send("config", _core.Config);
                    break;
            }
        }
        catch (Exception ex)
        {
            Send("notify", new { text = "Error: " + ex.Message, folder = (string?)null });
        }
    }
}

/// <summary>Lado de la bandeja: implementa <see cref="IServerBackend"/> hablando con el agente.</summary>
internal sealed class PipeBackend : IServerBackend
{
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private StreamWriter? _out;
    private ServerStatus _status = new(false, 0, "Conectando con el servicio…", true, Array.Empty<SessionInfo>(), true);

    public PipeBackend()
    {
        _ui = SynchronizationContext.Current!;
        Config = ServerConfig.Load();
        new Thread(Loop) { IsBackground = true, Name = "tuberia" }.Start();
    }

    public event Action? StatusChanged;
    public event Action<string, string?>? Notify;
    public event Action<int, string, string>? ChatReceived;
    public event Action<string>? RemoteText;
    public event Action<IReadOnlyList<string>>? RemoteFiles;

    public ServerStatus Status => _status;
    public ServerConfig Config { get; private set; }

    private void Post(Action a) => _ui.Post(_ => a(), null);

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeProto.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(3000);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                lock (_lock) _out = new StreamWriter(pipe, new UTF8Encoding(false));
                string? line;
                while ((line = reader.ReadLine()) != null) Handle(line);
            }
            catch (Exception) when (!_cts.IsCancellationRequested) { }
            lock (_lock) _out = null;
            _status = _status with { Listening = false, Error = "El servicio InfollopAssist no responde", Sessions = Array.Empty<SessionInfo>() };
            Post(() => StatusChanged?.Invoke());
            Thread.Sleep(2000);
        }
    }

    private void Handle(string line)
    {
        var o = JsonNode.Parse(line)!.AsObject();
        var d = o["d"];
        switch ((string?)o["t"])
        {
            case "status":
                var st = d!.Deserialize<ServerStatus>()!;
                Post(() => { _status = st; StatusChanged?.Invoke(); });
                break;
            case "config":
                var cfg = d!.Deserialize<ServerConfig>()!;
                Post(() => { Config = cfg; StatusChanged?.Invoke(); });
                break;
            case "notify":
                var (t, f) = ((string)d!["text"]!, (string?)d["folder"]);
                Post(() => Notify?.Invoke(t, f));
                break;
            case "chat":
                var (id, from, text) = ((int)d!["id"]!, (string)d["from"]!, (string)d["text"]!);
                Post(() => ChatReceived?.Invoke(id, from, text));
                break;
            case "remoteText":
                var rt = (string)d!;
                Post(() => RemoteText?.Invoke(rt));
                break;
            case "remoteFiles":
                var rf = d!.Deserialize<string[]>()!;
                Post(() => RemoteFiles?.Invoke(rf));
                break;
        }
    }

    private void Send(string type, object? data)
    {
        lock (_lock)
        {
            if (_out == null) throw new InvalidOperationException("El servicio InfollopAssist no responde");
            _out.WriteLine(PipeProto.Msg(type, data));
            _out.Flush();
        }
    }

    public void SaveConfig(ServerConfig cfg) => Send("saveConfig", cfg);
    public void DisconnectAll() => Send("disconnectAll", null);
    public void SendChat(int sessionId, string from, string text) => Send("chat", new { id = sessionId, from, text });
    public void SendClipboardText(string text) { try { Send("clipText", text); } catch { } }
    public void SendClipboardFiles(string[] paths) { try { Send("clipFiles", paths); } catch { } }

    public void Dispose() => _cts.Cancel();
}

/// <summary>Carpetas del usuario con sesión iniciada, vistas desde el agente (que corre como SYSTEM).</summary>
internal static class ConsoleUser
{
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserProfileDirectoryW(IntPtr token, StringBuilder? dir, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    public static string? ProfileDir()
    {
        if (!WTSQueryUserToken(WTSGetActiveConsoleSessionId(), out var tok)) return null;
        try
        {
            int size = 260;
            var sb = new StringBuilder(size);
            return GetUserProfileDirectoryW(tok, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(tok); }
    }

    private static string Fallback(string sub) => Path.Combine(AppPaths.MachineConfigDir, sub);

    public static string ReceivedFolder() =>
        ProfileDir() is { } p ? Path.Combine(p, "Downloads", "InfollopAssist") : Fallback("Recibidos");

    public static string ClipboardFolder() =>
        ProfileDir() is { } p ? Path.Combine(p, "AppData", "Local", "Temp", "InfollopAssist", "Portapapeles") : Fallback("Portapapeles");
}

/// <summary>Proceso agente: servidor sin interfaz, como SYSTEM en la sesión de consola.</summary>
internal static class Agent
{
    public static void Run()
    {
        using var mutex = new Mutex(true, @"Global\InfollopAssist.Agente", out bool first);
        if (!first) return;
        ServiceMode.Log("Agente iniciado");
        using var core = new ServerCore(serviceMode: true, ConsoleUser.ReceivedFolder, ConsoleUser.ClipboardFolder);
        core.Desktop.SecureAttention += () =>
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ServiceMode.SasEventName);
                ev.Set();
            }
            catch (Exception ex) { ServiceMode.Log("Ctrl+Alt+Supr: " + ex.Message); }
        };
        new PipeHost(core).Run(CancellationToken.None);
    }
}
