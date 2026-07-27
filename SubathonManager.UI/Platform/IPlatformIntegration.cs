namespace SubathonManager.UI.Platform;

public interface IPlatformIntegration
{
    void RegisterFileAssociations();
    bool TryAcquireSingleInstance(string[] args);
    event Action<ActivationRequest>? ActivationReceived;
    void Release();
}

public readonly record struct ActivationRequest(ActivationKind Kind, string Payload);

public enum ActivationKind
{
    Unknown,
    SmoFile,
    OAuth
}
