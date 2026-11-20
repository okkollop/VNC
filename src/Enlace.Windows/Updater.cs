using System.Diagnostics;
using System.Net.Http;
using Enlace.Protocol;

namespace Enlace.Windows;

public enum AppKind { Servidor, Visor }

/// <summary>
/// Actualizaciones automáticas desde el servidor propio (soporte.infollop.net).
/// Descarga version.json, comprueba la firma, descarga el .exe, comprueba su huella
/// y sustituye el ejecutable en uso (Windows permite renombrar un .exe que se está ejecutando).
/// </summary>
public static class Updater
{
    public const string DefaultBaseUrl = "https://soporte.infollop.net/infollopassist/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static Updater()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"InfollopAssist/{Brand.VersionText}");
    }

    public static string BaseUrl { get; set; } = DefaultBaseUrl;

    private static Uri Url(string file) => new(new Uri(BaseUrl.EndsWith('/') ? BaseUrl : BaseUrl + "/"), file);

    /// <summary>Devuelve la versión publicada si es más nueva que la actual y su firma es válida; si no, null.</summary>
    public static async Task<UpdateManifest?> CheckAsync(CancellationToken ct = default)
    {
        string json;
        try { json = await Http.GetStringAsync(Url("version.json"), ct).ConfigureAwait(false); }
        // Sin version.json en el servidor = aún no hay ninguna versión publicada: no es un error.
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        var m = UpdateManifest.Parse(json);
        if (!m.VerifySignature()) throw new InvalidDataException("La firma de la actualización no es válida: se ignora.");
        return m.IsNewerThan(Brand.Version) ? m : null;
    }

    /// <summary>Descarga y comprueba el ejecutable; devuelve la ruta del archivo listo para instalar.</summary>
    public static async Task<string> DownloadAsync(UpdateManifest m, AppKind kind, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var f = kind == AppKind.Servidor ? m.Servidor : m.Visor;
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Ruta del programa desconocida");
        var target = exe + ".new";
        using (var resp = await Http.GetAsync(Url(f.File), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buf = new byte[1 << 16];
            long done = 0;
            int n, last = -1;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (done > f.Size + 1024) throw new InvalidDataException("La descarga es mayor de lo esperado.");
                int pct = f.Size > 0 ? (int)(done * 100 / f.Size) : 0;
                if (pct != last) { last = pct; progress?.Report(pct); }
            }
        }
        if (!UpdateManifest.FileMatches(target, f))
        {
            TryDelete(target);
            throw new InvalidDataException("El archivo descargado no coincide con el publicado (huella distinta).");
        }
        return target;
    }

    /// <summary>
    /// Sustituye el ejecutable actual por el descargado. No reinicia: el que llama decide cómo.
    /// </summary>
    public static void Install(string downloaded)
    {
        var exe = Environment.ProcessPath!;
        var old = exe + ".old";
        TryDelete(old);
        File.Move(exe, old);                 // permitido aunque esté en ejecución
        try { File.Move(downloaded, exe); }
        catch
        {
            File.Move(old, exe);             // deshacer
            throw;
        }
    }

    /// <summary>Arranca de nuevo el programa (ya actualizado) con los mismos argumentos y cierra este.</summary>
    public static void Restart(string? args = null)
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args ?? string.Join(' ', Environment.GetCommandLineArgs().Skip(1)))
        {
            UseShellExecute = false,
        });
        Environment.Exit(0);
    }

    /// <summary>Al arrancar: borra restos de una actualización anterior.</summary>
    public static void CleanupOld()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        TryDelete(exe + ".old");
        TryDelete(exe + ".new");
    }

    /// <summary>Versión del archivo .exe en disco (puede ser más nueva que la que está en ejecución).</summary>
    public static Version? DiskVersion()
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
        catch { return null; }
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }

    /// <summary>Comprobación interactiva (menú "Buscar actualizaciones"): pregunta e instala.</summary>
    public static async Task CheckInteractiveAsync(IWin32Window? owner, AppKind kind, bool quietIfNone = false, Action? beforeRestart = null)
    {
        UpdateManifest? m;
        try { m = await CheckAsync(); }
        catch (Exception ex)
        {
            if (!quietIfNone)
                MessageBox.Show(owner, "No se pudo comprobar si hay actualizaciones:\n" + ex.Message, Brand.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (m == null)
        {
            if (!quietIfNone)
                MessageBox.Show(owner, $"Tienes la última versión ({Brand.VersionText}).", Brand.Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var msg = $"Hay una versión nueva de {Brand.Product}: {m.Version} (tienes la {Brand.VersionText})." +
                  (m.Notes.Length > 0 ? $"\n\n{m.Notes}" : "") + "\n\n¿Actualizar ahora? El programa se reiniciará.";
        if (MessageBox.Show(owner, msg, Brand.Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            // En modo servicio el servicio puede haberse actualizado ya: el .exe en disco es
            // el nuevo y este proceso sigue corriendo desde el .old. Basta con reiniciar.
            if (DiskVersion() is not { } onDisk || m.IsNewerThan(onDisk))
            {
                var file = await DownloadAsync(m, kind);
                Install(file);
            }
            beforeRestart?.Invoke();
            Restart();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "No se pudo actualizar:\n" + ex.Message, Brand.Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
