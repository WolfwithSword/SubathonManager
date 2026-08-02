using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using SubathonManager.Data.Widgets;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

[Collection("WorkingDirectory")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments")]
public class WidgetPackInstallerTests
{
    
    [Fact]
    public void ReadManifest_MissingFile_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        Assert.Null(WidgetPackInstaller.ReadManifest(Path.Combine(ws.Root, "nope.smw")));
    }

    [Fact]
    public void ReadManifest_NotAZip_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = ws.WriteFile("broken.smw", "definitely not a zip");

        Assert.Null(WidgetPackInstaller.ReadManifest(path));
    }

    [Fact]
    public void ReadManifest_ZipWithoutWidgetJson_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = TestPacks.WriteZip(ws.Path_("nomanifest.smw"),
            new Dictionary<string, string> { ["content/widget.html"] = "<html></html>" });

        Assert.Null(WidgetPackInstaller.ReadManifest(path));
    }

    [Fact]
    public void ReadManifest_MalformedJson_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = TestPacks.WriteZip(ws.Path_("bad.smw"),
            new Dictionary<string, string> { ["widget.json"] = "{ not json" });

        Assert.Null(WidgetPackInstaller.ReadManifest(path));
    }

    [Fact]
    public void ReadManifest_FullManifest_MapsEveryField()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = TestPacks.WriteSmw(ws.Path_("full.smw"), TestPacks.WidgetManifestJson(
            name: "Sub Timer", author: "Wolf", group: "Alerts", version: "2.1.0",
            entry: "content/timer.html", packId: "custom.pack.id", preview: "preview.png",
            docsUrl: "https://docs.example.com", tags: ["timer", "sub"],
            width: 640, height: 480, scaleX: 1.5f, scaleY: 2f));

        var manifest = WidgetPackInstaller.ReadManifest(path);

        Assert.NotNull(manifest);
        Assert.Equal("1", manifest!.FormatVersion);
        Assert.Equal("9.9.9", manifest.AppVersion);
        Assert.Equal("custom.pack.id", manifest.PackId);
        Assert.Equal("Sub Timer", manifest.Name);
        Assert.Equal("Wolf", manifest.Author);
        Assert.Equal("Alerts", manifest.Group);
        Assert.Equal("2.1.0", manifest.Version);
        Assert.Equal("https://docs.example.com", manifest.DocsUrl);
        Assert.Equal(new[] {"timer", "sub"}, manifest.Tags);
        Assert.Equal("preview.png", manifest.PreviewImage);
        Assert.Equal("content/timer.html", manifest.Entry);
        Assert.Equal(640, manifest.Width);
        Assert.Equal(480, manifest.Height);
        Assert.Equal(1.5f, manifest.ScaleX);
        Assert.Equal(2f, manifest.ScaleY);
    }

    [Fact]
    public void ReadManifest_NoWidgetObject_UsesEveryFallback()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = TestPacks.WriteZip(ws.Path_("MyPack.smw"),
            new Dictionary<string, string> { ["widget.json"] = "{}" });

        var manifest = WidgetPackInstaller.ReadManifest(path);

        Assert.NotNull(manifest);
        Assert.Equal("MyPack", manifest!.Name);
        Assert.Equal(string.Empty, manifest.Author);
        Assert.Equal("widgets", manifest.Group);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("widgets.mypack", manifest.PackId);
        Assert.Equal(string.Empty, manifest.Entry);
        Assert.Equal(400, manifest.Width);
        Assert.Equal(400, manifest.Height);
        Assert.Equal(1f, manifest.ScaleX);
        Assert.Equal(1f, manifest.ScaleY);
    }

    [Fact]
    public void ReadManifest_BlankPackId_IsDerivedFromAuthorGroupName()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = TestPacks.WriteSmw(ws.Path_("p.smw"),
            TestPacks.WidgetManifestJson(name: "My Timer", author: "Wolf", group: "Alerts", packId: ""));

        Assert.Equal("wolf.alerts.my-timer", WidgetPackInstaller.ReadManifest(path)!.PackId);
    }

    [Fact]
    public void ReadManifest_EntryIsNormalised()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = TestPacks.WriteSmw(ws.Path_("p.smw"),
            TestPacks.WidgetManifestJson(entry: @"\content\widget.html"));

        Assert.Equal("content/widget.html", WidgetPackInstaller.ReadManifest(path)!.Entry);
    }

    [Fact]
    public void ReadManifest_WrongTypedFields_FallBackInsteadOfThrowing()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = TestPacks.WriteZip(ws.Path_("typed.smw"), new Dictionary<string, string>
        {
            ["widget.json"] = """
                {
                  "version": 5,
                  "widget": {
                    "name": 42,
                    "tags": "not-an-array",
                    "entry": "content/a.html",
                    "size": { "width": 12.5, "height": 200 },
                    "scale": { "x": -3, "y": 2.5 }
                  }
                }
                """
        });

        var manifest = WidgetPackInstaller.ReadManifest(path);

        Assert.NotNull(manifest);
        Assert.Equal(string.Empty, manifest!.FormatVersion);
        Assert.Equal("typed", manifest.Name);
        Assert.Empty(manifest.Tags);
        Assert.Equal(400, manifest.Width);
        Assert.Equal(200, manifest.Height);
        Assert.Equal(1f, manifest.ScaleX);
        Assert.Equal(2.5f, manifest.ScaleY);
    }

    [Fact]
    public void ReadManifest_TagsArray_DropsNonStringsAndBlanks()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = TestPacks.WriteZip(ws.Path_("tags.smw"), new Dictionary<string, string>
        {
            ["widget.json"] = """
                { "widget": { "entry": "a.html", "tags": ["good", 7, "  ", "", "also-good"] } }
                """
        });

        Assert.Equal(new[] {"good", "also-good"}, WidgetPackInstaller.ReadManifest(path)!.Tags);
    }

        
    [Fact]
    public void Install_MissingFile_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        Assert.Null(WidgetPackInstaller.Install(Path.Combine(ws.Root, "nope.smw")));
    }

    [Fact]
    public void Install_ManifestWithoutEntry_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = TestPacks.WriteSmw(ws.Path_("noentry.smw"), TestPacks.WidgetManifestJson(entry: ""));

        Assert.Null(WidgetPackInstaller.Install(path));
    }

    [Fact]
    public void Install_CopiesPackIntoPackedStore_UnderSluggedVersion()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteSmw(ws.Path_("downloads", "timer.smw"),
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "1.2.0"));

        var installed = WidgetPackInstaller.Install(source);

        Assert.NotNull(installed);
        Assert.Equal(WidgetPackPaths.PackFile("wolf.widgets.timer", "1-2-0"), installed!.PackFile);
        Assert.True(File.Exists(installed.PackFile));
        Assert.Equal(WidgetPackPaths.EntryPath("wolf.widgets.timer", "1-2-0", "content/widget.html"),
            installed.HtmlPath);
    }

    [Fact]
    public void Install_UnsluggableVersion_FallsBackTo1_0_0()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteSmw(ws.Path_("downloads", "timer.smw"),
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "!!!"));

        var installed = WidgetPackInstaller.Install(source);

        Assert.NotNull(installed);
        Assert.EndsWith(Path.Combine("wolf.widgets.timer", "1-0-0.smw"), installed!.PackFile);
    }

    [Fact]
    public void Install_Reinstall_OverwritesAndClearsTheCacheDirectory()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteSmw(ws.Path_("downloads", "timer.smw"),
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "1.0.0"));

        var first = WidgetPackInstaller.Install(source)!;
        string cacheDir = WidgetPackPaths.CacheDirFor(first.PackFile);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "stale.bin"), "old");

        var second = WidgetPackInstaller.Install(source);

        Assert.NotNull(second);
        Assert.Equal(first.PackFile, second!.PackFile);
        Assert.False(Directory.Exists(cacheDir));
    }

    [Fact]
    public void Install_SourceAlreadyAtTarget_DoesNotSelfCopy()
    {
        using var ws = new TempWorkspace("packinstaller");
        string target = WidgetPackPaths.PackFile("wolf.widgets.timer", "1-0-0");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        TestPacks.WriteSmw(target,
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "1.0.0"));

        var installed = WidgetPackInstaller.Install(target);

        Assert.NotNull(installed);
        Assert.Equal(target, installed!.PackFile);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void Install_InvalidatesTheResolveCache()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteSmw(ws.Path_("downloads", "timer.smw"),
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "1.0.0"));

        string mountRoot = WidgetPackPaths.MountRoot("wolf.widgets.timer", "1-0-0");
        Assert.Null(WidgetPackPaths.Resolve(Path.Combine(mountRoot, "content", "widget.html")));

        var installed = WidgetPackInstaller.Install(source);

        Assert.NotNull(installed);
        Assert.NotNull(WidgetPackPaths.Resolve(installed!.HtmlPath));
    }

        
    [Fact]
    public void MountInPlace_MissingFile_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        Assert.Null(WidgetPackInstaller.MountInPlace(Path.Combine(ws.Root, "nope.smw")));
    }

    [Fact]
    public void MountInPlace_NoEntry_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        string path = TestPacks.WriteSmw(ws.Path_("p.smw"), TestPacks.WidgetManifestJson(entry: ""));

        Assert.Null(WidgetPackInstaller.MountInPlace(path));
    }

    [Fact]
    public void MountInPlace_MountsBesideThePack_WithoutCopying()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteSmw(ws.Path_("presets", "honse", "1-0-0.smw"),
            TestPacks.WidgetManifestJson(entry: "content/widget.html"));

        var mounted = WidgetPackInstaller.MountInPlace(source);

        Assert.NotNull(mounted);
        Assert.Equal(Path.GetFullPath(source), mounted!.PackFile);
        Assert.Equal(Path.Combine(ws.Root, "presets", "honse", "1-0-0", "content", "widget.html"),
            mounted.HtmlPath);
        Assert.False(Directory.Exists(WidgetPackPaths.PackedRoot));
    }

    [Fact]
    public void MountInPlace_ProducesAResolvablePath()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteSmw(ws.Path_("presets", "honse", "1-0-0.smw"),
            TestPacks.WidgetManifestJson(entry: "content/widget.html"));

        var mounted = WidgetPackInstaller.MountInPlace(source)!;
        var location = WidgetPackPaths.Resolve(mounted.HtmlPath);

        Assert.NotNull(location);
        Assert.Equal(Path.GetFullPath(source), location!.PackFileStr);
        Assert.Equal("honse", location.PackIdStr);
        Assert.Equal("1-0-0", location.VersionStr);
    }

        
    [Fact]
    public void DropIntoImports_MissingFile_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        Assert.Null(WidgetPackInstaller.DropIntoImports(Path.Combine(ws.Root, "nope.smw")));
    }

    [Fact]
    public void DropIntoImports_ValidPack_UsesTheNormalInstallPath()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteSmw(ws.Path_("downloads", "timer.smw"),
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "1.0.0"));

        Assert.Equal(WidgetPackPaths.PackFile("wolf.widgets.timer", "1-0-0"),
            WidgetPackInstaller.DropIntoImports(source));
    }

    [Fact]
    public void DropIntoImports_UninstallablePack_LandsFlatInPackedRoot()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteZip(ws.Path_("downloads", "mystery.smw"),
            new Dictionary<string, string> { ["readme.txt"] = "hi" });

        string? dropped = WidgetPackInstaller.DropIntoImports(source);

        Assert.Equal(Path.Combine(WidgetPackPaths.PackedRoot, "mystery.smw"), dropped);
        Assert.True(File.Exists(dropped));
    }

    [Fact]
    public void DropIntoImports_UninstallablePackAlreadyInPlace_ReturnsPathWithoutCopying()
    {
        using var ws = new TempWorkspace("packinstaller");
        Directory.CreateDirectory(WidgetPackPaths.PackedRoot);
        string source = TestPacks.WriteZip(Path.Combine(WidgetPackPaths.PackedRoot, "mystery.smw"),
            new Dictionary<string, string> { ["readme.txt"] = "hi" });

        Assert.Equal(source, WidgetPackInstaller.DropIntoImports(source));
    }

        
    [Fact]
    public void SweepCache_NoCacheRoot_ReturnsZero()
    {
        using var ws = new TempWorkspace("packinstaller");
        Assert.Equal(0, WidgetPackInstaller.SweepCache([]));
    }

    [Fact]
    public void SweepCache_RemovesEveryDirectory_WhenNothingIsLive()
    {
        using var ws = new TempWorkspace("packinstaller");
        Directory.CreateDirectory(Path.Combine(WidgetPackPaths.CacheRoot, "orphan-a"));
        Directory.CreateDirectory(Path.Combine(WidgetPackPaths.CacheRoot, "orphan-b"));

        Assert.Equal(2, WidgetPackInstaller.SweepCache([]));
        Assert.Empty(Directory.EnumerateDirectories(WidgetPackPaths.CacheRoot));
    }

    [Fact]
    public void SweepCache_KeepsCacheDirsForLiveWidgets()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteSmw(ws.Path_("downloads", "timer.smw"),
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "1.0.0"));
        var installed = WidgetPackInstaller.Install(source)!;

        string liveCache = WidgetPackPaths.CacheDirFor(installed.PackFile);
        Directory.CreateDirectory(liveCache);
        Directory.CreateDirectory(Path.Combine(WidgetPackPaths.CacheRoot, "orphan"));

        int removed = WidgetPackInstaller.SweepCache([installed.HtmlPath]);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(liveCache));
        Assert.False(Directory.Exists(Path.Combine(WidgetPackPaths.CacheRoot, "orphan")));
    }

    [Fact]
    public void SweepCache_UnresolvableLivePaths_AreIgnored()
    {
        using var ws = new TempWorkspace("packinstaller");
        Directory.CreateDirectory(Path.Combine(WidgetPackPaths.CacheRoot, "orphan"));

        Assert.Equal(1, WidgetPackInstaller.SweepCache([Path.Combine(ws.Root, "loose", "widget.html")]));
    }

            private static string InstallVersion(string version)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"smw-{Guid.NewGuid():N}.smw");
        TestPacks.WriteSmw(temp, TestPacks.WidgetManifestJson(
            packId: "wolf.widgets.timer", version: version, entry: "content/widget.html"));
        try
        {
            return WidgetPackInstaller.Install(temp)!.HtmlPath;
        }
        finally
        {
            try { File.Delete(temp); } catch { /**/ }
        }
    }

    [Fact]
    public void FindUpdate_UnresolvablePath_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        Assert.Null(WidgetPackInstaller.FindUpdate(Path.Combine(ws.Root, "loose", "widget.html")));
        Assert.Null(WidgetPackInstaller.FindNewerVersion(Path.Combine(ws.Root, "loose", "widget.html")));
    }

    [Fact]
    public void FindUpdate_OnlyVersionInstalled_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        string html = InstallVersion("1.0.0");

        Assert.Null(WidgetPackInstaller.FindUpdate(html));
    }

    [Fact]
    public void FindUpdate_NewerSiblingVersion_IsFound()
    {
        using var ws = new TempWorkspace("packinstaller");
        string html = InstallVersion("1.0.0");
        InstallVersion("1.2.0");
        WidgetPackPaths.InvalidateVersionCache();

        var update = WidgetPackInstaller.FindUpdate(html);

        Assert.NotNull(update);
        Assert.Equal("1-2-0", update!.Version);
        Assert.Equal("content/widget.html", update.Entry);
        Assert.True(File.Exists(update.PackFile));
        Assert.Equal("1-2-0", WidgetPackInstaller.FindNewerVersion(html));
    }

    [Fact]
    public void FindUpdate_PicksTheHighestOfSeveralNewerVersions()
    {
        using var ws = new TempWorkspace("packinstaller");
        string html = InstallVersion("1.0.0");
        InstallVersion("1.2.0");
        InstallVersion("1.10.0");
        InstallVersion("1.1.0");
        WidgetPackPaths.InvalidateVersionCache();

        Assert.Equal("1-10-0", WidgetPackInstaller.FindUpdate(html)!.Version);
    }

    [Fact]
    public void FindUpdate_OlderSiblingsOnly_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        InstallVersion("1.0.0");
        string html = InstallVersion("2.0.0");
        WidgetPackPaths.InvalidateVersionCache();

        Assert.Null(WidgetPackInstaller.FindUpdate(html));
    }

    [Fact]
    public void FindUpdate_NonVersionFolderNames_AreIgnored()
    {
        using var ws = new TempWorkspace("packinstaller");
        string html = InstallVersion("1.0.0");

        string folder = WidgetPackPaths.PackFolder("wolf.widgets.timer");
        File.WriteAllText(Path.Combine(folder, "nightly.smw"), "");
        WidgetPackPaths.InvalidateVersionCache();

        Assert.Null(WidgetPackInstaller.FindUpdate(html));
    }

    [Fact]
    public void FindUpdate_CurrentVersionNotVersionShaped_ReturnsNull()
    {
        using var ws = new TempWorkspace("packinstaller");
        string folder = Path.Combine(WidgetPackPaths.PackedRoot, "wolf.widgets.timer");
        Directory.CreateDirectory(folder);
        TestPacks.WriteSmw(Path.Combine(folder, "nightly.smw"), TestPacks.WidgetManifestJson());
        TestPacks.WriteSmw(Path.Combine(folder, "2-0-0.smw"), TestPacks.WidgetManifestJson());

        string html = Path.Combine(folder, "nightly", "content", "widget.html");

        Assert.Null(WidgetPackInstaller.FindUpdate(html));
    }

    [Fact]
    public void FindUpdate_NewerPackWithUnreadableManifest_ReportsEmptyEntry()
    {
        using var ws = new TempWorkspace("packinstaller");
        string html = InstallVersion("1.0.0");

        string folder = WidgetPackPaths.PackFolder("wolf.widgets.timer");
        File.WriteAllText(Path.Combine(folder, "9-0-0.smw"), "not a zip");
        WidgetPackPaths.InvalidateVersionCache();

        var update = WidgetPackInstaller.FindUpdate(html);

        Assert.NotNull(update);
        Assert.Equal("9-0-0", update!.Version);
        Assert.Equal(string.Empty, update.Entry);
    }

        
    [Fact]
    public void InstalledPack_ContentsAreReadableThroughTheFileSystemAdapter()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteSmw(ws.Path_("downloads", "timer.smw"),
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "1.0.0"),
            new Dictionary<string, string> { ["content/widget.html"] = "<html>hi</html>" });

        var installed = WidgetPackInstaller.Install(source)!;
        var fs = new WidgetPackFileSystem();

        Assert.True(fs.Exists(installed.HtmlPath));
        Assert.Equal("<html>hi</html>", fs.ReadAllText(installed.HtmlPath));
    }

    [Fact]
    public void Install_CopiedPackIsStillAValidZip()
    {
        using var ws = new TempWorkspace("packinstaller");
        string source = TestPacks.WriteSmw(ws.Path_("downloads", "timer.smw"),
            TestPacks.WidgetManifestJson(packId: "wolf.widgets.timer", version: "1.0.0"),
            new Dictionary<string, string> { ["content/widget.html"] = "<html></html>" });

        var installed = WidgetPackInstaller.Install(source)!;

        using var zip = ZipFile.OpenRead(installed.PackFile);
        Assert.NotNull(zip.GetEntry("widget.json"));
        Assert.NotNull(zip.GetEntry("content/widget.html"));
    }

}