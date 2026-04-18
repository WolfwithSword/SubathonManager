using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Data;

public static class OverlayPorter
{
    private const int SegmentHashLength = 4;
    private const string ExternalFolder = "_external";
    private const string ManifestFileName = "overlay.json";

    private static readonly JsonSerializerOptions SerializeOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    #region EXPORT

    public static async Task ExportRouteAsync(Route route, string outputPath, string exportName, HashSet<string>? excludedZipEntries = null, string version = "1", string appVersion = "")
    {
        var widgets = route.Widgets.ToList();
        var plan = BuildExportPlan(widgets);

        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (srcFile, zipEntry) in plan.FileCopies)
        {
            if (!File.Exists(srcFile)) continue;
            if (!seen.Add(zipEntry)) continue;
            if (excludedZipEntries != null && excludedZipEntries.Contains(zipEntry)) continue;
            archive.CreateEntryFromFile(srcFile, zipEntry, CompressionLevel.Optimal);
        }

        var manifest = BuildManifest(route, widgets, plan, exportName, version, appVersion);
        var manifestJson = JsonSerializer.Serialize(manifest, SerializeOptions);
        var manifestEntry = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
        await using var manifestStream = manifestEntry.Open();
        await manifestStream.WriteAsync(Encoding.UTF8.GetBytes(manifestJson));
        OpenExportFolder(outputPath);
    }

    [ExcludeFromCodeCoverage]
    private static void OpenExportFolder(string outputPath)
    {
        bool isTest =
            AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.FullName!.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
        if (isTest) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetDirectoryName(Path.GetFullPath(outputPath)),
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch {/**/}
    }
    
    private static ExportPlan BuildExportPlan(List<Widget> widgets)
    {
        var plan = new ExportPlan();

        var widgetRoots = widgets.Select(w => w.GetPath()).ToList();
        var zipRoots = GetZipWidgetRoots(widgetRoots);

        for (int i = 0; i < widgets.Count; i++)
        {
            var widget = widgets[i];
            string widgetRoot = widgetRoots[i];
            string zipWidgetRoot = zipRoots[i];
            var baseFolder = GetWidgetBaseFolder(zipWidgetRoot);

            plan.WidgetFolderMap[widget.Id] = zipWidgetRoot;

            if (Directory.Exists(widgetRoot))
            {
                foreach (var file in Directory.EnumerateFiles(widgetRoot, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(widgetRoot, file).Replace('\\', '/');
                    plan.FileCopies.Add((file, $"{zipWidgetRoot}/{relative}"));
                }
            }

            foreach (var jsVar in widget.JsVariables)
            {
                if (!((WidgetVariableType?)jsVar.Type).IsFileVariable()) continue;
                if (string.IsNullOrWhiteSpace(jsVar.Value)) continue;

                bool isAbsolute = !jsVar.Value.StartsWith("./") && !jsVar.Value.StartsWith("../")
                                  && Path.IsPathRooted(jsVar.Value);
                if (!isAbsolute) continue;

                bool isFolderType = jsVar.Type == WidgetVariableType.FolderPath;
                if (isFolderType && Directory.Exists(jsVar.Value))
                {
                    string varFolderName = SanitizeName(jsVar.Name);
                    foreach (var file in Directory.EnumerateFiles(jsVar.Value, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.GetRelativePath(jsVar.Value, file).Replace('\\', '/');
                        plan.FileCopies.Add((file, $"{zipWidgetRoot}/{ExternalFolder}/{varFolderName}/{relative}"));
                    }
                    SetRewrite(plan.VariableRewrites, widget.Id, jsVar.Name, $"./{ExternalFolder}/{varFolderName}");
                }
                else if (!isFolderType && File.Exists(jsVar.Value))
                {
                    string fileName = Path.GetFileName(jsVar.Value);
                    plan.FileCopies.Add((jsVar.Value, $"{zipWidgetRoot}/{ExternalFolder}/{fileName}"));
                    SetRewrite(plan.VariableRewrites, widget.Id, jsVar.Name, $"./{ExternalFolder}/{fileName}");
                }
            }
        }

        return plan;
    }

    private static JsonElement BuildManifest(Route route, List<Widget> widgets, ExportPlan plan, string exportName, string version = "1", string appVersion = "")
    {
        if (string.IsNullOrWhiteSpace(appVersion)) appVersion = AppServices.AppVersion;
        var widgetList = widgets.Select(w =>
        {
            if (!plan.WidgetFolderMap.TryGetValue(w.Id, out var zipWidgetRoot)) return null!;
            string htmlFileName = Path.GetFileName(w.HtmlPath);
            string htmlZipRelPath = $"{zipWidgetRoot}/{htmlFileName}";

            var rewrites = plan.VariableRewrites.TryGetValue(w.Id, out var r) ? r : new();
            var jsVars = w.JsVariables.Select(v =>
            {
                string value = rewrites.TryGetValue(v.Name, out var rewritten) ? rewritten : v.Value;
                return new { name = v.Name, value, type = v.Type };
            });

            return new
            {
                id = w.Id,
                name = w.Name,
                htmlPath = htmlZipRelPath,
                position = new { x = w.X, y = w.Y, z = w.Z },
                size = new { width = w.Width, height = w.Height },
                scale = new { x = w.ScaleX, y = w.ScaleY },
                visibility = w.Visibility,
                docsUrl = w.DocsUrl,
                cssVariables = w.CssVariables.Select(v => new { name = v.Name, value = v.Value }),
                jsVariables = jsVars
            };
        });

        var obj = new
        {
            version = version,
            app_version = appVersion,
            exported_at = DateTime.UtcNow,
            route = new
            {
                id = route.Id,
                name = exportName,
                resolution = new { width = route.Width, height = route.Height },
                created = route.CreatedTimestamp,
                updated = route.UpdatedTimestamp
            },
            // helpful for debug
            widget_folder_map = plan.WidgetFolderMap.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value
            ),
            widgets = widgetList
        };

        return JsonSerializer.SerializeToElement(obj);
    }
    #endregion

    #region IMPORT

    public static async Task<ImportResult> ImportRouteAsync(
        string smoPath,
        string extractRoot,
        IDbContextFactory<AppDbContext> factory)
    {
        string archiveName = Path.GetFileNameWithoutExtension(smoPath);
        string extractDir = Path.Combine(extractRoot, SanitizeName(archiveName));
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(smoPath, extractDir, overwriteFiles: true);

        string manifestPath = Path.Combine(extractDir, ManifestFileName);
        if (!File.Exists(manifestPath)) return ImportResult.Fail("overlay.json not found in archive");

        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        using var doc = JsonDocument.Parse(manifestJson);
        var root = doc.RootElement;

        await using var db = await factory.CreateDbContextAsync();

        var routeEl = root.GetProperty("route");
        string routeName = routeEl.GetProperty("name").GetString() ?? archiveName;

        var existingWidgetPaths = new HashSet<string>(
            db.Widgets.Select(w => w.HtmlPath),
            StringComparer.OrdinalIgnoreCase);

        var manifestWidgets = root.GetProperty("widgets").EnumerateArray()
            .Select(wEl =>
            {
                string htmlZipRelPath = wEl.GetProperty("htmlPath").GetString() ?? "";
                string htmlAbsPath = Path.GetFullPath(
                    Path.Combine(extractDir, htmlZipRelPath.Replace('/', Path.DirectorySeparatorChar)));
                return (wEl, htmlAbsPath);
            })
            .ToList();

        bool anyWidgetExists = manifestWidgets.Any(w => existingWidgetPaths.Contains(w.htmlAbsPath));

        Route? route;
        bool routeIsNew;

        if (anyWidgetExists)
        {
            string? matchedPath = manifestWidgets
                .Select(w => w.htmlAbsPath)
                .FirstOrDefault(p => existingWidgetPaths.Contains(p));

            route = await db.Routes
                .Include(r => r.Widgets).ThenInclude(w => w.JsVariables)
                .Include(r => r.Widgets).ThenInclude(w => w.CssVariables)
                .FirstOrDefaultAsync(r => r.Widgets.Any(w => w.HtmlPath == matchedPath));

            if (route == null) { route = BuildNewRoute(routeEl, routeName); routeIsNew = true; }
            else routeIsNew = false;
        }
        else
        {
            route = BuildNewRoute(routeEl, routeName);
            routeIsNew = true;
        }

        var newWidgets = new List<Widget>();
        var newCssVariables = new List<CssVariable>();
        var newJsVariables = new List<JsVariable>();

        foreach (var (wEl, htmlAbsPath) in manifestWidgets)
        {
            string widgetExtractFolder = Path.GetDirectoryName(htmlAbsPath)!;
            Widget? existingWidget = routeIsNew ? null
                : route.Widgets.FirstOrDefault(w =>
                    string.Equals(w.HtmlPath, htmlAbsPath, StringComparison.OrdinalIgnoreCase));

            if (existingWidget == null)
            {
                var widget = new Widget(wEl.GetProperty("name").GetString() ?? "Imported Widget", htmlAbsPath)
                {
                    Id = Guid.NewGuid(), RouteId = route.Id,
                    Visibility = wEl.GetProperty("visibility").GetBoolean(),
                    DocsUrl = wEl.TryGetProperty("docsUrl", out var du) ? du.GetString() : null
                };
                
                var pos = wEl.GetProperty("position");
                widget.X = pos.GetProperty("x").GetSingle();
                widget.Y = pos.GetProperty("y").GetSingle();
                widget.Z = pos.GetProperty("z").GetInt32();
                var size = wEl.GetProperty("size");
                widget.Width = size.GetProperty("width").GetInt32();
                widget.Height = size.GetProperty("height").GetInt32();
                var scale = wEl.GetProperty("scale");
                widget.ScaleX = scale.GetProperty("x").GetSingle();
                widget.ScaleY = scale.GetProperty("y").GetSingle();
                
                foreach (var v in wEl.GetProperty("cssVariables").EnumerateArray().Select(cssEl =>
                             CssVariable.FromJson(cssEl, widget.Id)).OfType<CssVariable>())
                {
                    widget.CssVariables.Add(v);
                }
                foreach (var v in wEl.GetProperty("jsVariables").EnumerateArray().Select(jsEl =>
                             BuildJsVariable(jsEl, widget.Id, widgetExtractFolder)).OfType<JsVariable>())
                {
                    widget.JsVariables.Add(v);
                }
                
                newWidgets.Add(widget);
            }
            else
            {
                var existingCssNames = new HashSet<string>(existingWidget.CssVariables.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var cssEl in wEl.GetProperty("cssVariables").EnumerateArray())
                {
                    string? name = cssEl.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name == null || existingCssNames.Contains(name)) continue;
                    var v = CssVariable.FromJson(cssEl, existingWidget.Id);
                    if (v != null) newCssVariables.Add(v);
                }
                
                var existingJsNames = new HashSet<string>(existingWidget.JsVariables.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var jsEl in wEl.GetProperty("jsVariables").EnumerateArray())
                {
                    string? name = jsEl.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name == null || existingJsNames.Contains(name)) continue;
                    var v = BuildJsVariable(jsEl, existingWidget.Id, widgetExtractFolder);
                    if (v != null) newJsVariables.Add(v);
                }
            }
        }

        return new ImportResult
        {
            Route = routeIsNew ? route : null,
            NewWidgets = newWidgets,
            NewCssVariables = newCssVariables,
            NewJsVariables = newJsVariables,
            RouteIsNew = routeIsNew
        };
    }

    private static Route BuildNewRoute(JsonElement routeEl, string fallbackName) => new Route
    {
        Id = Guid.NewGuid(),
        Name = routeEl.TryGetProperty("name", out var n) ? n.GetString() ?? fallbackName : fallbackName,
        Width = routeEl.GetProperty("resolution").GetProperty("width").GetInt32(),
        Height = routeEl.GetProperty("resolution").GetProperty("height").GetInt32(),
    };

    private static JsVariable? BuildJsVariable(JsonElement jsEl, Guid widgetId, string widgetFolder)
    {
        var v = JsVariable.FromJson(jsEl, widgetId);
        if (v == null) return null;

        if (!((WidgetVariableType?)v.Type).IsFileVariable() || string.IsNullOrWhiteSpace(v.Value))
            return v;

        string resolvedValue = v.Value.StartsWith("./") || v.Value.StartsWith("../")
            ? Path.GetFullPath(Path.Combine(widgetFolder, v.Value.Replace('/', Path.DirectorySeparatorChar)))
            : v.Value;

        resolvedValue = resolvedValue.Replace('\\', '/');
        string normWidgetFolder = widgetFolder.Replace('\\', '/').TrimEnd('/') + "/";

        v.Value = resolvedValue.StartsWith(normWidgetFolder, StringComparison.OrdinalIgnoreCase)
            ? "./" + resolvedValue[normWidgetFolder.Length..]
            : resolvedValue;

        return v;
    }

    #endregion

    #region HELPERS

    private static void SetRewrite(Dictionary<Guid, Dictionary<string, string>> rewrites, Guid widgetId, string varName, string value)
    {
        if (!rewrites.TryGetValue(widgetId, out var inner))
        {
            inner = new Dictionary<string, string>();
            rewrites[widgetId] = inner;
        }
        inner[varName] = value;
    }

    private static string GetWidgetBaseFolder(string zipWidgetRoot)
    {
        int lastSlash = zipWidgetRoot.LastIndexOf('/');
        return lastSlash > 0 ? zipWidgetRoot[..lastSlash] : zipWidgetRoot;
    }

    public static List<string> GetZipWidgetRoots(List<string> absoluteFolderPaths)
    {
        if (absoluteFolderPaths.Count == 0) return new();

        var segmentSets = absoluteFolderPaths
            .Select(p => p.Replace('\\', '/').TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        static bool IsPrefix(string[] parent, string[] child)
        {
            if (parent.Length > child.Length) return false;
            return !parent.Where((t, i) => !string.Equals(t, child[i], StringComparison.OrdinalIgnoreCase)).Any();
        }

        var roots = new List<string[]>(segmentSets.Count);

        foreach (var current in segmentSets)
        {
            string[]? bestRoot = null;

            foreach (var candidate in segmentSets)
            {
                if (ReferenceEquals(candidate, current)) continue;

                if (IsPrefix(candidate, current))
                {
                    if (bestRoot == null || candidate.Length > bestRoot.Length)
                        bestRoot = candidate;
                }
            }

            roots.Add(bestRoot ?? current);
        }

        var results = new List<string>(segmentSets.Count);

        for (int i = 0; i < segmentSets.Count; i++)
        {
            var segment = segmentSets[i];
            var root = roots[i];

            string leaf = SanitizeName(segment[^1]);

            var sb = new StringBuilder("widgets");

            var parentParts = root.SkipLast(1).ToArray();

            string bucketSource = parentParts.Length > 0
                ? string.Join("/", parentParts).ToLowerInvariant()
                : root[^1].ToLowerInvariant();

            sb.Append('/');
            sb.Append(HashSegment(bucketSource));

            int start = root.Length - 1;

            for (int j = start; j < segment.Length; j++)
            {
                sb.Append('/');
                sb.Append(SanitizeName(segment[j]));
            }

            results.Add(sb.ToString());
        }

        return results;
    }

    private static string HashSegment(string segment)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(segment));
        return Convert.ToHexString(bytes)[..SegmentHashLength].ToLowerInvariant();
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
    #endregion

    #region TYPES
    public class ImportResult
    {
        public Route? Route { get; init; }
        public List<Widget> NewWidgets { get; init; } = new();
        public List<CssVariable> NewCssVariables { get; init; } = new();
        public List<JsVariable> NewJsVariables { get; init; } = new();
        public bool RouteIsNew { get; init; }
        public bool Failed { get; init; }
        public string? FailReason { get; init; }
        public bool HasAnythingNew =>
            RouteIsNew || NewWidgets.Count > 0 || NewCssVariables.Count > 0 || NewJsVariables.Count > 0;
        public static ImportResult Fail(string reason) =>
            new ImportResult { Failed = true, FailReason = reason };
    }

    private class ExportPlan
    {
        public Dictionary<Guid, string> WidgetFolderMap { get; } = new(); // debug helper
        public List<(string Src, string ZipEntry)> FileCopies { get; } = new();
        public Dictionary<Guid, Dictionary<string, string>> VariableRewrites { get; } = new();
    }
    #endregion
}