using System.Web;
using SubathonManager.Core;
using SubathonManager.Core.Objects;

namespace SubathonManager.UI.Platform;

public static class ProtocolParser
{
    public static ActivationRequest Parse(string[] args)
    {
        if (args.Length == 0)
            return new ActivationRequest(ActivationKind.Unknown, string.Empty);

        var arg = args[0];
        
        if (arg.EndsWith(".smo", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(arg) &&
            !arg.Contains("subathonmanager://oauth", StringComparison.CurrentCultureIgnoreCase))
        {
            Utils.PendingOverlayImportPath = arg;
            return new ActivationRequest(ActivationKind.SmoFile, arg);
        }

        if (!arg.StartsWith("subathonmanager://"))
            return new ActivationRequest(ActivationKind.Unknown, arg);

        var uri = new Uri(arg);

        if (arg.Contains(".smo", StringComparison.OrdinalIgnoreCase) && uri.Host != "oauth")
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            var url = query["url"];
            if (!string.IsNullOrEmpty(url))
            {
                Utils.PendingOverlayImportPath = url;
                return new ActivationRequest(ActivationKind.SmoFile, url);
            }
        }
        else if (uri.Host == "oauth")
        {
            var provider = uri.AbsolutePath.TrimStart('/');
            var query = HttpUtility.ParseQueryString(uri.Query);

            Utils.PendingOAuthCallback = new OAuthCallback
            {
                Provider = provider,
                AccessToken = query["access_token"] ?? "",
                RefreshToken = query["refresh_token"] ?? "",
                Code = query["code"] ?? "",
                Error = query["error"] ?? "",
                ExpiresIn = query["expires_in"] ?? "",
                ClientId = query["client_id"] ?? ""
            };
            return new ActivationRequest(ActivationKind.OAuth, arg);
        }

        return new ActivationRequest(ActivationKind.Unknown, arg);
    }
}
