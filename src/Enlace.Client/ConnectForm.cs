using Enlace.Protocol;
using Enlace.Windows;

namespace Enlace.Client;

internal sealed class ConnectForm : Form
{
    private readonly ClientConfig _cfg = ClientConfig.Load();
    private readonly ComboBox _host = new() { Width = 260 };
    private readonly TextBox _pw = new() { UseSystemPasswordChar = true, Width = 260 };
    private readonly CheckBox _remember = new() { Text = "Recordar contraseña en este equipo", AutoSize = true, Checked = true };
    private readonly Button _connect = new() { Text = "Conectar", AutoSize = true };
    private readonly Label _msg = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(380, 0) };
    private CancellationTokenSource? _cts;

    public ConnectForm()
    {
        Text = "Enlace — Conectar";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) });
            grid.Controls.Add(c);
        }
        Row("Equipo (IP o nombre):", _host);
        Row("Contraseña:", _pw);
        grid.Controls.Add(new Label());
        grid.Controls.Add(_remember);
        var hint = new Label
        {
            Text = "Ejemplos: 192.168.1.20   ·   oficina-pc   ·   192.168.1.20:5901",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 6, 3, 3),
        };
        grid.Controls.Add(hint);
        grid.SetColumnSpan(hint, 2);
        grid.Controls.Add(_msg);
        grid.SetColumnSpan(_msg, 2);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(_connect);
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);
        AcceptButton = _connect;

        foreach (var h in _cfg.Hosts) _host.Items.Add(h.Host);
        if (_cfg.Hosts.Count > 0)
        {
            _host.Text = _cfg.Hosts[0].Host;
            FillPassword();
        }
        _host.SelectedIndexChanged += (_, _) => FillPassword();
        _connect.Click += async (_, _) => await ConnectAsync();
    }

    private void FillPassword()
    {
        var saved = _cfg.Hosts.FirstOrDefault(h => string.Equals(h.Host, _host.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        _pw.Text = Secret.Unprotect(saved?.ProtectedPassword) ?? "";
    }

    private async Task ConnectAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        _msg.Text = "";
        var input = _host.Text.Trim();
        if (!ClientConfig.TryParseHost(input, out var host, out var port))
        {
            _msg.Text = "Dirección no válida.";
            return;
        }

        _cts = new CancellationTokenSource();
        _connect.Text = "Cancelar";
        _host.Enabled = _pw.Enabled = false;
        UseWaitCursor = true;
        RfbClient? client = null;
        try
        {
            client = await RfbClient.ConnectAsync(host, port, _pw.Text, _cts.Token);
        }
        catch (RfbAuthException ex) { _msg.Text = ex.Message; }
        catch (OperationCanceledException) { _msg.Text = _cts.IsCancellationRequested ? "" : "No responde (tiempo agotado). ¿Está encendido y el servidor en marcha?"; }
        catch (System.Net.Sockets.SocketException ex) { _msg.Text = $"No se pudo conectar: {ex.Message}"; }
        catch (Exception ex) when (ex is IOException or RfbException or EndOfStreamException) { _msg.Text = "Error: " + ex.Message; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _connect.Text = "Conectar";
            _host.Enabled = _pw.Enabled = true;
            UseWaitCursor = false;
        }
        if (client == null) return;

        _cfg.Remember(input, _remember.Checked ? _pw.Text : null);
        _host.Items.Clear();
        foreach (var h in _cfg.Hosts) _host.Items.Add(h.Host);
        _host.Text = input;

        Hide();
        string? error;
        using (var viewer = new ViewerForm(client, _cfg, input))
        {
            viewer.ShowDialog();
            error = viewer.DisconnectError;
        }
        Show();
        Activate();
        if (error != null) _msg.Text = "Conexión perdida: " + error;
    }
}
