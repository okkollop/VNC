using Enlace.Protocol;
using Enlace.Windows;

namespace Enlace.Client;

/// <summary>Ventana principal del visor: libreta de equipos por carpetas.</summary>
internal sealed class BookForm : Form
{
    private const string AllKey = "\u0001todos", NoneKey = "\u0001ninguna";

    private readonly ClientConfig _cfg = ClientConfig.Load();
    private AddressBook Book => _cfg.Book;

    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Escribe una dirección para conectar o busca por nombre" };
    private readonly Button _go = new() { Text = "Conectar", AutoSize = true, Dock = DockStyle.Right };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, AllowDrop = true, ShowLines = false, FullRowSelect = true, ItemHeight = 24 };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, AllowColumnReorder = true };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly HashSet<string> _connecting = new();
    private int _sortColumn;
    private bool _sortDesc;

    public BookForm()
    {
        Text = "Enlace — Visor";
        Icon = AppIcon.Get();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(940, 640);
        MinimumSize = new Size(600, 400);

        // Barra superior: dirección/búsqueda
        var top = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 5, 8, 3) };
        top.Controls.Add(_search);
        top.Controls.Add(_go);

        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        bar.Items.Add("Nuevo equipo", null, (_, _) => NewEntry());
        bar.Items.Add("Nueva carpeta", null, (_, _) => NewFolder());
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add("Editar", null, (_, _) => EditSelected());
        bar.Items.Add("Eliminar", null, (_, _) => DeleteSelected());
        bar.Items.Add(new ToolStripSeparator());
        var import = new ToolStripDropDownButton("Importar de RealVNC");
        import.DropDownItems.Add("Archivos .vnc…", null, (_, _) => ImportFiles());
        import.DropDownItems.Add("Carpeta con archivos .vnc…", null, (_, _) => ImportFolder());
        bar.Items.Add(import);

        _list.Columns.Add("Nombre", 220);
        _list.Columns.Add("Dirección", 200);
        _list.Columns.Add("Última conexión", 150);
        _list.Columns.Add("Carpeta", 150);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 230 };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_list);

        var statusBar = new StatusStrip();
        statusBar.Items.Add(_status);

        Controls.Add(split);
        Controls.Add(bar);
        Controls.Add(top);
        Controls.Add(statusBar);

        _go.Click += (_, _) => GoFromSearch();
        _search.TextChanged += (_, _) => FillList();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; GoFromSearch(); }
            else if (e.KeyCode == Keys.Down && _list.Items.Count > 0) { _list.Focus(); _list.Items[0].Selected = true; e.Handled = true; }
        };
        _tree.AfterSelect += (_, _) => FillList();
        _list.ItemActivate += (_, _) => { foreach (var e in Selected()) Connect(e); };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.F2) EditSelected();
        };
        _list.ColumnClick += (_, e) =>
        {
            _sortDesc = _sortColumn == e.Column && !_sortDesc;
            _sortColumn = e.Column;
            FillList();
        };
        _list.ContextMenuStrip = BuildListMenu();
        _tree.ContextMenuStrip = BuildTreeMenu();
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node; };

        // Arrastrar equipos a una carpeta
        _list.ItemDrag += (_, _) => DoDragDrop(Selected().Select(e => e.Id).ToArray(), DragDropEffects.Move);
        _tree.DragOver += (_, e) =>
        {
            var n = _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));
            e.Effect = e.Data?.GetDataPresent(typeof(string[])) == true && n != null && (string)n.Tag! != AllKey ? DragDropEffects.Move : DragDropEffects.None;
        };
        _tree.DragDrop += (_, e) =>
        {
            var n = _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));
            if (n == null || e.Data?.GetData(typeof(string[])) is not string[] ids) return;
            MoveTo(Book.Entries.Where(x => ids.Contains(x.Id)).ToList(), FolderOf(n));
        };

        FillTree(AllKey);
        AppPaths.CleanClipboardFolder();
    }

    // ---------------- Vista ----------------

    private static string FolderOf(TreeNode n) => (string)n.Tag! == NoneKey ? "" : (string)n.Tag!;

    private void FillTree(string? select)
    {
        select ??= (string?)_tree.SelectedNode?.Tag ?? AllKey;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var all = _tree.Nodes.Add($"Todos los equipos ({Book.Entries.Count})");
        all.Tag = AllKey;
        foreach (var f in Book.AllFolders())
        {
            int n = Book.Entries.Count(e => string.Equals(e.Folder, f, StringComparison.CurrentCultureIgnoreCase));
            var node = _tree.Nodes.Add($"{f} ({n})");
            node.Tag = f;
        }
        int none = Book.Entries.Count(e => e.Folder.Length == 0);
        if (none > 0) _tree.Nodes.Add($"Sin carpeta ({none})").Tag = NoneKey;
        _tree.EndUpdate();
        _tree.SelectedNode = _tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => string.Equals((string)n.Tag!, select, StringComparison.CurrentCultureIgnoreCase)) ?? all;
        FillList();
    }

    private IEnumerable<BookEntry> VisibleEntries()
    {
        var q = _search.Text.Trim();
        IEnumerable<BookEntry> items = Book.Entries;
        if (q.Length > 0)
            // Al buscar se mira en todas las carpetas
            return items.Where(e => e.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                                    e.Host.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                    e.Folder.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        var tag = (string?)_tree.SelectedNode?.Tag ?? AllKey;
        if (tag == AllKey) return items;
        if (tag == NoneKey) return items.Where(e => e.Folder.Length == 0);
        return items.Where(e => string.Equals(e.Folder, tag, StringComparison.CurrentCultureIgnoreCase));
    }

    private void FillList()
    {
        var selected = Selected().Select(e => e.Id).ToHashSet();
        IEnumerable<BookEntry> items = VisibleEntries();
        items = _sortColumn switch
        {
            1 => _sortDesc ? items.OrderByDescending(e => e.Host) : items.OrderBy(e => e.Host),
            2 => _sortDesc ? items.OrderBy(e => e.LastConnected ?? DateTime.MinValue) : items.OrderByDescending(e => e.LastConnected ?? DateTime.MinValue),
            3 => _sortDesc ? items.OrderByDescending(e => e.Folder) : items.OrderBy(e => e.Folder),
            _ => _sortDesc ? items.OrderByDescending(e => e.Name, StringComparer.CurrentCultureIgnoreCase) : items.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in items)
        {
            var it = new ListViewItem(new[]
            {
                e.Name,
                e.Host,
                e.LastConnected?.ToLocalTime().ToString("dd/MM/yyyy, HH:mm") ?? "",
                e.Folder,
            }) { Tag = e, Selected = selected.Contains(e.Id) };
            if (_connecting.Contains(e.Id)) it.ForeColor = SystemColors.GrayText;
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        _status.Text = $"{_list.Items.Count} equipo(s)";
    }

    private List<BookEntry> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (BookEntry)i.Tag!).ToList();

    private void Save(string? selectFolder = null)
    {
        _cfg.Save();
        FillTree(selectFolder);
    }

    // ---------------- Menús ----------------

    private ContextMenuStrip BuildListMenu()
    {
        var m = new ContextMenuStrip();
        var move = new ToolStripMenuItem("Mover a carpeta");
        m.Items.Add("Conectar", null, (_, _) => { foreach (var e in Selected()) Connect(e); });
        m.Items.Add("Editar…", null, (_, _) => EditSelected());
        m.Items.Add(move);
        m.Items.Add("Olvidar contraseña", null, (_, _) =>
        {
            foreach (var e in Selected()) e.ProtectedPassword = null;
            Save();
        });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Eliminar", null, (_, _) => DeleteSelected());
        m.Opening += (_, e) =>
        {
            var sel = Selected();
            if (sel.Count == 0) { e.Cancel = true; return; }
            move.DropDownItems.Clear();
            foreach (var f in Book.AllFolders()) move.DropDownItems.Add(f, null, (_, _) => MoveTo(sel, f));
            move.DropDownItems.Add(new ToolStripSeparator());
            move.DropDownItems.Add("Sin carpeta", null, (_, _) => MoveTo(sel, ""));
            move.DropDownItems.Add("Nueva carpeta…", null, (_, _) =>
            {
                using var p = new TextPrompt("Nueva carpeta", "Nombre:");
                if (p.ShowDialog(this) == DialogResult.OK) MoveTo(sel, p.Value);
            });
        };
        return m;
    }

    private ContextMenuStrip BuildTreeMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Nueva carpeta…", null, (_, _) => NewFolder());
        var rename = m.Items.Add("Renombrar…", null, (_, _) =>
        {
            var f = FolderOf(_tree.SelectedNode!);
            using var p = new TextPrompt("Renombrar carpeta", "Nombre:", f);
            if (p.ShowDialog(this) != DialogResult.OK) return;
            Book.RenameFolder(f, p.Value);
            Save(p.Value);
        });
        var del = m.Items.Add("Eliminar carpeta", null, (_, _) =>
        {
            var f = FolderOf(_tree.SelectedNode!);
            if (MessageBox.Show(this, $"¿Eliminar la carpeta «{f}»? Sus equipos no se borran: pasan a «Sin carpeta».",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Book.DeleteFolder(f);
            Save(AllKey);
        });
        m.Opening += (_, _) =>
        {
            var tag = (string?)_tree.SelectedNode?.Tag;
            rename.Enabled = del.Enabled = tag is not null and not AllKey and not NoneKey;
        };
        return m;
    }

    // ---------------- Acciones ----------------

    private void NewEntry()
    {
        var tag = (string?)_tree.SelectedNode?.Tag;
        var e = new BookEntry { Folder = tag is null or AllKey or NoneKey ? "" : tag };
        using var d = new EntryDialog(e, Book.AllFolders(), isNew: true);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        Book.Entries.Add(e);
        Save(e.Folder.Length > 0 ? e.Folder : null);
    }

    private void NewFolder()
    {
        using var p = new TextPrompt("Nueva carpeta", "Nombre:");
        if (p.ShowDialog(this) != DialogResult.OK) return;
        Book.AddFolder(p.Value);
        Save(p.Value);
    }

    private void EditSelected()
    {
        var e = Selected().FirstOrDefault();
        if (e == null) return;
        using var d = new EntryDialog(e, Book.AllFolders(), isNew: false);
        if (d.ShowDialog(this) == DialogResult.OK) Save();
    }

    private void DeleteSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var what = sel.Count == 1 ? $"«{sel[0].Name}»" : $"{sel.Count} equipos";
        if (MessageBox.Show(this, $"¿Eliminar {what} de la libreta?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        Book.Entries.RemoveAll(sel.Contains);
        Save();
    }

    private void MoveTo(List<BookEntry> entries, string folder)
    {
        foreach (var e in entries) e.Folder = folder;
        Save();
    }

    private void ImportFiles()
    {
        using var d = new OpenFileDialog { Multiselect = true, Filter = "Conexiones de RealVNC (*.vnc)|*.vnc|Todos los archivos|*.*", Title = "Importar conexiones de RealVNC" };
        if (d.ShowDialog(this) == DialogResult.OK) Import(d.FileNames);
    }

    private void ImportFolder()
    {
        using var d = new FolderBrowserDialog { Description = "Carpeta con los archivos .vnc exportados de RealVNC (se buscan también en subcarpetas)", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) == DialogResult.OK) Import(new[] { d.SelectedPath });
    }

    private void Import(string[] paths)
    {
        var errors = new List<string>();
        var items = RealVncImport.ReadPaths(paths, errors);
        var r = Book.Import(items);
        Save(AllKey);
        var msg = $"Equipos nuevos: {r.Added}\nActualizados: {r.Updated}" + (r.Skipped > 0 ? $"\nSin dirección (omitidos): {r.Skipped}" : "");
        if (errors.Count > 0) msg += "\n\nNo se pudieron leer:\n" + string.Join("\n", errors.Take(15)) + (errors.Count > 15 ? "\n…" : "");
        MessageBox.Show(this, msg, "Importar de RealVNC", MessageBoxButtons.OK, r.Added + r.Updated > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    // ---------------- Conexión ----------------

    private void GoFromSearch()
    {
        var text = _search.Text.Trim();
        if (text.Length == 0)
        {
            foreach (var e in Selected()) Connect(e);
            return;
        }
        // Si coincide exactamente con un equipo guardado, se usa ese
        var match = Book.Entries.FirstOrDefault(e => string.Equals(e.Host, text, StringComparison.OrdinalIgnoreCase) ||
                                                    string.Equals(e.Name, text, StringComparison.CurrentCultureIgnoreCase));
        var visible = VisibleEntries().ToList();
        if (match == null && visible.Count == 1 && !ClientConfig.TryParseHost(text, out _, out _)) match = visible[0];
        if (match != null) { Connect(match); return; }

        if (!ClientConfig.TryParseHost(text, out _, out _))
        {
            _status.Text = "Dirección no válida";
            return;
        }
        Connect(new BookEntry { Name = text, Host = text }, adHoc: true);
    }

    private async void Connect(BookEntry entry, bool adHoc = false)
    {
        if (!_connecting.Add(entry.Id)) return;
        FillList();
        try
        {
            if (!ClientConfig.TryParseHost(entry.Host, out var host, out var port))
            {
                MessageBox.Show(this, $"La dirección de «{entry.Name}» no es válida.", Text);
                return;
            }

            string? password = Secret.Unprotect(entry.ProtectedPassword);
            bool savePassword = false;
            string? error = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (password == null)
                {
                    using var d = new PasswordDialog(entry.Name, error, allowSave: true);
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    password = d.Password;
                    savePassword = d.SavePassword;
                }

                _status.Text = $"Conectando con {entry.Name} ({entry.Host})…";
                RfbClient client;
                try
                {
                    client = await RfbClient.ConnectAsync(host, port, password);
                }
                catch (RfbAuthException ex)
                {
                    // Contraseña mala (también si era la guardada): volver a pedirla
                    error = ex.Message;
                    password = null;
                    if (entry.ProtectedPassword != null) { entry.ProtectedPassword = null; _cfg.Save(); }
                    continue;
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or RfbException or EndOfStreamException or OperationCanceledException)
                {
                    _status.Text = "";
                    var msg = ex switch
                    {
                        OperationCanceledException => "No responde (tiempo agotado). ¿Está encendido y con el servidor en marcha?",
                        System.Net.Sockets.SocketException se => $"No se pudo conectar: {se.Message}",
                        _ => ex.Message,
                    };
                    MessageBox.Show(this, msg, $"{entry.Name} — no se pudo conectar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Conectado
                entry.LastConnected = DateTime.UtcNow;
                if (savePassword) entry.ProtectedPassword = Secret.Protect(password);
                if (adHoc && savePassword)
                {
                    // Dirección escrita a mano con contraseña guardada: se añade a la libreta
                    Book.Entries.Add(entry);
                }
                _cfg.Save();
                FillTree(null);
                _status.Text = $"Conectado con {entry.Name}";
                OpenViewer(client, entry);
                return;
            }
        }
        finally
        {
            _connecting.Remove(entry.Id);
            FillList();
        }
    }

    private void OpenViewer(RfbClient client, BookEntry entry)
    {
        var viewer = new ViewerForm(client, _cfg, entry.Name);
        viewer.FormClosed += (_, _) =>
        {
            if (viewer.DisconnectError != null)
                MessageBox.Show(this, $"Se perdió la conexión con {entry.Name}:\n{viewer.DisconnectError}", "Enlace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            viewer.Dispose();
        };
        _viewers.Add(viewer);
        viewer.FormClosed += (_, _) => _viewers.Remove(viewer);
        viewer.Show();
    }

    private readonly List<ViewerForm> _viewers = new();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_viewers.Count > 0 && e.CloseReason == CloseReason.UserClosing &&
            MessageBox.Show(this, $"Hay {_viewers.Count} conexión(es) abierta(s). ¿Cerrarlas y salir?", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            e.Cancel = true;
            return;
        }
        foreach (var v in _viewers.ToList()) v.Close();
        base.OnFormClosing(e);
    }
}
