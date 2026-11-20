using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Enlace.Windows;
using Microsoft.Win32;

namespace Enlace.Server;

/// <summary>
/// Modo servicio, como el "modo de servicio" de RealVNC:
/// el servicio (SYSTEM, sesión 0) lanza un "agente" también como SYSTEM pero dentro de la sesión
/// de consola, que es quien captura la pantalla (incluida la de inicio de sesión y UAC). La bandeja
/// del usuario habla con el agente por una tubería con nombre.
/// </summary>
internal static class ServiceMode
{
    public const string ServiceName = "InfollopAssist";
    public const string DisplayName = "InfollopAssist Servidor";
    public const string SasEventName = @"Global\InfollopAssist.SAS";
    private const string MachineRunName = "InfollopAssistBandeja";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "InfollopAssist", "Server");

    /// <summary>Ubicación de la versión 1.0.0 (sin subcarpeta), para limpiarla al reinstalar.</summary>
    private static string OldInstalledExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "InfollopAssist", "InfollopAssistServidor.exe");
    public static string InstalledExe => Path.Combine(InstallDir, "InfollopAssistServidor.exe");

    public static bool IsInstalled()
    {
        try { using var sc = new ServiceController(ServiceName); _ = sc.Status; return true; }
        catch (InvalidOperationException) { return false; }
    }

    public static bool IsRunning()
    {
        try { using var sc = new ServiceController(ServiceName); return sc.Status == ServiceControllerStatus.Running; }
        catch (InvalidOperationException) { return false; }
    }

    // ---------------- Instalar / quitar (proceso elevado) ----------------

    private static int Run(string file, string args)
    {
        var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit(60000);
        return p.ExitCode;
    }

    /// <summary>Lanza esta misma aplicación como administrador (aviso de UAC) con un argumento y espera.</summary>
    public static bool RunElevated(string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; } // el usuario canceló el aviso de UAC
    }

    public static int Install()
    {
        try
        {
            // 1. Copiar el programa a Archivos de programa (ruta fija y protegida)
            Directory.CreateDirectory(InstallDir);
            var me = Environment.ProcessPath!;
            if (!string.Equals(Path.GetFullPath(me), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                if (IsInstalled()) StopService();
                KillOthers(InstalledExe);
                File.Copy(me, InstalledExe, true);
            }
            // Quitar sólo nuestro .exe de la ubicación antigua (en esa carpeta escriben otras aplicaciones)
            if (File.Exists(OldInstalledExe) && !string.Equals(Path.GetFullPath(me), OldInstalledExe, StringComparison.OrdinalIgnoreCase))
            {
                KillOthers(OldInstalledExe);
                try { File.Delete(OldInstalledExe); } catch { }
            }

            // 2. Servicio con reinicio automático si se cae (también tras actualizarse)
            if (!IsInstalled())
                Run("sc.exe", $"create {ServiceName} binPath= \"\\\"{InstalledExe}\\\" --servicio\" start= auto DisplayName= \"{DisplayName}\"");
            else
                Run("sc.exe", $"config {ServiceName} binPath= \"\\\"{InstalledExe}\\\" --servicio\" start= auto");
            Run("sc.exe", $"description {ServiceName} \"Acceso remoto InfollopAssist\"");
            Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/3000/restart/5000/restart/10000");

            // 3. Permitir Ctrl+Alt+Supr enviado por servicios
            using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                k.SetValue("SoftwareSASGeneration", 3, RegistryValueKind.DWord);

            // 4. Firewall
            Run("netsh.exe", $"advfirewall firewall delete rule name=\"{DisplayName}\"");
            Run("netsh.exe", $"advfirewall firewall add rule name=\"{DisplayName}\" dir=in action=allow program=\"{InstalledExe}\" enable=yes profile=any");

            // 5. Bandeja para todos los usuarios al iniciar sesión (en lugar del arranque por usuario)
            using (var k = Registry.LocalMachine.CreateSubKey(RunKey))
                k.SetValue(MachineRunName, $"\"{InstalledExe}\" --bandeja");

            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log("Instalación del servicio: " + ex);
            return 1;
        }
    }

    public static int Uninstall()
    {
        try
        {
            if (IsInstalled())
            {
                StopService();
                Run("sc.exe", $"delete {ServiceName}");
            }
            using (var k = Registry.LocalMachine.CreateSubKey(RunKey)) k.DeleteValue(MachineRunName, false);
            return 0;
        }
        catch (Exception ex)
        {
            Log("Desinstalación del servicio: " + ex);
            return 1;
        }
    }

    private static void StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        catch { }
    }

    /// <summary>Cierra otras copias que se estén ejecutando desde esa ruta (para poder sobrescribir el .exe).</summary>
    private static void KillOthers(string exePath)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                if (!string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
                p.WaitForExit(5000);
            }
            catch { }
        }
    }

    public static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.MachineConfigDir, "servidor.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n"); }
        catch { }
    }
}

/// <summary>El servicio de Windows: mantiene vivo el agente en la sesión activa y se ocupa de actualizar.</summary>
internal sealed class InfollopService : ServiceBase
{
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private IntPtr _agent;
    private uint _agentSession = uint.MaxValue;

    public InfollopService()
    {
        ServiceName = ServiceMode.ServiceName;
        CanStop = true;
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnStart(string[] args)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "agente" };
        _thread.Start();
        _ = Task.Run(UpdateLoop);
    }

    protected override void OnStop()
    {
        _stop.Cancel();
        KillAgent();
    }

    protected override void OnSessionChange(SessionChangeDescription d)
    {
        // Al cambiar de usuario o conectar/desconectar consola, el bucle relanza el agente
        _agentSession = uint.MaxValue - 1;
    }

    private void Loop()
    {
        using var sas = new EventWaitHandle(false, EventResetMode.AutoReset, ServiceMode.SasEventName);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                uint session = Native.WTSGetActiveConsoleSessionId();
                bool alive = _agent != IntPtr.Zero && Native.WaitForSingleObject(_agent, 0) == Native.WAIT_TIMEOUT;
                if (session != 0xFFFFFFFF && (!alive || session != _agentSession))
                {
                    KillAgent();
                    _agent = Native.LaunchAsSystemInSession(session, $"\"{Environment.ProcessPath}\" --agente");
                    _agentSession = session;
                }
            }
            catch (Exception ex)
            {
                ServiceMode.Log("Lanzar agente: " + ex.Message);
            }
            // Esperar 2 s o una petición de Ctrl+Alt+Supr del agente
            if (sas.WaitOne(2000)) Native.TrySendSas();
        }
    }

    private void KillAgent()
    {
        if (_agent == IntPtr.Zero) return;
        Native.TerminateProcess(_agent, 0);
        Native.CloseHandle(_agent);
        _agent = IntPtr.Zero;
    }

    private async Task UpdateLoop()
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), _stop.Token); } catch { return; }
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var cfg = ServerConfig.Load();
                Updater.BaseUrl = cfg.UpdateUrl;
                if (cfg.AutoUpdate && await Updater.CheckAsync(_stop.Token) is { } m)
                {
                    ServiceMode.Log($"Actualizando a {m.Version}");
                    var file = await Updater.DownloadAsync(m, AppKind.Servidor, null, _stop.Token);
                    Updater.Install(file);
                    KillAgent();
                    // Salida "anómala": Windows reinicia el servicio (acciones de recuperación) ya con el .exe nuevo
                    Environment.Exit(1);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ServiceMode.Log("Actualización: " + ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromHours(6), _stop.Token); } catch { return; }
        }
    }

    private static class Native
    {
        public const uint WAIT_TIMEOUT = 0x102;
        private const uint TOKEN_ALL_ACCESS = 0xF01FF, MAXIMUM_ALLOWED = 0x02000000;
        private const int TokenSessionId = 12, SecurityImpersonation = 2, TokenPrimary = 1;
        private const uint CREATE_NO_WINDOW = 0x08000000, CREATE_UNICODE_ENVIRONMENT = 0x400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr tok, uint access, IntPtr attrs, int level, int type, out IntPtr newTok);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetTokenInformation(IntPtr tok, int cls, ref uint info, int len);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUserW(IntPtr tok, string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit,
            uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("sas.dll")] private static extern void SendSAS(bool asUser);

        public static void TrySendSas()
        {
            try { SendSAS(false); } catch (Exception ex) { ServiceMode.Log("SendSAS: " + ex.Message); }
        }

        /// <summary>Lanza un proceso con el token de SYSTEM pero dentro de la sesión indicada (escritorio winsta0\default).</summary>
        public static IntPtr LaunchAsSystemInSession(uint session, string cmd)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out var tok)) throw new Win32Exception();
            try
            {
                if (!DuplicateTokenEx(tok, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var dup)) throw new Win32Exception();
                try
                {
                    if (!SetTokenInformation(dup, TokenSessionId, ref session, 4)) throw new Win32Exception();
                    var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                    if (!CreateProcessAsUserW(dup, null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                            IntPtr.Zero, Path.GetDirectoryName(Environment.ProcessPath), ref si, out var pi))
                        throw new Win32Exception();
                    CloseHandle(pi.hThread);
                    return pi.hProcess;
                }
                finally { CloseHandle(dup); }
            }
            finally { CloseHandle(tok); }
        }
    }
}
