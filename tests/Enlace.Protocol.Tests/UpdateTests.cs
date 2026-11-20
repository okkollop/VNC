using System.Security.Cryptography;
using Enlace.Protocol;

namespace Enlace.Protocol.Tests;

public class UpdateTests
{
    private static (string Priv, string Pub) NewKeys()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ec.ExportPkcs8PrivateKey()), Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
    }

    private static UpdateManifest Sample() => new()
    {
        Version = "1.2.0", Date = "2026-11-20", Notes = "Mejoras",
        Servidor = new UpdateFile { File = "InfollopAssistServidor.exe", Sha256 = "aa", Size = 10 },
        Visor = new UpdateFile { File = "InfollopAssistVisor.exe", Sha256 = "bb", Size = 20 },
    };

    [Fact]
    public void SignedManifestVerifiesAndSurvivesJson()
    {
        var (priv, pub) = NewKeys();
        var m = Sample();
        m.Sign(priv);
        var back = UpdateManifest.Parse(m.ToJson());
        Assert.True(back.VerifySignature(pub));
    }

    [Fact]
    public void TamperingBreaksSignature()
    {
        var (priv, pub) = NewKeys();
        var m = Sample();
        m.Sign(priv);
        m.Servidor.Sha256 = "cc";
        Assert.False(m.VerifySignature(pub));
        var other = NewKeys();
        var m2 = Sample();
        m2.Sign(other.Priv);
        Assert.False(m2.VerifySignature(pub));           // firmado con otra clave
        Assert.False(Sample().VerifySignature(pub));     // sin firma
        Assert.False(m2.VerifySignature());              // clave pública real de la aplicación
    }

    [Fact]
    public void VersionComparison()
    {
        var m = Sample();
        Assert.True(m.IsNewerThan(new Version(1, 1, 9)));
        Assert.False(m.IsNewerThan(new Version(1, 2, 0, 0)));
        Assert.False(m.IsNewerThan(new Version(1, 3, 0)));
    }

    [Fact]
    public void FileHash()
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, "hola");
        var uf = new UpdateFile { Sha256 = UpdateManifest.Sha256Of(f), Size = 4 };
        Assert.True(UpdateManifest.FileMatches(f, uf));
        File.WriteAllText(f, "hola!");
        Assert.False(UpdateManifest.FileMatches(f, uf));
        File.Delete(f);
    }
}
