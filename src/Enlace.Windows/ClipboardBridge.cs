using System.Collections.Specialized;
using System.Runtime.InteropServices;

namespace Enlace.Windows;

/// <summary>
/// Vigila el portapapeles local y aplica el remoto sin crear bucles de eco.
/// Debe crearse en el hilo de interfaz (STA).
/// </summary>
public sealed class ClipboardBridge : NativeWindow, IDisposable
{
    private readonly SynchronizationContext _ui;
    private uint _ownSeq;
    private string? _lastText;
    private string? _lastFilesKey;

    public ClipboardBridge()
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Crear en el hilo de interfaz");
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // ventana sólo-mensajes
        Native.AddClipboardFormatListener(Handle);
    }

    /// <summary>El usuario ha copiado texto en este equipo.</summary>
    public event Action<string>? LocalText;

    /// <summary>El usuario ha copiado archivos/carpetas en este equipo.</summary>
    public event Action<string[]>? LocalFiles;

    public bool Enabled { get; set; } = true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE) OnChanged();
        base.WndProc(ref m);
    }

    private void OnChanged()
    {
        if (!Enabled) return;
        if (Native.GetClipboardSequenceNumber() == _ownSeq) return;
        var (text, files) = Read();
        if (files is { Length: > 0 })
        {
            // Sólo se descarta si son los mismos archivos que acabamos de recibir del otro equipo
            if (string.Join("|", files) == _lastFilesKey) return;
            _lastFilesKey = null;
            _lastText = null;
            LocalFiles?.Invoke(files);
        }
        else if (text != null)
        {
            if (text == _lastText) return;
            _lastText = text;
            _lastFilesKey = null;
            LocalText?.Invoke(text);
        }
    }

    private static (string? Text, string[]? Files) Read()
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var list = Clipboard.GetFileDropList();
                    return (null, list.Cast<string>().ToArray());
                }
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText)) return (Clipboard.GetText(TextDataFormat.UnicodeText), null);
                return (null, null);
            }
            catch (ExternalException) { Thread.Sleep(30); } // otra aplicación lo tiene abierto
        }
        return (null, null);
    }

    private void Retry(Action set)
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                set();
                _ownSeq = Native.GetClipboardSequenceNumber();
                return;
            }
            catch (ExternalException) { Thread.Sleep(30); }
        }
    }

    /// <summary>Pone en el portapapeles local texto recibido del otro equipo. Seguro desde cualquier hilo.</summary>
    public void SetRemoteText(string text) => _ui.Post(_ =>
    {
        if (!Enabled || text.Length == 0) return;
        _lastText = text;
        _lastFilesKey = null;
        Retry(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true));
    }, null);

    /// <summary>Pone archivos recibidos en el portapapeles local para poder pegarlos. Seguro desde cualquier hilo.</summary>
    public void SetRemoteFiles(IReadOnlyList<string> paths) => _ui.Post(_ =>
    {
        if (!Enabled || paths.Count == 0) return;
        var col = new StringCollection();
        col.AddRange(paths.ToArray());
        _lastFilesKey = string.Join("|", paths);
        _lastText = null;
        var data = new DataObject();
        data.SetFileDropList(col);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // copiar
        Retry(() => Clipboard.SetDataObject(data, true));
    }, null);

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            Native.RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}
