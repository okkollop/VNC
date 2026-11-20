using Enlace.Protocol;

// Uso: Firmar <carpeta con los .exe> <versión> [notas]
// La clave privada se lee de la variable de entorno INFOLLOP_CLAVE (base64 PKCS#8). Nunca va al repositorio.
if (args.Length < 2)
{
    Console.Error.WriteLine("Uso: Firmar <carpeta> <versión> [notas]");
    return 2;
}
var dir = args[0];
var key = Environment.GetEnvironmentVariable("INFOLLOP_CLAVE");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("Falta la variable INFOLLOP_CLAVE con la clave privada.");
    return 3;
}

UpdateFile Describe(string name)
{
    var p = Path.Combine(dir, name);
    return new UpdateFile { File = name, Sha256 = UpdateManifest.Sha256Of(p), Size = new FileInfo(p).Length };
}

var m = new UpdateManifest
{
    Version = args[1].TrimStart('v'),
    Date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
    Notes = args.Length > 2 ? args[2] : "",
    Servidor = Describe("InfollopAssistServidor.exe"),
    Visor = Describe("InfollopAssistVisor.exe"),
};
m.Sign(key.Trim());
if (!m.VerifySignature())
{
    Console.Error.WriteLine("La clave privada no corresponde a la clave pública de la aplicación.");
    return 4;
}
File.WriteAllText(Path.Combine(dir, "version.json"), m.ToJson());
Console.WriteLine($"version.json firmado para la versión {m.Version}");
return 0;
