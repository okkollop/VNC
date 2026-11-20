using Enlace.Protocol;

namespace Enlace.Protocol.Tests;

public class BookTests
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "Data");

    [Fact]
    public void ParsesRealVncExport()
    {
        var e = RealVncImport.Parse(File.ReadAllText(Path.Combine(Data, "ejemplo2.vnc")))!;
        Assert.Equal("Equipo2", e.Name);
        Assert.Equal("10.0.0.146", e.Host);
        Assert.Equal("CARPETA1", e.Folder);
        Assert.Equal(new DateTime(2026, 6, 4, 14, 55, 53, 272, DateTimeKind.Utc), e.LastConnected);
        Assert.Equal("ca883c7f-0a36-43ba-92ec-7598ddef2b67", e.Uuid);
    }

    [Fact]
    public void ImportsFolderWithoutDuplicates()
    {
        var items = RealVncImport.ReadPaths(new[] { Data });
        Assert.Equal(5, items.Count);
        Assert.Contains(items, i => i.Name == "Equipo3" && i.LastConnected == null);

        var book = new AddressBook();
        Assert.Equal(new ImportResult(5, 0, 0), book.Import(items));
        Assert.Equal(new[] { "CARPETA0", "CARPETA1", "CARPETA2" }, book.AllFolders());

        // Reimportar actualiza en lugar de duplicar
        var original = book.Entries[0].Folder;
        book.Entries[0].Folder = "otra";
        Assert.Equal(new ImportResult(0, 5, 0), book.Import(items));
        Assert.Equal(5, book.Entries.Count);
        Assert.Equal(original, book.Entries[0].Folder);
    }

    [Fact]
    public void FolderOperations()
    {
        var book = new AddressBook();
        book.Entries.Add(new BookEntry { Name = "a", Host = "1.1.1.1", Folder = "Oficina" });
        book.AddFolder("Vacía");
        Assert.Equal(new[] { "Oficina", "Vacía" }, book.AllFolders());
        book.RenameFolder("oficina", "Sede");
        Assert.Equal("Sede", book.Entries[0].Folder);
        book.DeleteFolder("Sede");
        Assert.Equal("", book.Entries[0].Folder);
        Assert.Equal(new[] { "Vacía" }, book.AllFolders());
    }

    [Fact]
    public void SkipsFileWithoutHost() => Assert.Null(RealVncImport.Parse("FriendlyName=x\nLabels=y"));
}

public class HostAddressTests
{
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20", 6900)]
    [InlineData(" pc-oficina ", "pc-oficina", 6900)]
    [InlineData("192.168.1.20:1", "192.168.1.20", 5901)]
    [InlineData("192.168.1.20:5905", "192.168.1.20", 5905)]
    [InlineData("192.168.1.20::5999", "192.168.1.20", 5999)]
    [InlineData("pc::5999", "pc", 5999)]
    [InlineData("[fe80::1]:6000", "fe80::1", 6000)]
    [InlineData("[fe80::1]", "fe80::1", 6900)]
    [InlineData("fe80::1", "fe80::1", 6900)]
    [InlineData("equipo.ejemplo.com", "equipo.ejemplo.com", 6900)]
    public void Parses(string input, string host, int port)
    {
        Assert.True(HostAddress.TryParse(input, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pc:abc")]
    [InlineData("[fe80::1")]
    public void Rejects(string input) => Assert.False(HostAddress.TryParse(input, out _, out _));
}
