using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.DataUnitTests;

[Collection("ProviderOverrideTests")]
public class OverlayPorterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private static IDbContextFactory<AppDbContext> SetupServices()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(MockConfig.MakeMockConfig(new Dictionary<(string, string), string>
        {
            [("Server", "Port")] = "14045"
        }));
        AppServices.Provider = services.BuildServiceProvider();
        return AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    }

    private string MakeTempWidget(string folderName, string htmlFileName = "widget.html")
    {
        var dir = Path.Combine(Path.GetTempPath(), "OverlayPorterTests", Guid.NewGuid().ToString(), folderName);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(Path.GetDirectoryName(dir)!);
        var htmlPath = Path.Combine(dir, htmlFileName);
        File.WriteAllText(htmlPath, "<html><body>test</body></html>");
        return htmlPath;
    }

    private string MakeSmoFile(object manifest, Dictionary<string, string>? extraFiles = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "OverlayPorterTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var smoPath = Path.Combine(dir, "test.smo");

        using var zip = ZipFile.Open(smoPath, ZipArchiveMode.Create);

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });
        var entry = zip.CreateEntry("overlay.json");
        using (var s = entry.Open())
        using (var w = new StreamWriter(s))
            w.Write(manifestJson);

        if (extraFiles != null)
        {
            foreach (var (zipPath, content) in extraFiles)
            {
                var e = zip.CreateEntry(zipPath);
                using var s = e.Open();
                using var w = new StreamWriter(s);
                w.Write(content);
            }
        }

        return smoPath;
    }

    private string MakeTempExtractRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "OverlayPorterTests", Guid.NewGuid().ToString(), "imports");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        AppServices.Provider = null!;
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* */ }
        }
    }

    [Fact]
    public void GetZipWidgetRoots_EmptyList_ReturnsEmpty()
    {
        var result = OverlayPorter.GetZipWidgetRoots(new List<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void GetZipWidgetRoots_SinglePath_AlwaysHasHash()
    {
        var result = OverlayPorter.GetZipWidgetRoots(new List<string>
        {
            @"C:\stream\widgets\timer"
        });

        Assert.Single(result);
        var parts = result[0].Split('/');
        Assert.Equal("widgets", parts[0]);
        Assert.Equal("timer", parts[^1]);
        Assert.True(parts.Length >= 3, $"Expected at least 3 parts but got: {result[0]}");
    }

    [Fact]
    public void GetZipWidgetRoots_SameParent_SharesHash()
    {
        var paths = new List<string>
        {
            @"C:\stream\widgets\timer",
            @"C:\stream\widgets\alerts"
        };
        var result = OverlayPorter.GetZipWidgetRoots(paths);

        Assert.Equal(2, result.Count);
        
        var timerParts = result[0].Split('/');
        var alertsParts = result[1].Split('/');

        Assert.Equal("widgets", timerParts[0]);
        Assert.Equal("widgets", alertsParts[0]);
        Assert.Equal(timerParts[1], alertsParts[1]);
        Assert.Equal("timer", timerParts[^1]);
        Assert.Equal("alerts", alertsParts[^1]);
    }

    [Fact]
    public void GetZipWidgetRoots_DifferentDrives_GetDifferentHashs()
    {
        var paths = new List<string>
        {
            @"C:\Path\To\steampunk",
            @"G:\My\Path\steampunk"
        };
        var result = OverlayPorter.GetZipWidgetRoots(paths);

        Assert.Equal(2, result.Count);
        var cParts = result[0].Split('/');
        var gParts = result[1].Split('/');

        Assert.NotEqual(cParts[1], gParts[1]);
        Assert.Equal("steampunk", cParts[^1]);
        Assert.Equal("steampunk", gParts[^1]);
    }

    [Fact]
    public void GetZipWidgetRoots_SharedPrefixWithDifferentLeafs_SameParentDifferentLeafs()
    {
        var paths = new List<string>
        {
            @"G:\My\Path\steampunk",
            @"G:\My\Path\other"
        };
        var result = OverlayPorter.GetZipWidgetRoots(paths);

        var p1 = result[0].Split('/');
        var p2 = result[1].Split('/');

        Assert.Equal(p1[1], p2[1]);
        Assert.Equal("steampunk", p1[^1]);
        Assert.Equal("other", p2[^1]);
    }

    [Fact]
    public void GetZipWidgetRoots_ThreePaths_TwoSameDriveOneDifferent()
    {
        var paths = new List<string>
        {
            @"C:\Path\To\steampunk",
            @"G:\My\Path\steampunk",
            @"G:\My\Path\other"
        };
        var result = OverlayPorter.GetZipWidgetRoots(paths);

        Assert.Equal(3, result.Count);
        var cBucket = result[0].Split('/')[1];
        var g1Bucket = result[1].Split('/')[1];
        var g2Bucket = result[2].Split('/')[1];

        Assert.NotEqual(cBucket, g1Bucket);
        Assert.Equal(g1Bucket, g2Bucket);
    }

    [Fact]
    public void GetZipWidgetRoots_AlwaysStartsWithWidgets()
    {
        var paths = new List<string> { @"C:\foo\bar\baz", @"D:\qux\baz" };
        var result = OverlayPorter.GetZipWidgetRoots(paths);
        Assert.All(result, r => Assert.StartsWith("widgets/", r));
    }

    [Fact]
    public void GetZipWidgetRoots_LeafIsAlwaysLiteralNotHashed()
    {
        var paths = new List<string> { @"C:\stream\my_timer_widget" };
        var result = OverlayPorter.GetZipWidgetRoots(paths);
        Assert.EndsWith("/my_timer_widget", result[0]);
    }

    [Fact]
    public void GetZipWidgetRoots_ResultCountMatchesInputCount()
    {
        var paths = Enumerable.Range(0, 5)
            .Select(i => $@"C:\widgets\widget{i}")
            .ToList();
        var result = OverlayPorter.GetZipWidgetRoots(paths);
        Assert.Equal(paths.Count, result.Count);
    }
    
    [Fact]
    public async Task ExportRouteAsync_CreatesZipFile()
    {
        SetupServices();
        var htmlPath = MakeTempWidget("mytimer");
        var route = new Route { Name = "Test Route", Width = 1920, Height = 1080 };
        var widget = new Widget("Timer", htmlPath) { RouteId = route.Id };
        route.Widgets.Add(widget);

        var outDir = Path.Combine(Path.GetTempPath(), "OverlayPorterTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(outDir);
        _tempDirs.Add(outDir);
        var outPath = Path.Combine(outDir, "test.smo");

        await OverlayPorter.ExportRouteAsync(route, outPath, "My Export");

        Assert.True(File.Exists(outPath));
    }

    [Fact]
    public async Task ExportRouteAsync_ZipContainsManifest()
    {
        SetupServices();
        var htmlPath = MakeTempWidget("mytimer");
        var route = new Route { Name = "Test", Width = 1920, Height = 1080 };
        route.Widgets.Add(new Widget("Timer", htmlPath) { RouteId = route.Id });

        var outPath = Path.Combine(Path.GetTempPath(), "OverlayPorterTests",
            Guid.NewGuid().ToString(), "out.smo");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);

        await OverlayPorter.ExportRouteAsync(route, outPath, "Export");

        using var zip = ZipFile.OpenRead(outPath);
        Assert.NotNull(zip.GetEntry("overlay.json"));
    }

    [Fact]
    public async Task ExportRouteAsync_ManifestContainsWidgetHtmlPath()
    {
        SetupServices();
        var htmlPath = MakeTempWidget("mytimer");
        var route = new Route { Name = "Test", Width = 1920, Height = 1080 };
        route.Widgets.Add(new Widget("Timer", htmlPath) { RouteId = route.Id });

        var outPath = Path.Combine(Path.GetTempPath(), "OverlayPorterTests",
            Guid.NewGuid().ToString(), "out.smo");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);

        await OverlayPorter.ExportRouteAsync(route, outPath, "Export");

        using var zip = ZipFile.OpenRead(outPath);
        using var ms = new MemoryStream();
        zip.GetEntry("overlay.json")!.Open().CopyTo(ms);
        var doc = JsonDocument.Parse(ms.ToArray());
        var widgets = doc.RootElement.GetProperty("widgets").EnumerateArray().ToList();

        Assert.Single(widgets);
        var htmlZipPath = widgets[0].GetProperty("htmlPath").GetString()!;
        Assert.EndsWith("/widget.html", htmlZipPath);
        Assert.StartsWith("widgets/", htmlZipPath);
    }

    [Fact]
    public async Task ExportRouteAsync_ManifestUsesExportName()
    {
        SetupServices();
        var htmlPath = MakeTempWidget("mytimer");
        var route = new Route { Name = "Original Name", Width = 1920, Height = 1080 };
        route.Widgets.Add(new Widget("Timer", htmlPath) { RouteId = route.Id });

        var outPath = Path.Combine(Path.GetTempPath(), "OverlayPorterTests",
            Guid.NewGuid().ToString(), "out.smo");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);

        await OverlayPorter.ExportRouteAsync(route, outPath, "Custom Export Name");

        using var zip = ZipFile.OpenRead(outPath);
        using var ms = new MemoryStream();
        zip.GetEntry("overlay.json")!.Open().CopyTo(ms);
        var doc = JsonDocument.Parse(ms.ToArray());
        var name = doc.RootElement.GetProperty("route").GetProperty("name").GetString();
        Assert.Equal("Custom Export Name", name);
    }

    [Fact]
    public async Task ExportRouteAsync_ExcludedEntries_NotInZip()
    {
        SetupServices();
        var htmlPath = MakeTempWidget("mytimer");
        var assetPath = Path.Combine(Path.GetDirectoryName(htmlPath)!, "bg.png");
        await File.WriteAllBytesAsync(assetPath, [0x89, 0x50]);

        var route = new Route { Name = "Test", Width = 1920, Height = 1080 };
        route.Widgets.Add(new Widget("Timer", htmlPath) { RouteId = route.Id });

        var outPath = Path.Combine(Path.GetTempPath(), "OverlayPorterTests",
            Guid.NewGuid().ToString(), "out.smo");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);

        var zipRoots = OverlayPorter.GetZipWidgetRoots(new List<string>
            { Path.GetDirectoryName(htmlPath)! });
        var bgZipEntry = $"{zipRoots[0]}/bg.png";

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { bgZipEntry };
        await OverlayPorter.ExportRouteAsync(route, outPath, "Export", excluded);

        using var zip = ZipFile.OpenRead(outPath);
        Assert.Null(zip.GetEntry(bgZipEntry));
        Assert.NotNull(zip.GetEntry($"{zipRoots[0]}/widget.html"));
    }

    [Fact]
    public async Task ExportRouteAsync_WidgetFilesIncludedInZip()
    {
        SetupServices();
        var htmlPath = MakeTempWidget("mytimer");
        var extraFile = Path.Combine(Path.GetDirectoryName(htmlPath)!, "style.css");
        await File.WriteAllTextAsync(extraFile, "body { color: red; }");

        var route = new Route { Name = "Test", Width = 1920, Height = 1080 };
        route.Widgets.Add(new Widget("Timer", htmlPath) { RouteId = route.Id });

        var outPath = Path.Combine(Path.GetTempPath(), "OverlayPorterTests",
            Guid.NewGuid().ToString(), "out.smo");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);

        await OverlayPorter.ExportRouteAsync(route, outPath, "Export");

        using var zip = ZipFile.OpenRead(outPath);
        var entries = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(entries, e => e.EndsWith("widget.html"));
        Assert.Contains(entries, e => e.EndsWith("style.css"));
    }

    [Fact]
    public async Task ImportRouteAsync_MissingManifest_ReturnsFailed()
    {
        var factory = SetupServices();
        var dir = Path.Combine(Path.GetTempPath(), "OverlayPorterTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var smoPath = Path.Combine(dir, "empty.smo");
        using (var zip = ZipFile.Open(smoPath, ZipArchiveMode.Create))
            zip.CreateEntry("dummy.txt");

        var result = await OverlayPorter.ImportRouteAsync(smoPath, MakeTempExtractRoot(), factory);

        Assert.True(result.Failed);
        Assert.False(string.IsNullOrWhiteSpace(result.FailReason));
    }

    [Fact]
    public async Task ImportRouteAsync_NewRoute_RouteIsNew()
    {
        var factory = SetupServices();
        var htmlZipPath = "widgets/abc1/mytimer/widget.html";
        var manifest = new
        {
            version = 1,
            route = new { name = "My Overlay", resolution = new { width = 1920, height = 1080 } },
            widget_folder_map = new { },
            widgets = new[]
            {
                new
                {
                    name = "Timer", htmlPath = htmlZipPath,
                    position = new { x = 0f, y = 0f, z = 0 },
                    size = new { width = 400, height = 300 },
                    scale = new { x = 1f, y = 1f },
                    visibility = true, docsUrl = (string?)null,
                    cssVariables = Array.Empty<object>(),
                    jsVariables = Array.Empty<object>()
                }
            }
        };
        var smo = MakeSmoFile(manifest, new Dictionary<string, string>
        {
            [htmlZipPath] = "<html></html>"
        });

        var result = await OverlayPorter.ImportRouteAsync(smo, MakeTempExtractRoot(), factory);

        Assert.False(result.Failed);
        Assert.True(result.RouteIsNew);
        Assert.NotNull(result.Route);
        Assert.Equal("My Overlay", result.Route!.Name);
    }

    [Fact]
    public async Task ImportRouteAsync_NewRoute_WidgetInNewWidgets()
    {
        var factory = SetupServices();
        var htmlZipPath = "widgets/abc1/mytimer/widget.html";
        var manifest = new
        {
            version = 1,
            route = new { name = "Overlay", resolution = new { width = 1920, height = 1080 } },
            widget_folder_map = new { },
            widgets = new[]
            {
                new
                {
                    name = "MyWidget", htmlPath = htmlZipPath,
                    position = new { x = 10f, y = 20f, z = 1 },
                    size = new { width = 500, height = 400 },
                    scale = new { x = 1f, y = 1f },
                    visibility = true, docsUrl = (string?)null,
                    cssVariables = Array.Empty<object>(),
                    jsVariables = Array.Empty<object>()
                }
            }
        };
        var smo = MakeSmoFile(manifest, new Dictionary<string, string> { [htmlZipPath] = "" });
        var result = await OverlayPorter.ImportRouteAsync(smo, MakeTempExtractRoot(), factory);

        Assert.Single(result.NewWidgets);
        var w = result.NewWidgets[0];
        Assert.Equal("MyWidget", w.Name);
        Assert.Equal(10f, w.X);
        Assert.Equal(20f, w.Y);
        Assert.Equal(1, w.Z);
        Assert.Equal(500, w.Width);
        Assert.Equal(400, w.Height);
    }

    [Fact]
    public async Task ImportRouteAsync_NewRoute_WidgetHtmlPathPointsInsideExtractDir()
    {
        var factory = SetupServices();
        var htmlZipPath = "widgets/abc1/mytimer/widget.html";
        var manifest = new
        {
            version = 1,
            route = new { name = "R", resolution = new { width = 1920, height = 1080 } },
            widget_folder_map = new { },
            widgets = new[]
            {
                new
                {
                    name = "W", htmlPath = htmlZipPath,
                    position = new { x = 0f, y = 0f, z = 0 },
                    size = new { width = 400, height = 300 },
                    scale = new { x = 1f, y = 1f },
                    visibility = true, docsUrl = (string?)null,
                    cssVariables = Array.Empty<object>(),
                    jsVariables = Array.Empty<object>()
                }
            }
        };
        var extractRoot = MakeTempExtractRoot();
        var smo = MakeSmoFile(manifest, new Dictionary<string, string> { [htmlZipPath] = "" });
        var result = await OverlayPorter.ImportRouteAsync(smo, extractRoot, factory);

        var widget = result.NewWidgets[0];
        Assert.StartsWith(extractRoot, widget.HtmlPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("widget.html", widget.HtmlPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportRouteAsync_ReImport_RouteNotNew_NoDuplicateWidgets()
    {
        var factory = SetupServices();
        var htmlZipPath = "widgets/abc1/mytimer/widget.html";
        var extractRoot = MakeTempExtractRoot();
        var manifest = new
        {
            version = 1,
            route = new { name = "R", resolution = new { width = 1920, height = 1080 } },
            widget_folder_map = new { },
            widgets = new[]
            {
                new
                {
                    name = "W", htmlPath = htmlZipPath,
                    position = new { x = 0f, y = 0f, z = 0 },
                    size = new { width = 400, height = 300 },
                    scale = new { x = 1f, y = 1f },
                    visibility = true, docsUrl = (string?)null,
                    cssVariables = Array.Empty<object>(),
                    jsVariables = Array.Empty<object>()
                }
            }
        };
        var smo = MakeSmoFile(manifest, new Dictionary<string, string> { [htmlZipPath] = "" });

        //seed
        var first = await OverlayPorter.ImportRouteAsync(smo, extractRoot, factory);
        Assert.True(first.RouteIsNew);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routes.Add(first.Route!);
            db.Widgets.AddRange(first.NewWidgets);
            await db.SaveChangesAsync();
        }

        var second = await OverlayPorter.ImportRouteAsync(smo, extractRoot, factory);

        Assert.False(second.RouteIsNew);
        Assert.Null(second.Route);
        Assert.Empty(second.NewWidgets);
        Assert.False(second.HasAnythingNew);
    }

    [Fact]
    public async Task ImportRouteAsync_ReImport_NewVariable_AddedToExistingWidget()
    {
        var factory = SetupServices();
        var htmlZipPath = "widgets/abc1/mytimer/widget.html";
        var extractRoot = MakeTempExtractRoot();

        object MakeManifest(object[] jsVars) => new
        {
            version = 1,
            route = new { name = "R", resolution = new { width = 1920, height = 1080 } },
            widget_folder_map = new { },
            widgets = new[]
            {
                new
                {
                    name = "W", htmlPath = htmlZipPath,
                    position = new { x = 0f, y = 0f, z = 0 },
                    size = new { width = 400, height = 300 },
                    scale = new { x = 1f, y = 1f },
                    visibility = true, docsUrl = (string?)null,
                    cssVariables = Array.Empty<object>(),
                    jsVariables = jsVars
                }
            }
        };

        var smo1 = MakeSmoFile(MakeManifest([]), new Dictionary<string, string> { [htmlZipPath] = "" });
        var first = await OverlayPorter.ImportRouteAsync(smo1, extractRoot, factory);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routes.Add(first.Route!);
            db.Widgets.AddRange(first.NewWidgets);
            await db.SaveChangesAsync();
        }

        var smo2 = MakeSmoFile(MakeManifest([
            new { name = "myVar", value = "hello", type = WidgetVariableType.String }
        ]), new Dictionary<string, string> { [htmlZipPath] = "" });
        var second = await OverlayPorter.ImportRouteAsync(smo2, extractRoot, factory);

        Assert.False(second.RouteIsNew);
        Assert.Empty(second.NewWidgets);
        Assert.Single(second.NewJsVariables);
        Assert.Equal("myVar", second.NewJsVariables[0].Name);
        Assert.Equal("hello", second.NewJsVariables[0].Value);
    }

    [Fact]
    public async Task ImportRouteAsync_CssVariables_ImportedOnNewWidget()
    {
        var factory = SetupServices();
        var htmlZipPath = "widgets/abc1/mytimer/widget.html";
        var manifest = new
        {
            version = 1,
            route = new { name = "R", resolution = new { width = 1920, height = 1080 } },
            widget_folder_map = new { },
            widgets = new[]
            {
                new
                {
                    name = "W", htmlPath = htmlZipPath,
                    position = new { x = 0f, y = 0f, z = 0 },
                    size = new { width = 400, height = 300 },
                    scale = new { x = 1f, y = 1f },
                    visibility = true, docsUrl = (string?)null,
                    cssVariables = new[] { new { name = "primary-color", value = "#ff0000" } },
                    jsVariables = Array.Empty<object>()
                }
            }
        };
        var smo = MakeSmoFile(manifest, new Dictionary<string, string> { [htmlZipPath] = "" });
        var result = await OverlayPorter.ImportRouteAsync(smo, MakeTempExtractRoot(), factory);

        Assert.Single(result.NewWidgets);
        var cssVars = result.NewWidgets[0].CssVariables;
        Assert.Single(cssVars);
        Assert.Equal("primary-color", cssVars[0].Name);
        Assert.Equal("#ff0000", cssVars[0].Value);
    }

    [Fact]
    public async Task ImportRouteAsync_RelativeFileVariable_ResolvedToAbsoluteInsideExtractDir()
    {
        var factory = SetupServices();
        var htmlZipPath = "widgets/abc1/mytimer/widget.html";
        var assetZipPath = "widgets/abc1/mytimer/bg.png";
        var manifest = new
        {
            version = 1,
            route = new { name = "R", resolution = new { width = 1920, height = 1080 } },
            widget_folder_map = new { },
            widgets = new[]
            {
                new
                {
                    name = "W", htmlPath = htmlZipPath,
                    position = new { x = 0f, y = 0f, z = 0 },
                    size = new { width = 400, height = 300 },
                    scale = new { x = 1f, y = 1f },
                    visibility = true, docsUrl = (string?)null,
                    cssVariables = Array.Empty<object>(),
                    jsVariables = new[]
                    {
                        new { name = "bgImage", value = "./bg.png", type = WidgetVariableType.ImageFile }
                    }
                }
            }
        };
        var extractRoot = MakeTempExtractRoot();
        var smo = MakeSmoFile(manifest, new Dictionary<string, string>
        {
            [htmlZipPath] = "",
            [assetZipPath] = "fake png bytes"
        });
        var result = await OverlayPorter.ImportRouteAsync(smo, extractRoot, factory);

        var jsVar = result.NewWidgets[0].JsVariables.FirstOrDefault(v => v.Name == "bgImage");
        Assert.NotNull(jsVar);
        Assert.StartsWith("./", jsVar!.Value);
        Assert.Contains("bg.png", jsVar.Value);
    }

    [Fact]
    public async Task ImportRouteAsync_RouteResolution_CorrectWidthHeight()
    {
        var factory = SetupServices();
        var htmlZipPath = "widgets/abc1/mytimer/widget.html";
        var manifest = new
        {
            version = 1,
            route = new { name = "R", resolution = new { width = 2560, height = 1440 } },
            widget_folder_map = new { },
            widgets = new[]
            {
                new
                {
                    name = "W", htmlPath = htmlZipPath,
                    position = new { x = 0f, y = 0f, z = 0 },
                    size = new { width = 400, height = 300 },
                    scale = new { x = 1f, y = 1f },
                    visibility = true, docsUrl = (string?)null,
                    cssVariables = Array.Empty<object>(),
                    jsVariables = Array.Empty<object>()
                }
            }
        };
        var smo = MakeSmoFile(manifest, new Dictionary<string, string> { [htmlZipPath] = "" });
        var result = await OverlayPorter.ImportRouteAsync(smo, MakeTempExtractRoot(), factory);

        Assert.NotNull(result.Route);
        Assert.Equal(2560, result.Route!.Width);
        Assert.Equal(1440, result.Route.Height);
    }

    [Fact]
    public async Task RoundTrip_ExportThenImport_WidgetHtmlPathResolvesCorrectly()
    {
        var factory = SetupServices();
        var htmlPath = MakeTempWidget("roundtrip");
        var route = new Route { Name = "RT", Width = 1920, Height = 1080 };
        var widget = new Widget("RT Widget", htmlPath) { RouteId = route.Id };
        route.Widgets.Add(widget);

        var outDir = Path.Combine(Path.GetTempPath(), "OverlayPorterTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(outDir);
        _tempDirs.Add(outDir);
        var smoPath = Path.Combine(outDir, "rt.smo");

        await OverlayPorter.ExportRouteAsync(route, smoPath, "RT Export");

        var extractRoot = MakeTempExtractRoot();
        var result = await OverlayPorter.ImportRouteAsync(smoPath, extractRoot, factory);

        Assert.False(result.Failed);
        Assert.True(result.RouteIsNew);
        Assert.Single(result.NewWidgets);

        var imported = result.NewWidgets[0];
        Assert.True(File.Exists(imported.HtmlPath), $"HtmlPath should exist on disk: {imported.HtmlPath}");
        Assert.EndsWith("widget.html", imported.HtmlPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RoundTrip_ExportThenImport_WidgetPropertiesPreserved()
    {
        var factory = SetupServices();
        var htmlPath = MakeTempWidget("props");
        var route = new Route { Name = "Props Test", Width = 1280, Height = 720 };
        var widget = new Widget("Props Widget", htmlPath)
        {
            RouteId = route.Id,
            X = 100f, Y = 200f, Z = 3,
            Width = 640, Height = 480,
            ScaleX = 1.5f, ScaleY = 2f,
            Visibility = false
        };
        route.Widgets.Add(widget);

        var outDir = Path.Combine(Path.GetTempPath(), "OverlayPorterTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(outDir);
        _tempDirs.Add(outDir);
        var smoPath = Path.Combine(outDir, "props.smo");
        await OverlayPorter.ExportRouteAsync(route, smoPath, "Props");

        var result = await OverlayPorter.ImportRouteAsync(smoPath, MakeTempExtractRoot(), factory);
        var imported = result.NewWidgets[0];

        Assert.Equal(100f, imported.X);
        Assert.Equal(200f, imported.Y);
        Assert.Equal(3, imported.Z);
        Assert.Equal(640, imported.Width);
        Assert.Equal(480, imported.Height);
        Assert.Equal(1.5f, imported.ScaleX);
        Assert.Equal(2f, imported.ScaleY);
        Assert.False(imported.Visibility);
    }
    
    [Fact]
    public async Task ExportRouteAsync_AbsoluteFileVariable_RewrittenToRelativeInManifest()
    {
        SetupServices();
        var htmlPath = MakeTempWidget("timerfx");
 
        var extDir = Path.Combine(Path.GetTempPath(), "OverlayPorterTests", Guid.NewGuid().ToString(), "sounds");
        Directory.CreateDirectory(extDir);
        _tempDirs.Add(Path.GetDirectoryName(extDir)!);
        var soundFile = Path.Combine(extDir, "alert.mp3");
        await File.WriteAllBytesAsync(soundFile, [0x49, 0x44, 0x33]);
 
        var route = new Route { Name = "Test", Width = 1920, Height = 1080 };
        var widget = new Widget("FX", htmlPath)
        {
            RouteId = route.Id,
            JsVariables = new List<JsVariable>
            {
                new JsVariable
                {
                    Name = "alertSound",
                    Value = soundFile, // absolute path
                    Type = WidgetVariableType.SoundFile,
                    WidgetId = Guid.NewGuid()
                }
            }
        };
        route.Widgets.Add(widget);
 
        var outPath = Path.Combine(Path.GetTempPath(), "OverlayPorterTests",
            Guid.NewGuid().ToString(), "fx.smo");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);
 
        await OverlayPorter.ExportRouteAsync(route, outPath, "FX");
 
        using var zip = ZipFile.OpenRead(outPath);
        using var ms = new MemoryStream();
        await zip.GetEntry("overlay.json")!.Open().CopyToAsync(ms);
        var doc = JsonDocument.Parse(ms.ToArray());
        var jsVars = doc.RootElement
            .GetProperty("widgets")[0]
            .GetProperty("jsVariables")
            .EnumerateArray()
            .ToList();
 
        var alertVar = jsVars.FirstOrDefault(v => v.GetProperty("name").GetString() == "alertSound");
        Assert.True(alertVar.ValueKind != JsonValueKind.Undefined, "alertSound variable not found in manifest");
        var rewrittenValue = alertVar.GetProperty("value").GetString()!;
        Assert.StartsWith("./", rewrittenValue);
        Assert.Contains("alert.mp3", rewrittenValue);
    }
 
    [Fact]
    public async Task ExportRouteAsync_AbsoluteFileVariable_FileIsInExternalFolderInZip()
    {
        SetupServices();
        var htmlPath = MakeTempWidget("timerfx2");
 
        var extDir = Path.Combine(Path.GetTempPath(), "OverlayPorterTests", Guid.NewGuid().ToString(), "assets");
        Directory.CreateDirectory(extDir);
        _tempDirs.Add(Path.GetDirectoryName(extDir)!);
        var imageFile = Path.Combine(extDir, "logo.png");
        await File.WriteAllBytesAsync(imageFile, [0x89, 0x50]);
 
        var route = new Route { Name = "Test", Width = 1920, Height = 1080 };
        var widget = new Widget("FX2", htmlPath)
        {
            RouteId = route.Id,
            JsVariables = new List<JsVariable>
            {
                new JsVariable
                {
                    Name = "logo",
                    Value = imageFile,
                    Type = WidgetVariableType.ImageFile,
                    WidgetId = Guid.NewGuid()
                }
            }
        };
        route.Widgets.Add(widget);
 
        var outPath = Path.Combine(Path.GetTempPath(), "OverlayPorterTests",
            Guid.NewGuid().ToString(), "fx2.smo");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);
 
        await OverlayPorter.ExportRouteAsync(route, outPath, "FX2");
 
        using var zip = ZipFile.OpenRead(outPath);
        var entries = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(entries, e => e.Contains("_external") && e.EndsWith("logo.png"));
    }
 
    [Fact]
    public async Task ExportRouteAsync_FolderPathVariable_AllFilesInExternalSubfolder()
    {
        SetupServices();
        var htmlPath = MakeTempWidget("timerFolder");
 
        var extDir = Path.Combine(Path.GetTempPath(), "OverlayPorterTests", Guid.NewGuid().ToString(), "media");
        Directory.CreateDirectory(extDir);
        _tempDirs.Add(Path.GetDirectoryName(extDir)!);
        await File.WriteAllTextAsync(Path.Combine(extDir, "a.mp4"), "fake");
        await File.WriteAllTextAsync(Path.Combine(extDir, "b.mp4"), "fake");
 
        var route = new Route { Name = "Test", Width = 1920, Height = 1080 };
        var widget = new Widget("Folder", htmlPath)
        {
            RouteId = route.Id,
            JsVariables =
            [
                new JsVariable
                {
                    Name = "videoFolder",
                    Value = extDir,
                    Type = WidgetVariableType.FolderPath,
                    WidgetId = Guid.NewGuid()
                }
            ]
        };
        route.Widgets.Add(widget);
 
        var outPath = Path.Combine(Path.GetTempPath(), "OverlayPorterTests",
            Guid.NewGuid().ToString(), "folder.smo");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);
 
        await OverlayPorter.ExportRouteAsync(route, outPath, "Folder");
 
        using var zip = ZipFile.OpenRead(outPath);
        var entries = zip.Entries.Select(e => e.FullName).ToList();
        
        Assert.Contains(entries, e => e.Contains("_external/videoFolder") && e.EndsWith("a.mp4"));
        Assert.Contains(entries, e => e.Contains("_external/videoFolder") && e.EndsWith("b.mp4"));
        using var ms = new MemoryStream();
        await zip.GetEntry("overlay.json")!.Open().CopyToAsync(ms);
        var doc = JsonDocument.Parse(ms.ToArray());
        var folderVar = doc.RootElement
            .GetProperty("widgets")[0]
            .GetProperty("jsVariables")
            .EnumerateArray()
            .First(v => v.GetProperty("name").GetString() == "videoFolder");
        Assert.StartsWith("./", folderVar.GetProperty("value").GetString()!);
    }
 
    [Fact]
    public async Task ExportRouteAsync_RelativeFileVariable_NotMovedToExternal()
    {
        SetupServices();
        var htmlPath = MakeTempWidget("relvar");
        var assetPath = Path.Combine(Path.GetDirectoryName(htmlPath)!, "asset.png");
        await File.WriteAllBytesAsync(assetPath, [0x89, 0x50]);
 
        var route = new Route { Name = "Test", Width = 1920, Height = 1080 };
        var widget = new Widget("Rel", htmlPath)
        {
            RouteId = route.Id,
            JsVariables =
            [
                new JsVariable
                {
                    Name = "img",
                    Value = "./asset.png",
                    Type = WidgetVariableType.ImageFile,
                    WidgetId = Guid.NewGuid()
                }
            ]
        };
        route.Widgets.Add(widget);
 
        var outPath = Path.Combine(Path.GetTempPath(), "OverlayPorterTests",
            Guid.NewGuid().ToString(), "rel.smo");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        _tempDirs.Add(Path.GetDirectoryName(outPath)!);
 
        await OverlayPorter.ExportRouteAsync(route, outPath, "Rel");
 
        using var zip = ZipFile.OpenRead(outPath);
        var entries = zip.Entries.Select(e => e.FullName).ToList();
        
        Assert.Contains(entries, e => e.EndsWith("asset.png") && !e.Contains("_external"));
        Assert.DoesNotContain(entries, e => e.Contains("_external") && e.EndsWith("asset.png"));
    }
 
    [Fact]
    public async Task ImportRouteAsync_ReImport_NewCssVariable_AddedToExistingWidget()
    {
        var factory = SetupServices();
        var htmlZipPath = "widgets/abc1/mytimer/widget.html";
        var extractRoot = MakeTempExtractRoot();
 
        object MakeManifest(object[] cssVars) => new
        {
            version = 1,
            route = new { name = "R", resolution = new { width = 1920, height = 1080 } },
            widget_folder_map = new { },
            widgets = new[]
            {
                new
                {
                    name = "W", htmlPath = htmlZipPath,
                    position = new { x = 0f, y = 0f, z = 0 },
                    size = new { width = 400, height = 300 },
                    scale = new { x = 1f, y = 1f },
                    visibility = true, docsUrl = (string?)null,
                    cssVariables = cssVars,
                    jsVariables = Array.Empty<object>()
                }
            }
        };
 
        var smo1 = MakeSmoFile(MakeManifest([]), new Dictionary<string, string> { [htmlZipPath] = "" });
        var first = await OverlayPorter.ImportRouteAsync(smo1, extractRoot, factory);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routes.Add(first.Route!);
            db.Widgets.AddRange(first.NewWidgets);
            await db.SaveChangesAsync();
        }
 
        var smo2 = MakeSmoFile(MakeManifest([
            new { name = "accent-color", value = "#00ff00" }
        ]), new Dictionary<string, string> { [htmlZipPath] = "" });
        var second = await OverlayPorter.ImportRouteAsync(smo2, extractRoot, factory);
 
        Assert.False(second.RouteIsNew);
        Assert.Empty(second.NewWidgets);
        Assert.Single(second.NewCssVariables);
        Assert.Equal("accent-color", second.NewCssVariables[0].Name);
        Assert.Equal("#00ff00", second.NewCssVariables[0].Value);
    }
 
    [Fact]
    public async Task ImportRouteAsync_ReImport_ExistingCssVariable_NotDuplicated()
    {
        var factory = SetupServices();
        var htmlZipPath = "widgets/abc1/mytimer/widget.html";
        var extractRoot = MakeTempExtractRoot();
 
        var cssVars = new object[] { new { name = "bg-color", value = "#ffffff" } };
        object manifest = new
        {
            version = 1,
            route = new { name = "R", resolution = new { width = 1920, height = 1080 } },
            widget_folder_map = new { },
            widgets = new[]
            {
                new
                {
                    name = "W", htmlPath = htmlZipPath,
                    position = new { x = 0f, y = 0f, z = 0 },
                    size = new { width = 400, height = 300 },
                    scale = new { x = 1f, y = 1f },
                    visibility = true, docsUrl = (string?)null,
                    cssVariables = cssVars,
                    jsVariables = Array.Empty<object>()
                }
            }
        };
        var smo = MakeSmoFile(manifest, new Dictionary<string, string> { [htmlZipPath] = "" });
 
        var first = await OverlayPorter.ImportRouteAsync(smo, extractRoot, factory);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routes.Add(first.Route!);
            db.Widgets.AddRange(first.NewWidgets);
            db.CssVariables.AddRange(first.NewWidgets.SelectMany(w => w.CssVariables));
            await db.SaveChangesAsync();
        }
 
        var second = await OverlayPorter.ImportRouteAsync(smo, extractRoot, factory);
 
        Assert.Empty(second.NewCssVariables);
        Assert.False(second.HasAnythingNew);
    }
}