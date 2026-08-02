using System.IO.Compression;
using SubathonManager.Data.Overlays;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

[Collection("WorkingDirectory")]
public class OverlayPackInstallerTests
{
    [Fact]
    public void ReadManifest_MissingFile_ReturnsNull()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        Assert.Null(OverlayPackInstaller.ReadManifest(Path.Combine(ws.Root, "nope.smo")));
    }

    [Fact]
    public void ReadManifest_NotAZip_ReturnsNull()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        Assert.Null(OverlayPackInstaller.ReadManifest(ws.WriteFile("bad.smo", "nope")));
    }

    [Fact]
    public void ReadManifest_NoOverlayJson_ReturnsNull()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string path = TestPacks.WriteZip(ws.Path_("nomanifest.smo"),
            new Dictionary<string, string> { ["widgets/a/widget.html"] = "<html></html>" });

        Assert.Null(OverlayPackInstaller.ReadManifest(path));
    }

    [Fact]
    public void ReadManifest_MalformedJson_ReturnsNull()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string path = TestPacks.WriteZip(ws.Path_("bad.smo"),
            new Dictionary<string, string> { ["overlay.json"] = "}" });

        Assert.Null(OverlayPackInstaller.ReadManifest(path));
    }

    [Fact]
    public void ReadManifest_FullManifest_MapsEveryField()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string path = TestPacks.WriteSmo(ws.Path_("full.smo"), TestPacks.OverlayManifestJson(
            name: "Main Overlay", author: "Wolf", overlayVersion: "2.3.0", tags: ["main", "1080p"]));

        var manifest = OverlayPackInstaller.ReadManifest(path);

        Assert.NotNull(manifest);
        Assert.Equal("1", manifest!.FormatVersion);
        Assert.Equal("9.9.9", manifest.AppVersion);
        Assert.Equal("Main Overlay", manifest.Name);
        Assert.Equal("Wolf", manifest.Author);
        Assert.Equal("2.3.0", manifest.Version);
        Assert.Equal(new[] {"main", "1080p"}, manifest.Tags);
    }

    [Fact]
    public void ReadManifest_NoOverlayVersion_FallsBackToRootVersion()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string path = TestPacks.WriteSmo(ws.Path_("v.smo"),
            TestPacks.OverlayManifestJson(overlayVersion: null, rootVersion: "4.5.6"));

        Assert.Equal("4.5.6", OverlayPackInstaller.ReadManifest(path)!.Version);
    }

    [Fact]
    public void ReadManifest_NoVersionAnywhere_FallsBackTo100()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string path = TestPacks.WriteSmo(ws.Path_("v.smo"),
            TestPacks.OverlayManifestJson(overlayVersion: null));

        Assert.Equal("1.0.0", OverlayPackInstaller.ReadManifest(path)!.Version);
    }

    [Fact]
    public void ReadManifest_BlankName_FallsBackToFileName()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string path = TestPacks.WriteSmo(ws.Path_("MyLayout.smo"),
            TestPacks.OverlayManifestJson(name: "   "));

        Assert.Equal("MyLayout", OverlayPackInstaller.ReadManifest(path)!.Name);
    }

    [Fact]
    public void ReadManifest_NoRouteObject_UsesFallbacks()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string path = TestPacks.WriteZip(ws.Path_("Bare.smo"),
            new Dictionary<string, string> { ["overlay.json"] = "{}" });

        var manifest = OverlayPackInstaller.ReadManifest(path)!;

        Assert.Equal("Bare", manifest.Name);
        Assert.Equal(string.Empty, manifest.Author);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Empty(manifest.Tags);
        Assert.Equal(string.Empty, manifest.FormatVersion);
    }

    [Fact]
    public void ReadManifest_TagsArray_DropsNonStringsAndBlanks()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string path = TestPacks.WriteZip(ws.Path_("t.smo"), new Dictionary<string, string>
        {
            ["overlay.json"] = """{ "route": { "tags": ["a", 2, "  ", "b"] } }"""
        });

        Assert.Equal(new[] {"a", "b"}, OverlayPackInstaller.ReadManifest(path)!.Tags);
    }

    [Fact]
    public void Install_MissingFile_ReturnsNull()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        Assert.Null(OverlayPackInstaller.Install(Path.Combine(ws.Root, "nope.smo")));
    }

    [Fact]
    public void Install_NoManifest_ReturnsNull()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string path = TestPacks.WriteZip(ws.Path_("nomanifest.smo"),
            new Dictionary<string, string> { ["a.txt"] = "x" });

        Assert.Null(OverlayPackInstaller.Install(path));
    }

    [Fact]
    public void Install_CopiesArchiveAndExtractsIt()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string source = TestPacks.WriteSmo(ws.Path_("downloads", "layout.smo"),
            TestPacks.OverlayManifestJson(name: "Main Overlay", author: "Wolf", overlayVersion: "1.2.0"),
            new Dictionary<string, string>
            {
                ["widgets/abc/widget.html"] = "<html>hi</html>",
                ["widgets/abc/style.css"] = "body{}"
            });

        var installed = OverlayPackInstaller.Install(source);

        Assert.NotNull(installed);
        Assert.Equal(OverlayPackPaths.ArchiveFile("Wolf", "Main Overlay", "1.2.0"), installed!.ArchiveFile);
        Assert.Equal(OverlayPackPaths.UnpackDir("Wolf", "Main Overlay"), installed.UnpackDir);
        Assert.True(File.Exists(installed.ArchiveFile));
        Assert.Equal("<html>hi</html>",
            File.ReadAllText(Path.Combine(installed.UnpackDir, "widgets", "abc", "widget.html")));
        Assert.True(File.Exists(Path.Combine(installed.UnpackDir, "widgets", "abc", "style.css")));
        Assert.True(File.Exists(Path.Combine(installed.UnpackDir, "overlay.json")));
    }

    [Fact]
    public void Install_ReturnsTheParsedManifest()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string source = TestPacks.WriteSmo(ws.Path_("layout.smo"),
            TestPacks.OverlayManifestJson(name: "Main", author: "Wolf", overlayVersion: "1.0.0"));

        var installed = OverlayPackInstaller.Install(source)!;

        Assert.Equal("Main", installed.Manifest.Name);
        Assert.Equal("Wolf", installed.Manifest.Author);
        Assert.Equal("1.0.0", installed.Manifest.Version);
    }

    [Fact]
    public void Install_SourceAlreadyAtTarget_DoesNotSelfCopy()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string target = OverlayPackPaths.ArchiveFile("Wolf", "Main", "1.0.0");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        TestPacks.WriteSmo(target,
            TestPacks.OverlayManifestJson(name: "Main", author: "Wolf", overlayVersion: "1.0.0"),
            new Dictionary<string, string> { ["a.txt"] = "kept" });

        var installed = OverlayPackInstaller.Install(target);

        Assert.NotNull(installed);
        Assert.True(File.Exists(target));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(installed!.UnpackDir, "a.txt")));
    }

    [Fact]
    public void Install_Reinstall_OverwritesExtractedFiles()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string first = TestPacks.WriteSmo(ws.Path_("v1", "layout.smo"),
            TestPacks.OverlayManifestJson(name: "Main", author: "Wolf", overlayVersion: "1.0.0"),
            new Dictionary<string, string> { ["a.txt"] = "old" });
        OverlayPackInstaller.Install(first);

        string second = TestPacks.WriteSmo(ws.Path_("v2", "layout.smo"),
            TestPacks.OverlayManifestJson(name: "Main", author: "Wolf", overlayVersion: "1.0.0"),
            new Dictionary<string, string> { ["a.txt"] = "new" });
        var installed = OverlayPackInstaller.Install(second)!;

        Assert.Equal("new", File.ReadAllText(Path.Combine(installed.UnpackDir, "a.txt")));
    }

    [Fact]
    public void Install_DifferentVersions_ShareTheUnpackDirButKeepSeparateArchives()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string v1 = TestPacks.WriteSmo(ws.Path_("v1.smo"),
            TestPacks.OverlayManifestJson(name: "Main", author: "Wolf", overlayVersion: "1.0.0"));
        string v2 = TestPacks.WriteSmo(ws.Path_("v2.smo"),
            TestPacks.OverlayManifestJson(name: "Main", author: "Wolf", overlayVersion: "2.0.0"));

        var a = OverlayPackInstaller.Install(v1)!;
        var b = OverlayPackInstaller.Install(v2)!;

        Assert.NotEqual(a.ArchiveFile, b.ArchiveFile);
        Assert.Equal(a.UnpackDir, b.UnpackDir);
        Assert.Equal(2, OverlayPackPaths.ImportedVersions("Wolf", "Main").Count);
    }

    [Fact]
    public void Install_SkipsZipSlipEntries()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string source = TestPacks.WriteZip(ws.Path_("evil.smo"), new Dictionary<string, string>
        {
            ["overlay.json"] = TestPacks.OverlayManifestJson(name: "Evil", author: "Wolf"),
            ["../../escaped.txt"] = "pwned",
            ["safe.txt"] = "fine"
        });

        var installed = OverlayPackInstaller.Install(source);

        Assert.NotNull(installed);
        Assert.True(File.Exists(Path.Combine(installed!.UnpackDir, "safe.txt")));
        Assert.Empty(Directory.EnumerateFiles(ws.Root, "escaped.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void Install_SkipsDirectoryEntries()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string path = ws.Path_("dirs.smo");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var manifest = zip.CreateEntry("overlay.json");
            using (var s = manifest.Open())
            using (var w = new StreamWriter(s))
                w.Write(TestPacks.OverlayManifestJson(name: "Dirs", author: "Wolf"));

            zip.CreateEntry("emptyfolder/");
        }

        var installed = OverlayPackInstaller.Install(path);

        Assert.NotNull(installed);
        Assert.False(Directory.Exists(Path.Combine(installed!.UnpackDir, "emptyfolder")));
    }

    [Fact]
    public void Install_NestedFoldersAreRecreated()
    {
        using var ws = new TempWorkspace("overlayinstaller");
        string source = TestPacks.WriteSmo(ws.Path_("deep.smo"),
            TestPacks.OverlayManifestJson(name: "Deep", author: "Wolf"),
            new Dictionary<string, string> { ["a/b/c/d.txt"] = "deep" });

        var installed = OverlayPackInstaller.Install(source)!;

        Assert.Equal("deep", File.ReadAllText(Path.Combine(installed.UnpackDir, "a", "b", "c", "d.txt")));
    }

    [Fact]
    public void ManifestFileName_IsOverlayJson()
        => Assert.Equal("overlay.json", OverlayPackInstaller.ManifestFileName);
}
