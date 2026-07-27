using System.IO.Pipes;
using System.Text;

namespace SubathonManager.UI.Platform;

public abstract class PlatformIntegrationBase : IPlatformIntegration
{
    private const string MutexName = @"Global\SubathonManager_SingleInstanceMutex";
    private const string PipeName = "SubathonManager_SingleInstance_Pipe";

    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCts;

    public event Action<ActivationRequest>? ActivationReceived;

    public abstract void RegisterFileAssociations();

    public bool TryAcquireSingleInstance(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            var request = ProtocolParser.Parse(args);
            if (request.Kind != ActivationKind.Unknown)
                ForwardToPrimary(request);
            else
                ForwardToPrimary(new ActivationRequest(ActivationKind.Unknown, "SHOW"));
            return false;
        }

        StartPipeServer();
        return true;
    }

    private void ForwardToPrimary(ActivationRequest request)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var payload = $"{(int)request.Kind}\n{request.Payload}";
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch {/* */ }
    }

    private void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(token);
                    var parts = text.Split('\n', 2);
                    if (parts.Length == 0) continue;

                    if (!int.TryParse(parts[0], out int kindInt))
                        continue;
                    var kind = (ActivationKind)kindInt;
                    var payload = parts.Length > 1 ? parts[1] : string.Empty;

                    ActivationReceived?.Invoke(new ActivationRequest(kind, payload));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                { /**/ }
            }
        }, token);
    }

    public void Release()
    {
        try { _pipeCts?.Cancel(); } catch { /**/ }
        try { _mutex?.ReleaseMutex(); } catch { /**/ }
        _mutex?.Dispose();
        _mutex = null;
    }
}
