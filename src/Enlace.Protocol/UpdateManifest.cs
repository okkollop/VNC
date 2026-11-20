using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enlace.Protocol;

public sealed class UpdateFile
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>
/// Descriptor de la última versión publicada (version.json en el servidor de actualizaciones).
/// Va firmado con ECDSA P-256: sólo quien tiene la clave privada puede publicar versiones
/// que los programas acepten, aunque alguien consiga escribir en la web.
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>Clave pública (SubjectPublicKeyInfo, base64) con la que se comprueban las firmas.</summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEa/flRlIQL7JXdKqcF/H/j68NCEAxdHsuasx/vlWADQyBsorMzcENLHcM15H2yxPtQIiFa10wEFNJvFa7uUI9Fg==";

    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("servidor")] public UpdateFile Servidor { get; set; } = new();
    [JsonPropertyName("visor")] public UpdateFile Visor { get; set; } = new();
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";

    /// <summary>Texto exacto que se firma (versión, notas y huellas de los dos programas).</summary>
    public byte[] SignedBytes() => Encoding.UTF8.GetBytes(
        $"InfollopAssist|{Version}|{Date}|{Notes}|servidor:{Servidor.File}:{Servidor.Sha256.ToLowerInvariant()}:{Servidor.Size}" +
        $"|visor:{Visor.File}:{Visor.Sha256.ToLowerInvariant()}:{Visor.Size}");

    public static UpdateManifest Parse(string json) =>
        JsonSerializer.Deserialize<UpdateManifest>(json) ?? throw new FormatException("version.json vacío");

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public void Sign(string privateKeyPkcs8Base64)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
        Signature = Convert.ToBase64String(ec.SignData(SignedBytes(), HashAlgorithmName.SHA256));
    }

    public bool VerifySignature(string publicKeyBase64 = PublicKey)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ec.VerifyData(SignedBytes(), Convert.FromBase64String(Signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public System.Version? ParsedVersion => System.Version.TryParse(Version, out var v) ? v : null;

    public bool IsNewerThan(System.Version current)
    {
        var v = ParsedVersion;
        if (v == null) return false;
        static System.Version N(System.Version x) => new(x.Major, x.Minor, Math.Max(0, x.Build));
        return N(v) > N(current);
    }

    public static string Sha256Of(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>Comprueba que el archivo descargado es exactamente el publicado.</summary>
    public static bool FileMatches(string path, UpdateFile expected) =>
        new FileInfo(path).Length == expected.Size &&
        string.Equals(Sha256Of(path), expected.Sha256, StringComparison.OrdinalIgnoreCase);
}
