using SubathonManager.Data.Widgets;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

[Collection("WorkingDirectory")]
public class WidgetPackFileSystemTests
{
    private static string MakePackedWidget(params (string entry, string content)[] files)
    {
        string folder = Path.Combine(WidgetPackPaths.PackedRoot, "wolf.widgets.timer");
        Directory.CreateDirectory(folder);
        TestPacks.WriteZip(Path.Combine(folder, "1-0-0.smw"),
            files.Select(f => new KeyValuePair<string, string>(f.entry, f.content)));
        WidgetPackPaths.InvalidateResolveCache();
        return Path.Combine(folder, "1-0-0");
    }

    [Fact]
    public void Exists_PackedEntry_IsTrue()
    {
        using var ws = new TempWorkspace("packfs");
        string mount = MakePackedWidget(("content/widget.html", "<html></html>"));
        var fs = new WidgetPackFileSystem();

        Assert.True(fs.Exists(Path.Combine(mount, "content", "widget.html")));
        Assert.False(fs.Exists(Path.Combine(mount, "content", "missing.html")));
    }

    [Fact]
    public void IsPacked_DistinguishesPackedFromLoose()
    {
        using var ws = new TempWorkspace("packfs");
        string mount = MakePackedWidget(("content/widget.html", "<html></html>"));
        string loose = ws.WriteFile("loose/widget.html", "<html></html>");
        var fs = new WidgetPackFileSystem();

        Assert.True(fs.IsPacked(Path.Combine(mount, "content", "widget.html")));
        Assert.False(fs.IsPacked(loose));
    }

    [Fact]
    public void ReadAllText_And_ReadAllBytes_ComeFromTheArchive()
    {
        using var ws = new TempWorkspace("packfs");
        string mount = MakePackedWidget(("content/widget.html", "<html>packed</html>"));
        var fs = new WidgetPackFileSystem();
        string path = Path.Combine(mount, "content", "widget.html");

        Assert.Equal("<html>packed</html>", fs.ReadAllText(path));
        Assert.Equal("<html>packed</html>"u8.ToArray(), fs.ReadAllBytes(path));
    }

    [Fact]
    public void ReadAllText_MissingPackedEntry_ReturnsNull()
    {
        using var ws = new TempWorkspace("packfs");
        string mount = MakePackedWidget(("content/widget.html", "x"));
        var fs = new WidgetPackFileSystem();

        Assert.Null(fs.ReadAllText(Path.Combine(mount, "content", "missing.html")));
        Assert.Null(fs.ReadAllBytes(Path.Combine(mount, "content", "missing.html")));
    }

    [Fact]
    public void GetRealFilePath_SmallPackedEntry_ReturnsNull()
    {
        using var ws = new TempWorkspace("packfs");
        string mount = MakePackedWidget(("content/widget.html", "small"));
        var fs = new WidgetPackFileSystem();
        Assert.Null(fs.GetRealFilePath(Path.Combine(mount, "content", "widget.html")));
    }

    [Fact]
    public void GetRealFilePath_LargePackedEntry_MaterialisesToTheCache()
    {
        using var ws = new TempWorkspace("packfs");
        string big = new('x', (int)WidgetPackMemoryCache.MaxEntrySize + 16);
        string mount = MakePackedWidget(("content/widget.html", "<html></html>"), ("media/big.txt", big));
        var fs = new WidgetPackFileSystem();

        string? real = fs.GetRealFilePath(Path.Combine(mount, "media", "big.txt"));

        Assert.NotNull(real);
        Assert.True(File.Exists(real));
        Assert.StartsWith(WidgetPackPaths.CacheRoot, real!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnumerateFiles_PackedFolder_ListsMountedEntryPaths()
    {
        using var ws = new TempWorkspace("packfs");
        string mount = MakePackedWidget(
            ("widget.json", "{}"),
            ("content/widget.html", "<html></html>"),
            ("content/sub/style.css", "body{}"));
        var fs = new WidgetPackFileSystem();

        var files = fs.EnumerateFiles(Path.Combine(mount, "content")).ToList();

        Assert.Equal(2, files.Count);
        Assert.Contains(Path.Combine(mount, "content", "widget.html"), files);
        Assert.Contains(Path.Combine(mount, "content", "sub", "style.css"), files);
        Assert.DoesNotContain(Path.Combine(mount, "widget.json"), files);
    }

    [Fact]
    public void EnumerateFiles_MountRoot_ListsEverything()
    {
        using var ws = new TempWorkspace("packfs");
        string mount = MakePackedWidget(("widget.json", "{}"), ("content/widget.html", "<html></html>"));
        var fs = new WidgetPackFileSystem();

        var files = fs.EnumerateFiles(mount).ToList();

        Assert.Equal(2, files.Count);
        Assert.Contains(Path.Combine(mount, "widget.json"), files);
    }

    [Fact]
    public void Unpack_PackedPath_ExtractsEverything()
    {
        using var ws = new TempWorkspace("packfs");
        string mount = MakePackedWidget(("widget.json", "{}"), ("content/widget.html", "<html>u</html>"));
        var fs = new WidgetPackFileSystem();

        string target = Path.Combine(ws.Root, "unpacked");
        Assert.True(fs.Unpack(mount, target));
        Assert.Equal("<html>u</html>", File.ReadAllText(Path.Combine(target, "content", "widget.html")));
    }

    [Fact]
    public void Unpack_LoosePath_ReturnsFalse()
    {
        using var ws = new TempWorkspace("packfs");
        string loose = ws.Dir("loose");
        var fs = new WidgetPackFileSystem();

        Assert.False(fs.Unpack(loose, Path.Combine(ws.Root, "out")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPath_FallsBackToDiskAndReportsMissing(string? path)
    {
        using var ws = new TempWorkspace("packfs");
        var fs = new WidgetPackFileSystem();

        Assert.False(fs.Exists(path!));
        Assert.False(fs.IsPacked(path!));
        Assert.Null(fs.ReadAllText(path!));
    }

    [Fact]
    public void LooseFile_IsReadFromDisk()
    {
        using var ws = new TempWorkspace("packfs");
        string loose = ws.WriteFile("loose/widget.html", "<html>disk</html>");
        var fs = new WidgetPackFileSystem();

        Assert.True(fs.Exists(loose));
        Assert.Equal("<html>disk</html>", fs.ReadAllText(loose));
        Assert.Equal("<html>disk</html>"u8.ToArray(), fs.ReadAllBytes(loose));
        Assert.Equal(loose, fs.GetRealFilePath(loose));
    }

    [Fact]
    public void MissingLooseFile_ReturnsNulls()
    {
        using var ws = new TempWorkspace("packfs");
        string missing = Path.Combine(ws.Root, "loose", "nope.html");
        var fs = new WidgetPackFileSystem();

        Assert.False(fs.Exists(missing));
        Assert.Null(fs.ReadAllText(missing));
        Assert.Null(fs.ReadAllBytes(missing));
        Assert.Null(fs.GetRealFilePath(missing));
    }

    [Fact]
    public void EnumerateFiles_LooseFolder_RecursesOnDisk()
    {
        using var ws = new TempWorkspace("packfs");
        ws.WriteFile("loose/widget.html", "a");
        ws.WriteFile("loose/sub/style.css", "b");
        var fs = new WidgetPackFileSystem();

        var files = fs.EnumerateFiles(Path.Combine(ws.Root, "loose")).ToList();

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void EnumerateFiles_MissingFolder_ReturnsEmpty()
    {
        using var ws = new TempWorkspace("packfs");
        var fs = new WidgetPackFileSystem();

        Assert.Empty(fs.EnumerateFiles(Path.Combine(ws.Root, "nope")));
    }

    [Fact]
    public void ReadersAreReusedPerPackFile()
    {
        using var ws = new TempWorkspace("packfs");
        string mount = MakePackedWidget(("content/widget.html", "<html>cached</html>"));
        var fs = new WidgetPackFileSystem();
        string path = Path.Combine(mount, "content", "widget.html");

        Assert.Equal("<html>cached</html>", fs.ReadAllText(path));
        Assert.Equal("<html>cached</html>", fs.ReadAllText(path));
        Assert.True(fs.Exists(path));
    }
}
