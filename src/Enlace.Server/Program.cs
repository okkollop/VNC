namespace Enlace.Server;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\Enlace.Servidor", out bool first);
        if (!first)
        {
            MessageBox.Show("Enlace Servidor ya está en marcha (icono junto al reloj).", "Enlace Servidor");
            return;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var cfg = ServerConfig.Load();
        if (cfg.Password == null)
        {
            using var f = new SettingsForm(cfg, firstRun: true);
            if (f.ShowDialog() != DialogResult.OK) return;
        }

        Application.Run(new ServerTray(cfg));
    }
}
