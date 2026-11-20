using Enlace.Windows;

namespace Enlace.Client;

internal sealed class ViewerOptionsForm : SidebarSettingsForm
{
    private readonly Panel _about;

    public ViewerOptionsForm(ClientConfig cfg) : base($"Opciones — {Brand.ViewerName}")
    {
        var general = AddPage("General", "Ajustes del visor. Se aplican a las conexiones nuevas.");
        var fit = new ToggleSwitch { Checked = cfg.FitToWindow };
        fit.CheckedChanged += (_, _) => { cfg.FitToWindow = fit.Checked; cfg.Save(); };
        AddRow(general, "Ajustar la pantalla a la ventana", "Si se desactiva, se ve a tamaño real con barras de desplazamiento.", fit);
        var sync = new ToggleSwitch { Checked = cfg.SyncClipboardFiles };
        sync.CheckedChanged += (_, _) => { cfg.SyncClipboardFiles = sync.Checked; cfg.Save(); };
        AddRow(general, "Copiar y pegar archivos entre equipos", "Al copiar archivos con Ctrl+C en un equipo se pueden pegar en el otro.", sync);
        var max = new NumericUpDown { Minimum = 0, Maximum = 1_000_000, Increment = 256, Value = cfg.MaxClipboardFilesMB, Width = 100 };
        max.ValueChanged += (_, _) => { cfg.MaxClipboardFilesMB = (int)max.Value; cfg.Save(); };
        AddRow(general, "Límite al copiar archivos (MB)", "0 = sin límite. Para más, usa «Enviar archivos» o arrástralos a la ventana.", max);
        AddRow(general, "Carpeta de recibidos", AppPaths.ReceivedFolder, MakeButton("Abrir", () =>
        {
            Directory.CreateDirectory(AppPaths.ReceivedFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.ReceivedFolder}\"") { UseShellExecute = true });
        }));

        var upd = AddPage("Actualizaciones");
        var auto = new ToggleSwitch { Checked = cfg.AutoUpdate };
        auto.CheckedChanged += (_, _) => { cfg.AutoUpdate = auto.Checked; cfg.Save(); };
        AddRow(upd, "Buscar actualizaciones al abrir", "Una vez al día. Si hay versión nueva, pregunta antes de instalarla.", auto);
        var url = new TextBox { Text = cfg.UpdateUrl, Width = 280 };
        url.Leave += (_, _) => { cfg.UpdateUrl = url.Text.Trim(); Updater.BaseUrl = cfg.UpdateUrl; cfg.Save(); };
        AddRow(upd, "Servidor de actualizaciones", "Dirección donde se publican las versiones.", url);

        _about = AddAboutPage(Brand.ViewerName, async () => await Updater.CheckInteractiveAsync(this, AppKind.Visor));
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void ShowAbout() => Select(_about);
}
