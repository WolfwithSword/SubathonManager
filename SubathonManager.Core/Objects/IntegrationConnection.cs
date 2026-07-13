using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Objects;

public class IntegrationConnection
{
    public SubathonEventSource Source { get; init; }
    public string Service { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Status { get; init; }
    public bool Configured { get; init; } = true;

    public override string ToString() => $"[{Source}:{Service}] [{Name}] Connected: {Status}";
}