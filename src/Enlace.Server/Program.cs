using System.ServiceProcess;
using Enlace.Windows;

namespace Enlace.Server;

/// <summary>
/// Un único ejecutable con varios papeles:
///   (sin argumentos) / --abrir   bandeja + ventana de estado
///   --bandeja                    bandeja minimizada (arranque con Windows)
///   --servicio                   servicio de Windows (lo lanza el sistema)
///   --agente                     servidor en la sesión del usuario como SYSTEM (lo lanza el servicio)
///   --instalar-servicio / --quitar-servicio   (con permisos de administrador)
/// </summary>
internal static class Program
{
    private const string ShowEventName = @"Local\InfollopAssist.Servidor.Mostrar";
    private static Mutex? _single;

    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
        switch (mode)
        {
            case "--servicio":
                ServiceBase.Run(new InfollopService());
                return 0;
            case "--agente":
                Agent.Run();
                return 0;
            case "--instalar-servicio":
                return ServiceMode.Install();
            case "--quitar-servicio":
                return ServiceMode.Uninstall();
        }

        Updater.CleanupOld();
        bool showWindow = mode != "--bandeja";

        _single = new Mutex(true, @"Local\InfollopAssist.Servidor", out bool first);
        if (!first)
        {
            // Ya está en marcha: pedirle que muestre su ventana
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            return 0;
        }

        ApplicationConfiguration.Initialize();
        var ctx = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ctx);

        bool service = ServiceMode.IsInstalled();
        IServerBackend backend;
        if (service) backend = new PipeBackend();
        else
        {
            var cfg = ServerConfig.Load();
            if (cfg.Password == null)
            {
                using var f = new PasswordForm(required: true);
                if (f.ShowDialog() != DialogResult.OK) return 0;
                cfg.SetPassword(f.Password);
                cfg.Save();
            }
            backend = new ServerCore(serviceMode: false, () => AppPaths.ReceivedFolder, () => AppPaths.ClipboardFolder);
        }

        var app = new TrayApp(backend, service, showWindow);

        // Otra copia arrancada a mano: mostrar la ventana de esta
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (showEvent.WaitOne()) ctx.Post(_ => app.ShowStatus(), null);
        }) { IsBackground = true }.Start();

        Application.Run(app);
        return 0;
    }

    /// <summary>Libera el bloqueo de instancia única (antes de reiniciarse o de pasar a modo servicio).</summary>
    public static void ReleaseSingleInstance()
    {
        try { _single?.ReleaseMutex(); } catch { }
        _single?.Dispose();
        _single = null;
    }
}
