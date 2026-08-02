using System.Diagnostics.CodeAnalysis;
using SubathonManager.Data.Widgets;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

[Collection("WorkingDirectory")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments")]
public class WidgetCollectionInstallerTests
{
    private static KeyValuePair<string, byte[]> SmwEntry(string entryName, string packId, string version)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"smwc-src-{Guid.NewGuid():N}.smw");
        TestPacks.WriteSmw(temp, TestPacks.WidgetManifestJson(
            packId: packId, version: version, entry: "content/widget.html"),
            new Dictionary<string, string> { ["content/widget.html"] = "<html></html>" });

        var bytes = File.ReadAllBytes(temp);
        try { File.Delete(temp); } catch { /**/ }
        return new KeyValuePair<string, byte[]>(entryName, bytes);
    }

    #region ReadManifest

    [Fact]
    public void ReadManifest_MissingFile_ReturnsNull()
    {
        using var ws = new TempWorkspace("collection");
        Assert.Null(WidgetCollectionInstaller.ReadManifest(Path.Combine(ws.Root, "nope.smwc")));
    }

    [Fact]
    public void ReadManifest_NotAZip_ReturnsNull()
    {
        using var ws = new TempWorkspace("collection");
        Assert.Null(WidgetCollectionInstaller.ReadManifest(ws.WriteFile("bad.smwc", "nope")));
    }

    [Fact]
    public void ReadManifest_NoCollectionJson_ReturnsNull()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("nomanifest.smwc"),
            new Dictionary<string, string> { ["a.smw"] = "x" });

        Assert.Null(WidgetCollectionInstaller.ReadManifest(path));
    }

    [Fact]
    public void ReadManifest_MalformedJson_ReturnsNull()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("bad.smwc"),
            new Dictionary<string, string> { ["collection.json"] = "{{{" });

        Assert.Null(WidgetCollectionInstaller.ReadManifest(path));
    }

    [Fact]
    public void ReadManifest_FullManifest_MapsEveryField()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("full.smwc"), new Dictionary<string, string>
        {
            ["collection.json"] = TestPacks.CollectionManifestJson(
                name: "Stream Kit", author: "Wolf", version: "3.1.0",
                description: "Everything", tags: ["kit", "stream"])
        });

        var manifest = WidgetCollectionInstaller.ReadManifest(path);

        Assert.NotNull(manifest);
        Assert.Equal("1", manifest!.FormatVersion);
        Assert.Equal("9.9.9", manifest.AppVersion);
        Assert.Equal("Stream Kit", manifest.Name);
        Assert.Equal("Wolf", manifest.Author);
        Assert.Equal("3.1.0", manifest.Version);
        Assert.Equal("Everything", manifest.Description);
        Assert.Equal(new[] {"kit", "stream"}, manifest.Tags);
    }

    [Fact]
    public void ReadManifest_EmptyObject_UsesFallbacks()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("MyKit.smwc"),
            new Dictionary<string, string> { ["collection.json"] = "{}" });

        var manifest = WidgetCollectionInstaller.ReadManifest(path);

        Assert.NotNull(manifest);
        Assert.Equal("MyKit", manifest!.Name);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal(string.Empty, manifest.Author);
        Assert.Empty(manifest.Tags);
    }

    [Fact]
    public void ReadManifest_BlankNameAndVersion_UseFallbacks()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("Fallbacks.smwc"), new Dictionary<string, string>
        {
            ["collection.json"] = """{ "collection": { "name": "   ", "version": "  " } }"""
        });

        var manifest = WidgetCollectionInstaller.ReadManifest(path)!;

        Assert.Equal("Fallbacks", manifest.Name);
        Assert.Equal("1.0.0", manifest.Version);
    }

    [Fact]
    public void ReadManifest_TagsArray_DropsNonStringsAndBlanks()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("t.smwc"), new Dictionary<string, string>
        {
            ["collection.json"] = """{ "collection": { "tags": ["a", 1, "", "  ", "b"] } }"""
        });

        Assert.Equal(new[] {"a", "b"}, WidgetCollectionInstaller.ReadManifest(path)!.Tags);
    }

    #endregion

    #region InstallAll

    [Fact]
    public void InstallAll_MissingFile_ReturnsNull()
    {
        using var ws = new TempWorkspace("collection");
        Assert.Null(WidgetCollectionInstaller.InstallAll(Path.Combine(ws.Root, "nope.smwc")));
    }

    [Fact]
    public void InstallAll_NotAZip_ReturnsNull()
    {
        using var ws = new TempWorkspace("collection");
        Assert.Null(WidgetCollectionInstaller.InstallAll(ws.WriteFile("bad.smwc", "nope")));
    }

    [Fact]
    public void InstallAll_NoPacksInside_ReturnsNull()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("empty.smwc"), new Dictionary<string, string>
        {
            ["collection.json"] = TestPacks.CollectionManifestJson(),
            ["readme.txt"] = "no packs here"
        });

        Assert.Null(WidgetCollectionInstaller.InstallAll(path));
    }

    [Fact]
    public void InstallAll_InstallsEveryPack()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("kit.smwc"), new List<KeyValuePair<string, byte[]>>
        {
            new("collection.json",
                System.Text.Encoding.UTF8.GetBytes(TestPacks.CollectionManifestJson(name: "Kit"))),
            SmwEntry("packs/timer.smw", "wolf.widgets.timer", "1.0.0"),
            SmwEntry("packs/alerts.smw", "wolf.widgets.alerts", "2.0.0")
        });

        var result = WidgetCollectionInstaller.InstallAll(path);

        Assert.NotNull(result);
        Assert.Equal("Kit", result!.Manifest!.Name);
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, result.Packs.Count);
        Assert.All(result.Packs, p => Assert.True(File.Exists(p.PackFile)));
        Assert.Contains(result.Packs, p => p.Manifest.PackId == "wolf.widgets.timer");
        Assert.Contains(result.Packs, p => p.Manifest.PackId == "wolf.widgets.alerts");
    }

    [Fact]
    public void InstallAll_CountsBrokenPacksAsFailures()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("kit.smwc"), new List<KeyValuePair<string, byte[]>>
        {
            new("collection.json",
                System.Text.Encoding.UTF8.GetBytes(TestPacks.CollectionManifestJson())),
            SmwEntry("packs/good.smw", "wolf.widgets.timer", "1.0.0"),
            new("packs/broken.smw", System.Text.Encoding.UTF8.GetBytes("not a zip"))
        });

        var result = WidgetCollectionInstaller.InstallAll(path);

        Assert.NotNull(result);
        Assert.Single(result!.Packs);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void InstallAll_WithoutManifest_StillInstallsPacks()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("nomanifest.smwc"),
            new List<KeyValuePair<string, byte[]>>
        {
            SmwEntry("timer.smw", "wolf.widgets.timer", "1.0.0")
        });

        var result = WidgetCollectionInstaller.InstallAll(path);

        Assert.NotNull(result);
        Assert.Null(result!.Manifest);
        Assert.Single(result.Packs);
    }

    [Fact]
    public void InstallAll_IgnoresNonSmwEntries()
    {
        using var ws = new TempWorkspace("collection");
        string path = TestPacks.WriteZip(ws.Path_("kit.smwc"), new List<KeyValuePair<string, byte[]>>
        {
            new("collection.json",
                System.Text.Encoding.UTF8.GetBytes(TestPacks.CollectionManifestJson())),
            new("preview.png", [0x89, 0x50]),
            new("docs/readme.md", System.Text.Encoding.UTF8.GetBytes("# hi")),
            SmwEntry("timer.smw", "wolf.widgets.timer", "1.0.0")
        });

        var result = WidgetCollectionInstaller.InstallAll(path)!;

        Assert.Single(result.Packs);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void InstallAll_LeavesNoScratchFolderBehind()
    {
        using var ws = new TempWorkspace("collection");
        var before = Directory.EnumerateDirectories(Path.GetTempPath(), "smwc-*").ToHashSet();

        string path = TestPacks.WriteZip(ws.Path_("kit.smwc"), new List<KeyValuePair<string, byte[]>>
        {
            SmwEntry("timer.smw", "wolf.widgets.timer", "1.0.0")
        });
        WidgetCollectionInstaller.InstallAll(path);

        var after = Directory.EnumerateDirectories(Path.GetTempPath(), "smwc-*").ToHashSet();
        Assert.Empty(after.Except(before));
    }

    #endregion

    #region constants

    [Fact]
    public void Constants()
    {
        Assert.Equal(".smwc", WidgetCollectionInstaller.CollectionExtension);
        Assert.Equal("collection.json", WidgetCollectionInstaller.ManifestFileName);
    }

    #endregion
}
