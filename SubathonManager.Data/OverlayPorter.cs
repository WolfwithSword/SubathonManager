using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data.Overlays;
using SubathonManager.Data.Widgets;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Data;

public static class OverlayPorter
{
    private const int SegmentHashLength = 4;
    private const string ExternalFolder = "_external";
    private const string PacksFolder = "packs";
    private const string ManifestFileName = OverlayPackInstaller.ManifestFileName;
    public const string FormatVersion = "2";

    private static readonly JsonSerializerOptions SerializeOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    #region EXPORT

    public static async Task ExportRouteAsync(Route route, string outputPath, string exportName,
        HashSet<string>? excludedZipEntries = null, string version = "1", string appVersion = "",
        string author = "", List<string>? tags = null)
    {
        var widgets = route.Widgets.ToList();
        var plan = BuildExportPlan(widgets, excludedZipEntries);

        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (srcFile, zipEntry) in plan.FileCopies)
        {
            if (!WidgetFiles.Current.Exists(srcFile)) continue;
            if (!seen.Add(zipEntry)) continue;
            if (excludedZipEntries != null && excludedZipEntries.Contains(zipEntry)) continue;

            if (File.Exists(srcFile))
            {
                await archive.CreateEntryFromFileAsync(srcFile, zipEntry, CompressionLevel.Optimal);
                continue;
            }

            var bytes = WidgetFiles.Current.ReadAllBytes(srcFile);
            if (bytes == null) continue;
            var packedEntry = archive.CreateEntry(zipEntry, CompressionLevel.Optimal);
            await using var packedStream = await packedEntry.OpenAsync();
            await packedStream.WriteAsync(bytes);
        }

        var manifest = BuildManifest(route, widgets, plan, exportName, version, appVersion, author, tags);
        var manifestJson = JsonSerializer.Serialize(manifest, SerializeOptions);
        var manifestEntry = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal);
        await using var manifestStream = await manifestEntry.OpenAsync();
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
    
    public const string ResourcesFolder = "_resources";

    private static ExportPlan BuildExportPlan(List<Widget> widgets, HashSet<string>? excludedZipEntries = null)
    {
        var plan = new ExportPlan();
        bool IsKept(string zipEntry) => excludedZipEntries?.Contains(zipEntry) != true;

        foreach (var rel in ResourcePaths.EnumerateRelative())
        {
            string zipEntry = $"{ResourcesFolder}/{rel}";
            if (!IsKept(zipEntry)) continue;
            if (ResourcePaths.ToLocalPath(ResourcePaths.UrlPrefix + rel) is { } source)
                plan.FileCopies.Add((source, zipEntry));
        }

        var widgetRoots = widgets.Select(w => w.GetPath()).ToList();
        var zipRoots = GetZipWidgetRoots(widgetRoots);

        for (int i = 0; i < widgets.Count; i++)
        {
            var widget = widgets[i];
            string widgetRoot = widgetRoots[i];
            string zipWidgetRoot = zipRoots[i];
            var baseFolder = GetWidgetBaseFolder(zipWidgetRoot);

            plan.WidgetFolderMap[widget.Id] = zipWidgetRoot;

            bool isPacked = WidgetPackPaths.TryResolve(widget.HtmlPath, out var packFile, out var packEntry,
                out var packId, out var packVersion);

            if (isPacked)
            {
                string packZipPath = $"{PacksFolder}/{packId}/{packVersion}{WidgetPackPaths.PackExtension}";
                plan.FileCopies.Add((packFile, packZipPath));
                plan.WidgetPacks[widget.Id] = new PackReference(packId, packVersion, packEntry, packZipPath);
            }
            else if (widget.Type.IsAsset())
            {
                if (WidgetFiles.Current.Exists(widget.HtmlPath))
                {
                    string fileName = Path.GetFileName(widget.HtmlPath);
                    plan.FileCopies.Add((widget.HtmlPath, $"{zipWidgetRoot}/{fileName}"));
                }
            }
            else
            {
                foreach (var file in WidgetFiles.Current.EnumerateFiles(widgetRoot))
                {
                    string relative = Path.GetRelativePath(widgetRoot, file).Replace('\\', '/');
                    plan.FileCopies.Add((file, $"{zipWidgetRoot}/{relative}"));
                }
            }

            foreach (var jsVar in widget.JsVariables)
            {
                if (!((WidgetVariableType?)jsVar.Type).IsFileVariable()) continue;
                if (string.IsNullOrWhiteSpace(jsVar.Value)) continue;

                if (ResourcePaths.RelativeFromUrl(jsVar.Value) is { } resourceRel)
                {
                    if (!IsKept($"{ResourcesFolder}/{resourceRel}")) continue;
                    string upToRoot = string.Concat(
                        Enumerable.Repeat("../", zipWidgetRoot.Split('/').Length));
                    SetRewrite(plan.VariableRewrites, widget.Id, jsVar.Name,
                        $"{upToRoot}{ResourcesFolder}/{resourceRel}");
                    continue;
                }

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

    private static JsonElement BuildManifest(Route route, List<Widget> widgets, ExportPlan plan, string exportName,
        string version = "1", string appVersion = "", string author = "", List<string>? tags = null)
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

            var pack = plan.WidgetPacks.TryGetValue(w.Id, out var packRef)
                ? new { id = packRef.PackId, version = packRef.Version, entry = packRef.Entry, file = packRef.ZipPath }
                : null;

            return new
            {
                id = w.Id,
                name = w.Name,
                htmlPath = htmlZipRelPath,
                pack,
                type = w.Type.ToString(),
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
            // formatversion helps determine some behaviour from old stuff
            version = version,
            format_version = FormatVersion,
            app_version = appVersion,
            exported_at = DateTime.UtcNow,
            route = new
            {
                id = route.Id,
                name = exportName,
                author = author,
                overlay_version = version,
                tags = tags ?? new List<string>(),
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
        await ZipFile.ExtractToDirectoryAsync(smoPath, extractDir, overwriteFiles: true);

        return await ImportExtractedRouteAsync(extractDir, factory, archiveName);
    }
    
    public static async Task<ImportResult> ImportExtractedRouteAsync(
        string extractDir,
        IDbContextFactory<AppDbContext> factory,
        string fallbackName,
        string? routeNameOverride = null)
    {
        string manifestPath = Path.Combine(extractDir, ManifestFileName);
        if (!File.Exists(manifestPath)) return ImportResult.Fail("overlay.json not found in archive");

        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        using var doc = JsonDocument.Parse(manifestJson);
        var root = doc.RootElement;

        await using var db = await factory.CreateDbContextAsync();

        var routeEl = root.GetProperty("route");
        string routeName = routeNameOverride
                           ?? routeEl.GetProperty("name").GetString()
                           ?? fallbackName;

        var manifestWidgets = root.GetProperty("widgets").EnumerateArray()
            .Select(wEl => ResolveManifestWidget(wEl, extractDir))
            .ToList();

        var existing = db.Widgets
            .Select(w => new { w.Id, w.HtmlPath, w.RouteId })
            .ToList()
            .Select(w => new
            {
                w.Id,
                w.HtmlPath,
                w.RouteId,
                PackFolder = WidgetPackPaths.Resolve(w.HtmlPath)?.PackFolderStr
            })
            .ToList();

        Guid? matchedRouteId = null;
        foreach (var mw in manifestWidgets)
        {
            var hit = existing.FirstOrDefault(w =>
                mw.PackFolder != null
                    ? string.Equals(w.PackFolder, mw.PackFolder, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(w.HtmlPath, mw.HtmlAbsPath, StringComparison.OrdinalIgnoreCase));

            if (hit == null) continue;
            matchedRouteId = hit.RouteId;
            break;
        }

        if (matchedRouteId == null)
        {
            string baseName = OverlayPackPaths.BaseRouteName(routeName);
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                matchedRouteId = db.Routes
                    .Select(r => new { r.Id, r.Name })
                    .ToList()
                    .FirstOrDefault(r => string.Equals(
                        OverlayPackPaths.BaseRouteName(r.Name), 
                        baseName, StringComparison.OrdinalIgnoreCase))
                    ?.Id;
            }
        }

        Route? route = null;
        bool routeIsNew;

        if (matchedRouteId != null)
        {
            route = await db.Routes
                .Include(r => r.Widgets).ThenInclude(w => w.JsVariables)
                .Include(r => r.Widgets).ThenInclude(w => w.CssVariables)
                .FirstOrDefaultAsync(r => r.Id == matchedRouteId);
        }

        if (route == null)
        {
            route = BuildNewRoute(routeEl, routeName);
            routeIsNew = true;
        }
        else
        {
            routeIsNew = false;
        }

        var newWidgets = new List<Widget>();
        var newCssVariables = new List<CssVariable>();
        var newJsVariables = new List<JsVariable>();
        var repointedWidgets = new List<Widget>();

        foreach (var mw in manifestWidgets)
        {
            var wEl = mw.Element;
            string htmlAbsPath = mw.HtmlAbsPath;

            string widgetExtractFolder = mw.PackFolder != null
                ? mw.OverlayFolder
                : Path.GetDirectoryName(htmlAbsPath)!;

            Widget? existingWidget = routeIsNew ? null
                : route.Widgets.FirstOrDefault(w => MatchesManifestWidget(w, mw));

            if (existingWidget != null &&
                !string.Equals(existingWidget.HtmlPath, htmlAbsPath, StringComparison.OrdinalIgnoreCase))
            {
                existingWidget.HtmlPath = htmlAbsPath;
                repointedWidgets.Add(existingWidget);
            }

            if (existingWidget == null)
            {
                WidgetType widgetType = WidgetType.Html;
                if (wEl.TryGetProperty("type", out var typeEl))
                    Enum.TryParse(typeEl.GetString(), ignoreCase: true, out widgetType);
                else
                    widgetType = WidgetTypeHelper.DetectFromPath(htmlAbsPath);

                var widget = new Widget(wEl.GetProperty("name").GetString() ?? "Imported Widget", htmlAbsPath)
                {
                    Id = Guid.NewGuid(), RouteId = route.Id,
                    Type = widgetType,
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

                if (widgetType == WidgetType.Html)
                {
                    foreach (var v in wEl.GetProperty("cssVariables").EnumerateArray().Select(cssEl =>
                                 CssVariable.FromJson(cssEl, widget.Id)).OfType<CssVariable>())
                    {
                        widget.CssVariables.Add(v);
                    }
                    foreach (var v in wEl.GetProperty("jsVariables").EnumerateArray().Select(jsEl =>
                                 BuildJsVariable(jsEl, widget.Id, widgetExtractFolder, mw.PackFolder != null))
                                 .OfType<JsVariable>())
                    {
                        widget.JsVariables.Add(v);
                    }
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
                    var v = BuildJsVariable(jsEl, existingWidget.Id, widgetExtractFolder, mw.PackFolder != null);
                    if (v != null) newJsVariables.Add(v);
                }
            }
        }

        var repointedIds = new HashSet<Guid>(repointedWidgets.Select(w => w.Id));
        foreach (var mw in manifestWidgets.Where(m => m.PackFolder != null))
        {
            var strays = existing.Where(w =>
                string.Equals(w.PackFolder, mw.PackFolder, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(w.HtmlPath, mw.HtmlAbsPath, StringComparison.OrdinalIgnoreCase) &&
                !repointedIds.Contains(w.Id));

            foreach (var stray in strays)
            {
                var tracked = await db.Widgets.FirstOrDefaultAsync(w => w.Id == stray.Id);
                if (tracked == null || !repointedIds.Add(tracked.Id)) continue;
                tracked.HtmlPath = mw.HtmlAbsPath;
                repointedWidgets.Add(tracked);
            }
        }

        bool renameMerged = !routeIsNew && !string.Equals(route!.Name, routeName, StringComparison.Ordinal);

        return new ImportResult
        {
            Route = routeIsNew ? route : null,
            NewWidgets = newWidgets,
            NewCssVariables = newCssVariables,
            NewJsVariables = newJsVariables,
            RepointedWidgets = repointedWidgets,
            RouteIsNew = routeIsNew,
            MergedRouteId = routeIsNew ? null : route!.Id,
            MergedRouteName = renameMerged ? routeName : null
        };
    }

    private record ManifestWidget(JsonElement Element, string HtmlAbsPath, string OverlayFolder, string? PackFolder);

    private static ManifestWidget ResolveManifestWidget(JsonElement wEl, string extractDir)
    {
        string htmlZipRelPath = wEl.TryGetProperty("htmlPath", out var hp) ? hp.GetString() ?? "" : "";
        
        string overlayPath = Path.GetFullPath(
            Path.Combine(extractDir, htmlZipRelPath.Replace('/', Path.DirectorySeparatorChar)));
        string overlayFolder = Path.GetDirectoryName(overlayPath) ?? extractDir;

        if (!wEl.TryGetProperty("pack", out var packEl) || packEl.ValueKind != JsonValueKind.Object)
            return new ManifestWidget(wEl, overlayPath, overlayFolder, null);

        string file = packEl.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(file))
            return new ManifestWidget(wEl, overlayPath, overlayFolder, null);

        string smwPath = Path.GetFullPath(
            Path.Combine(extractDir, file.Replace('/', Path.DirectorySeparatorChar)));

        var packManifest = WidgetPackInstaller.ReadManifest(smwPath);
        string entry = packManifest?.Entry ?? string.Empty;
        if (string.IsNullOrWhiteSpace(entry))
            entry = packEl.TryGetProperty("entry", out var en) ? en.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(entry) || !File.Exists(smwPath))
            return new ManifestWidget(wEl, overlayPath, overlayFolder, null);

        string mountRoot = Path.Combine(
            Path.GetDirectoryName(smwPath)!,
            Path.GetFileNameWithoutExtension(smwPath));

        string packFolder = Path.GetDirectoryName(smwPath)!;
        string htmlPath = WidgetPackPaths.EntryPathIn(mountRoot, entry);

        return new ManifestWidget(wEl, htmlPath, overlayFolder, packFolder);
    }
    
    private static bool MatchesManifestWidget(Widget widget, ManifestWidget mw)
    {
        if (mw.PackFolder != null)
        {
            var location = WidgetPackPaths.Resolve(widget.HtmlPath);
            return location != null &&
                   string.Equals(location.PackFolderStr, mw.PackFolder, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(widget.HtmlPath, mw.HtmlAbsPath, StringComparison.OrdinalIgnoreCase);
    }

    private static Route BuildNewRoute(JsonElement routeEl, string routeName) => new Route
    {
        Id = Guid.NewGuid(),
        Name = routeName,
        Width = routeEl.GetProperty("resolution").GetProperty("width").GetInt32(),
        Height = routeEl.GetProperty("resolution").GetProperty("height").GetInt32(),
    };

    private static JsVariable? BuildJsVariable(JsonElement jsEl, Guid widgetId, string widgetFolder, bool isPacked)
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

        if (!resolvedValue.StartsWith(normWidgetFolder, StringComparison.OrdinalIgnoreCase))
        {
            v.Value = resolvedValue;
            return v;
        }

        string relative = resolvedValue[normWidgetFolder.Length..];
        bool bundledByOverlay = isPacked &&
                                relative.StartsWith(ExternalFolder + "/", 
                                    StringComparison.OrdinalIgnoreCase);

        v.Value = bundledByOverlay ? resolvedValue : "./" + relative;

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

    private static string SanitizeName(string name) => SafeFileName.Sanitize(name);
    #endregion

    #region TYPES
    public class ImportResult
    {
        public Route? Route { get; init; }
        public List<Widget> NewWidgets { get; init; } = [];
        public List<CssVariable> NewCssVariables { get; init; } = [];
        public List<JsVariable> NewJsVariables { get; init; } = [];
        public bool RouteIsNew { get; init; }
        public bool Failed { get; init; }
        public string? FailReason { get; init; }

        public List<Widget> RepointedWidgets { get; init; } = [];
        public Guid? MergedRouteId { get; init; }
        public string? MergedRouteName { get; init; }

        public bool HasAnythingNew =>
            RouteIsNew || NewWidgets.Count > 0 || NewCssVariables.Count > 0 || NewJsVariables.Count > 0
            || RepointedWidgets.Count > 0
            || (MergedRouteName != null && MergedRouteId != null);
        public static ImportResult Fail(string reason) =>
            new ImportResult { Failed = true, FailReason = reason };
    }

    private record PackReference(string PackId, string Version, string Entry, string ZipPath);

    private class ExportPlan
    {
        public Dictionary<Guid, string> WidgetFolderMap { get; } = new(); // debug helper
        public Dictionary<Guid, PackReference> WidgetPacks { get; } = new();
        public List<(string Src, string ZipEntry)> FileCopies { get; } = [];
        public Dictionary<Guid, Dictionary<string, string>> VariableRewrites { get; } = new();
    }
    #endregion
}