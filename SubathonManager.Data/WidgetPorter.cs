using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data.Widgets;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Data;

public static partial class WidgetPorter
{
    public const string ManifestFileName = WidgetPackInstaller.ManifestFileName;
    public const string ContentFolder = "content";
    public const string ExternalFolder = "_external";
    public const string SharedFolder = "_shared";
    public const string FormatVersion = "1";

    private static readonly JsonSerializerOptions MetaOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    #region TYPES

    public enum SmwEntryKind
    {
        Manifest,
        Entry,
        Css,
        Meta,
        Asset,
        External
    }

    public class SmwEntry
    {
        public required string ZipEntry { get; init; }
        public string? AbsSource { get; init; }
        public SmwEntryKind Kind { get; init; }
        public bool DefaultSelected { get; set; }
        public bool Locked { get; init; }
        public Func<SmwExportOptions, byte[]>? Generator { get; init; }
        public bool InUse { get; init; }
        public string? UsageHint { get; init; }
    }

    public class SmwExportOptions
    {
        public string Name { get; init; } = string.Empty;
        public string Author { get; init; } = string.Empty;
        public string Group { get; init; } = string.Empty;

        public string Version { get; init; } = "1.0.0";
        public string AppVersion { get; init; } = string.Empty;

        public List<string> Tags { get; init; } = [];

        public string PreviewImagePath { get; init; } = string.Empty;
    }

    public static readonly string[] PreviewExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    public static string PreviewEntryName(string sourcePath)
    {
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!PreviewExtensions.Contains(ext)) ext = ".png";
        return "preview" + ext;
    }
    
    public static string? ExtractExistingPreview(Widget widget)
    {
        var location = WidgetPackPaths.Resolve(widget.HtmlPath);
        if (location == null) return null;

        var manifest = WidgetPackInstaller.ReadManifest(location.PackFileStr);
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.PreviewImage)) return null;

        var bytes = WidgetFiles.Current.ReadAllBytes(
            WidgetPackPaths.EntryPathIn(location.MountRootStr, manifest.PreviewImage));
        if (bytes == null) return null;

        try
        {
            string temp = Path.Combine(Path.GetTempPath(),
                $"smw-preview-{Guid.NewGuid():N}{Path.GetExtension(manifest.PreviewImage)}");
            File.WriteAllBytes(temp, bytes);
            return temp;
        }
        catch
        {
            return null;
        }
    }


    public static List<string> ParseTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(tag => seen.Add(tag))
            .ToList();
    }

    public class ExportPlan
    {
        public List<SmwEntry> Entries { get; } = [];
        public Dictionary<string, string> VariableRewrites { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string EntryZipPath { get; set; } = string.Empty;

        public Dictionary<string, (string ZipEntry, string Value)> OptionalRewrites { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        public bool IsSelected(string zipEntry)
            => Entries.Any(e => e.ZipEntry.Equals(zipEntry, StringComparison.OrdinalIgnoreCase)
                                && (e.Locked || e.DefaultSelected));

        public string ResolveVarValue(JsVariable jsVar)
        {
            if (VariableRewrites.TryGetValue(jsVar.Name, out var rewritten)) return rewritten;
            if (OptionalRewrites.TryGetValue(jsVar.Name, out var optional) && IsSelected(optional.ZipEntry))
                return optional.Value;
            return jsVar.Value ?? string.Empty;
        }
    }

    #endregion

    #region PLAN

    public static ExportPlan BuildPlan(Widget widget)
    {
        var plan = new ExportPlan();
        if (widget.Type.IsAsset() || !WidgetFiles.Current.Exists(widget.HtmlPath)) return plan;

        string widgetRoot = widget.GetPath();
        string htmlEntry = $"{ContentFolder}/{Path.GetFileName(widget.HtmlPath)}";
        plan.EntryZipPath = htmlEntry;

        var cssPaths = widget.GetLinkedCssPaths();
        var cssZipMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var htmlLinkRewrites = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var cssPath in cssPaths)
        {
            string? entry = ToContentEntry(widgetRoot, cssPath);
            if (entry == null)
            {
                entry = $"{ContentFolder}/{SharedFolder}/{Path.GetFileName(cssPath)}";
                string original = GetHtmlLinkFor(widget, cssPath);
                if (!string.IsNullOrEmpty(original))
                    htmlLinkRewrites[original] = $"./{SharedFolder}/{Path.GetFileName(cssPath)}";
            }
            cssZipMap[cssPath] = entry;
        }

        plan.Entries.Add(new SmwEntry
        {
            ZipEntry = ManifestFileName,
            Kind = SmwEntryKind.Manifest,
            DefaultSelected = true,
            Locked = true,
            Generator = opts => Encoding.UTF8.GetBytes(BuildManifest(widget, plan, opts))
        });

        string htmlText = WidgetFiles.Current.ReadAllText(widget.HtmlPath) ?? string.Empty;
        var hardcodedResources = new HashSet<string>(ResourcePaths.FindReferences(htmlText),
            StringComparer.OrdinalIgnoreCase);
        foreach (var cssPath in cssPaths)
            hardcodedResources.UnionWith(ResourcePaths.FindReferences(WidgetFiles.Current.ReadAllText(cssPath)));

        Func<SmwExportOptions, byte[]>? htmlGenerator = null;
        if (htmlLinkRewrites.Count > 0 || hardcodedResources.Count > 0)
            htmlGenerator = _ => Encoding.UTF8.GetBytes(
                RewriteResourceUrls(RewriteHtmlLinks(htmlText, htmlLinkRewrites), htmlEntry, plan));

        plan.Entries.Add(new SmwEntry
        {
            ZipEntry = htmlEntry,
            AbsSource = widget.HtmlPath,
            Kind = SmwEntryKind.Entry,
            DefaultSelected = true,
            Locked = true,
            Generator = htmlGenerator
        });

        plan.Entries.Add(new SmwEntry
        {
            ZipEntry = $"{htmlEntry}.json",
            AbsSource = WidgetFiles.Current.Exists(widget.HtmlPath + ".json") ? widget.HtmlPath + ".json" : null,
            Kind = SmwEntryKind.Meta,
            DefaultSelected = true,
            Generator = opts => Encoding.UTF8.GetBytes(BuildWidgetMetaJson(widget, plan, opts))
        });

        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cssPath in cssPaths)
        {
            string cssEntry = cssZipMap[cssPath];
            var varsInFile = ClaimCssVariables(widget, cssPath, consumed);

            string cssText = WidgetFiles.Current.ReadAllText(cssPath) ?? string.Empty;
            bool cssUsesResources = ResourcePaths.FindReferences(cssText).Any();

            Func<SmwExportOptions, byte[]>? cssGenerator = null;
            if (varsInFile.Count > 0 || cssUsesResources)
            {
                var capturedVars = varsInFile;
                string capturedEntry = cssEntry;
                cssGenerator = _ => Encoding.UTF8.GetBytes(
                    RewriteResourceUrls(OverrideCssValues(cssText, capturedVars), capturedEntry, plan));
            }

            plan.Entries.Add(new SmwEntry
            {
                ZipEntry = cssEntry,
                AbsSource = cssPath,
                Kind = SmwEntryKind.Css,
                DefaultSelected = true,
                Generator = cssGenerator
            });

            string cssMetaPath = cssPath + ".json";
            bool hasMeta = WidgetFiles.Current.Exists(cssMetaPath);
            bool worthWriting = varsInFile.Any(v =>
                v.Type != WidgetCssVariableType.Default || !string.IsNullOrWhiteSpace(v.Description));

            if (!hasMeta && !worthWriting) continue;

            string capturedMetaPath = cssMetaPath;
            var capturedMetaVars = varsInFile;
            plan.Entries.Add(new SmwEntry
            {
                ZipEntry = $"{cssEntry}.json",
                AbsSource = hasMeta ? cssMetaPath : null,
                Kind = SmwEntryKind.Meta,
                DefaultSelected = true,
                Generator = _ => Encoding.UTF8.GetBytes(BuildCssMetaJson(capturedMetaPath, capturedMetaVars))
            });
        }

        var claimed = new HashSet<string>(plan.Entries.Select(en => en.ZipEntry),
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in WidgetFiles.Current.EnumerateFiles(widgetRoot))
        {
            string? entry = ToContentEntry(widgetRoot, file);
            if (entry == null || !claimed.Add(entry)) continue;

            plan.Entries.Add(new SmwEntry
            {
                ZipEntry = entry,
                AbsSource = file,
                Kind = SmwEntryKind.Asset,
                DefaultSelected = false
            });
        }

        AddFileVariableEntries(widget, widgetRoot, plan, claimed);
        AddSharedResourceEntries(widget, plan, claimed, hardcodedResources);
        return plan;
    }

    private static void AddSharedResourceEntries(Widget widget, ExportPlan plan, HashSet<string> claimed,
        HashSet<string> hardcodedResources)
    {
        var varUsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var jsVar in widget.JsVariables)
        {
            if (!((WidgetVariableType?)jsVar.Type).IsFileVariable()) continue;
            if (ResourcePaths.RelativeFromUrl(jsVar.Value) is { } rel) varUsed[rel] = jsVar.Name;
        }

        foreach (var rel in ResourcePaths.EnumerateRelative())
        {
            string entry = $"{ContentFolder}/{ExternalFolder}/{ResourcePaths.BundleFolder}/{rel}";
            if (!claimed.Add(entry)) continue;

            bool byVar = varUsed.TryGetValue(rel, out var varName);
            bool byMarkup = hardcodedResources.Contains(rel);

            plan.Entries.Add(new SmwEntry
            {
                ZipEntry = entry,
                AbsSource = ResourcePaths.ToLocalPath(ResourcePaths.UrlPrefix + rel),
                Kind = SmwEntryKind.External,
                DefaultSelected = false,
                InUse = byVar || byMarkup,
                UsageHint = byVar
                    ? $"Used by variable \"{varName}\""
                    : byMarkup ? "Referenced directly by this widget's html/css" : null
            });

            if (byVar)
                plan.OptionalRewrites[varName!] =
                    (entry, $"./{ExternalFolder}/{ResourcePaths.BundleFolder}/{rel}");
        }
    }

    private static void AddFileVariableEntries(Widget widget, string widgetRoot, ExportPlan plan, HashSet<string> claimed)
    {
        foreach (var jsVar in widget.JsVariables)
        {
            if (!((WidgetVariableType?)jsVar.Type).IsFileVariable()) continue;
            if (string.IsNullOrWhiteSpace(jsVar.Value)) continue;
            if (ResourcePaths.IsResourceUrl(jsVar.Value)) continue;

            bool isFolderType = jsVar.Type == WidgetVariableType.FolderPath;
            bool isRelative = jsVar.Value.StartsWith("./") || jsVar.Value.StartsWith("../");

            if (isRelative)
            {
                string rel = jsVar.Value.TrimStart('.').TrimStart('/');
                string prefix = $"{ContentFolder}/{rel}";
                foreach (var entry in plan.Entries.Where(en =>
                             en.Kind == SmwEntryKind.Asset &&
                             (en.ZipEntry.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                              en.ZipEntry.StartsWith(prefix.TrimEnd('/') + "/",
                                  StringComparison.OrdinalIgnoreCase))))
                {
                    entry.DefaultSelected = true;
                }
                continue;
            }

            if (!Path.IsPathRooted(jsVar.Value)) continue;

            if (isFolderType && Directory.Exists(jsVar.Value))
            {
                string varFolder = SanitizeName(jsVar.Name);
                foreach (var file in Directory.EnumerateFiles(jsVar.Value, "*", 
                             SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(jsVar.Value, file).Replace('\\', '/');
                    string entry = $"{ContentFolder}/{ExternalFolder}/{varFolder}/{rel}";
                    
                    if (!claimed.Add(entry)) continue;
                    plan.Entries.Add(new SmwEntry
                    {
                        ZipEntry = entry,
                        AbsSource = file,
                        Kind = SmwEntryKind.External,
                        DefaultSelected = true
                    });
                }
                
                plan.VariableRewrites[jsVar.Name] = $"./{ExternalFolder}/{varFolder}";
            }
            else if (!isFolderType && File.Exists(jsVar.Value))
            {
                string fileName = Path.GetFileName(jsVar.Value);
                string entry = $"{ContentFolder}/{ExternalFolder}/{fileName}";
                if (claimed.Add(entry))
                {
                    plan.Entries.Add(new SmwEntry
                    {
                        ZipEntry = entry,
                        AbsSource = jsVar.Value,
                        Kind = SmwEntryKind.External,
                        DefaultSelected = true
                    });
                }
                plan.VariableRewrites[jsVar.Name] = $"./{ExternalFolder}/{fileName}";
            }
        }
    }

    #endregion

    #region EXPORT

    public static async Task ExportWidgetAsync(ExportPlan plan, SmwExportOptions options, string outputPath)
    {
        var opts = options;
        if (string.IsNullOrWhiteSpace(opts.AppVersion))
        {
            opts = new SmwExportOptions
            {
                Name = options.Name,
                Author = options.Author,
                Group = options.Group,
                Version = options.Version,
                Tags = options.Tags,
                PreviewImagePath = options.PreviewImagePath,
                AppVersion = AppServices.AppVersion
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        await using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false);

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.Entries)
        {
            if (entry is { Locked: false, DefaultSelected: false }) continue;
            if (!written.Add(entry.ZipEntry)) continue;

            byte[]? bytes = null;
            if (entry.Generator != null)
            {
                bytes = entry.Generator(opts);
            }
            else if (entry.AbsSource != null)
            {
                if (File.Exists(entry.AbsSource))
                {
                    await archive.CreateEntryFromFileAsync(entry.AbsSource, entry.ZipEntry, CompressionLevel.Optimal);
                    continue;
                }
                bytes = WidgetFiles.Current.ReadAllBytes(entry.AbsSource);
            }

            if (bytes == null) continue;

            var zipEntry = archive.CreateEntry(entry.ZipEntry, CompressionLevel.Optimal);
            await using var stream = await zipEntry.OpenAsync();
            await stream.WriteAsync(bytes);
        }

        if (!string.IsNullOrWhiteSpace(opts.PreviewImagePath) && File.Exists(opts.PreviewImagePath))
        {
            string previewEntry = PreviewEntryName(opts.PreviewImagePath);
            if (written.Add(previewEntry))
                await archive.CreateEntryFromFileAsync(opts.PreviewImagePath, previewEntry, CompressionLevel.Optimal);
        }
    }
    
    public static string ExportsDirectory => Path.GetFullPath(Path.Combine("./exports", "widgets"));

    public static WidgetMeta ReadExistingMeta(Widget widget)
    {
        var json = WidgetFiles.Current.ReadAllText(widget.HtmlPath + ".json");
        if (json == null) return new WidgetMeta();
        try
        {
            return JsonSerializer.Deserialize<WidgetMeta>(json, MetaOptions) ?? new WidgetMeta();
        }
        catch
        {
            return new WidgetMeta();
        }
    }

    public static string BuildFileName(string author, string group, string name, string version)
    {
        var parts = new[] { author, WidgetPackPaths.NormalizeGroup(group), name, version }
            .Select(p => SanitizeName(p?.Trim() ?? string.Empty).Replace(' ', '-'))
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return $"{string.Join('_', parts)}.smw";
    }

    private static string BuildManifest(Widget widget, ExportPlan plan, SmwExportOptions opts)
    {
        string name = string.IsNullOrWhiteSpace(opts.Name) ? widget.Name : opts.Name;
        string group = WidgetPackPaths.NormalizeGroup(opts.Group);

        string packId = WidgetPackPaths.TryResolve(widget.HtmlPath, 
            out _, out _, out var existingPackId, out _)
            ? existingPackId
            : WidgetPackPaths.MakePackId(opts.Author, group, name);

        var obj = new
        {
            version = FormatVersion,
            app_version = opts.AppVersion,
            exported_at = DateTime.UtcNow,
            widget = new
            {
                pack_id = packId,
                name,
                author = opts.Author,
                group,
                widget_version = opts.Version,
                tags = opts.Tags,
                preview_image = string.IsNullOrWhiteSpace(opts.PreviewImagePath)
                    ? string.Empty
                    : PreviewEntryName(opts.PreviewImagePath),
                docsUrl = widget.DocsUrl ?? string.Empty,
                type = widget.Type.ToString(),
                entry = plan.EntryZipPath,
                size = new { width = widget.Width, height = widget.Height },
                scale = new { x = widget.ScaleX, y = widget.ScaleY }
            }
        };
        return JsonSerializer.Serialize(obj, ManifestOptions);
    }

    #endregion

    #region OVERRIDES

    private static List<CssVariable> ClaimCssVariables(Widget widget, string cssPath, HashSet<string> consumed)
    {
        var result = new List<CssVariable>();
        string? content = WidgetFiles.Current.ReadAllText(cssPath);
        if (content == null) return result;

        foreach (Match match in CssVarRegex().Matches(content))
        {
            string name = match.Groups[1].Value.Trim();
            if (!consumed.Add(name)) continue;
            var dbVar = widget.CssVariables.FirstOrDefault(v =>
                string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (dbVar != null) result.Add(dbVar);
        }

        return result;
    }

    private static string RewriteResourceUrls(string text, string ownZipEntry, ExportPlan plan)
    {
        if (string.IsNullOrEmpty(text)) return text;

        int depth = ownZipEntry.Split('/').Length - 2;
        string upToContent = depth <= 0 ? "./" : string.Concat(Enumerable.Repeat("../", depth));

        return ResourcePaths.RewriteReferences(text, rel =>
            plan.IsSelected($"{ContentFolder}/{ExternalFolder}/{ResourcePaths.BundleFolder}/{rel}")
                ? $"{upToContent}{ExternalFolder}/{ResourcePaths.BundleFolder}/"
                : null);
    }

    private static string OverrideCssValues(string css, List<CssVariable> variables)
    {
        foreach (var variable in variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Value)) continue;
            var pattern = new Regex($@"(--{Regex.Escape(variable.Name)}\s*:\s*)([^;]*)(;)");
            css = pattern.Replace(css, m => $"{m.Groups[1].Value}{variable.Value}{m.Groups[3].Value}", 1);
        }
        return css;
    }

    private static string BuildCssMetaJson(string existingMetaPath, List<CssVariable> variables)
    {
        Dictionary<string, Dictionary<string, string>> meta = new(StringComparer.Ordinal);
        var existingJson = WidgetFiles.Current.ReadAllText(existingMetaPath);
        if (existingJson != null)
        {
            try
            {
                meta = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(existingJson) ?? meta;
            }
            catch { /**/ }
        }

        foreach (var variable in variables)
        {
            if (!meta.TryGetValue(variable.Name, out var entry))
            {
                entry = new Dictionary<string, string>(StringComparer.Ordinal);
                meta[variable.Name] = entry;
            }
            entry["type"] = variable.Type.ToString();
            entry["description"] = variable.Description ?? string.Empty;
        }

        return JsonSerializer.Serialize(meta, ManifestOptions);
    }

    private static string BuildWidgetMetaJson(Widget widget, ExportPlan plan, SmwExportOptions opts)
    {
        WidgetMeta meta = ReadExistingMeta(widget);

        if (!string.IsNullOrWhiteSpace(opts.Author)) meta.Author = opts.Author;
        if (!string.IsNullOrWhiteSpace(widget.DocsUrl)) meta.Url = widget.DocsUrl!;
        meta.Width = widget.Width;
        meta.Height = widget.Height;

        foreach (var jsVar in widget.JsVariables)
        {
            if (!meta.Vars.TryGetValue(jsVar.Name, out var metaVar))
            {
                metaVar = new WidgetMetaVar();
                meta.Vars[jsVar.Name] = metaVar;
            }

            metaVar.Name = jsVar.Name;
            metaVar.Type = jsVar.Type;
            metaVar.Description = jsVar.Description ?? string.Empty;

            metaVar.Value = ToMetaValue(jsVar.Type, plan.ResolveVarValue(jsVar), metaVar);
        }

        return JsonSerializer.Serialize(meta, MetaOptions);
    }

    private static object ToMetaValue(WidgetVariableType type, string value, WidgetMetaVar metaVar)
    {
        var items = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        switch (type)
        {
            case WidgetVariableType.StringSelect:
                metaVar.Options = items;
                return items.Count > 0 ? items[0] : string.Empty;

            case WidgetVariableType.Boolean:
                return bool.TryParse(value, out var b) && b;

            case WidgetVariableType.Int:
            case WidgetVariableType.Percent:
                return int.TryParse(value, out var i) ? i : 0;

            case WidgetVariableType.Float:
                return float.TryParse(value, out var f) ? f : 0f;

            default:
                if (type.IsListType()) return items;
                return value;
        }
    }

    #endregion

    #region HELPERS

    private static string? ToContentEntry(string widgetRoot, string absPath)
    {
        string rel = Path.GetRelativePath(widgetRoot, absPath).Replace('\\', '/');
        if (rel.StartsWith("../") || rel == ".." || Path.IsPathRooted(rel)) return null;
        return $"{ContentFolder}/{rel}";
    }

    private static string GetHtmlLinkFor(Widget widget, string cssPath)
    {
        string baseDir = Path.GetDirectoryName(widget.HtmlPath)!;
        foreach (Match match in Widget.CssLinkMatches(WidgetFiles.Current.ReadAllText(widget.HtmlPath) ?? string.Empty))
        {
            string href = match.Groups[1].Value;
            string resolved = Path.IsPathRooted(href) ? href : Path.Combine(baseDir, href);
            if (string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(cssPath), StringComparison.OrdinalIgnoreCase))
                return href;
        }
        return string.Empty;
    }

    private static string RewriteHtmlLinks(string html, Dictionary<string, string> rewrites)
    {
        foreach (var (original, replacement) in rewrites)
        {
            html = html.Replace($"\"{original}\"", $"\"{replacement}\"")
                       .Replace($"'{original}'", $"'{replacement}'");
        }
        return html;
    }

    private static string SanitizeName(string name) => SafeFileName.Sanitize(name);

    [GeneratedRegex(@"--([a-zA-Z0-9-_]+)\s*:\s*([^;]+);")]
    private static partial Regex CssVarRegex();

    #endregion
}
