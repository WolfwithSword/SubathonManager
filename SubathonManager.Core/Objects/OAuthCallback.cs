using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Objects;

[ExcludeFromCodeCoverage]
public class OAuthCallback
{
    public string Provider { get; set; } = string.Empty;
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string? Error { get; set; }
    
    public string? Code { get; set; }
}