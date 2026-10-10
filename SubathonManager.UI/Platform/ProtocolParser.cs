using System.Collections.Specialized;
using System.Web;
using SubathonManager.Core;
using SubathonManager.Core.Objects;

namespace SubathonManager.UI.Platform;

public static class ProtocolParser {
    public static ActivationRequest Parse(string[] args) {
        if (args.Length == 0)
            return new ActivationRequest(ActivationKind.Unknown, string.Empty);

        string arg = args[0];

        if (arg.EndsWith(".smo", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(arg) &&
            !arg.Contains("subathonmanager://oauth", StringComparison.CurrentCultureIgnoreCase)) {
            Utils.PendingOverlayImportPath = arg;
            return new ActivationRequest(ActivationKind.SmoFile, arg);
        }

        if ((arg.EndsWith(".smw", StringComparison.OrdinalIgnoreCase) ||
             arg.EndsWith(".smwc", StringComparison.OrdinalIgnoreCase)) && File.Exists(arg)) {
            Utils.PendingWidgetPackImportPath = arg;
            return new ActivationRequest(ActivationKind.SmwFile, arg);
        }

        if (arg.EndsWith(CustomAction.FileExtension, StringComparison.OrdinalIgnoreCase) && File.Exists(arg)) {
            Utils.PendingActionImportPath = arg;
            return new ActivationRequest(ActivationKind.SmaFile, arg);
        }

        if (!arg.StartsWith("subathonmanager://"))
            return new ActivationRequest(ActivationKind.Unknown, arg);

        var uri = new Uri(arg);

        if (uri.Host == "oauth")
            return new ActivationRequest(ActivationKind.OAuth, arg);

        NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);
        string? url = query["url"];
        if (string.IsNullOrEmpty(url))
            return new ActivationRequest(ActivationKind.Unknown, arg);

        ActivationKind kind = ClassifyPack(url);
        if (kind == ActivationKind.Unknown) kind = ClassifyPack(uri.AbsolutePath);
        if (kind == ActivationKind.Unknown) kind = ClassifyHost(uri.Host);

        switch (kind) {
            case ActivationKind.SmoFile:
                Utils.PendingOverlayImportPath = url;
                return new ActivationRequest(ActivationKind.SmoFile, url);
            case ActivationKind.SmwFile:
                Utils.PendingWidgetPackImportPath = url;
                return new ActivationRequest(ActivationKind.SmwFile, url);
            case ActivationKind.SmaFile:
                Utils.PendingActionImportPath = url;
                return new ActivationRequest(ActivationKind.SmaFile, url);
            default:
                return new ActivationRequest(ActivationKind.Unknown, arg);
        }
    }

    private static ActivationKind ClassifyPack(string value) {
        string path = value;
        int cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];

        if (path.EndsWith(".smo", StringComparison.OrdinalIgnoreCase))
            return ActivationKind.SmoFile;
        if (path.EndsWith(".smw", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".smwc", StringComparison.OrdinalIgnoreCase))
            return ActivationKind.SmwFile;
        if (path.EndsWith(CustomAction.FileExtension, StringComparison.OrdinalIgnoreCase))
            return ActivationKind.SmaFile;
        return ActivationKind.Unknown;
    }

    private static ActivationKind ClassifyHost(string host) {
        return host.ToLowerInvariant() switch {
            "overlay" or "smo" => ActivationKind.SmoFile,
            "widget" or "widgets" or "smw" or "smwc" or "collection" => ActivationKind.SmwFile,
            "action" or "actions" or "sma" => ActivationKind.SmaFile,
            _ => ActivationKind.Unknown
        };
    }
}