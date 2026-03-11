using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace SubathonManager.Server;

[ExcludeFromCodeCoverage] // This is a beta feature as a fallback for non-windows systems or WINE/Proton/Other without HTTPListener support
public sealed class TcpListenerContextAdapter : IHttpContext
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly string _rawRequest;
    private readonly Dictionary<string, string> _headers;
    private readonly byte[] _bodyBytes;

    public string Method { get; }
    public string Path { get; }
    public string QueryString { get; }
    public Encoding Encoding => Encoding.UTF8;
    public Stream Body { get; }

    public bool IsWebSocket =>
        _headers.TryGetValue("upgrade", out var v) &&
        v.Equals("websocket", StringComparison.OrdinalIgnoreCase);

    private TcpListenerContextAdapter(
        TcpClient client,
        NetworkStream stream,
        string method,
        string path,
        string queryString,
        Dictionary<string, string> headers,
        byte[] body,
        string rawRequest)
    {
        _client = client;
        _stream = stream;
        Method = method;
        Path = path;
        QueryString = queryString;
        _headers = headers;
        _bodyBytes = body;
        Body = new MemoryStream(body);
        _rawRequest = rawRequest;
    }

    public static async Task<TcpListenerContextAdapter?> ParseAsync(
        TcpClient client,
        CancellationToken ct = default)
    {
        var stream = client.GetStream();

        var headerBuffer = new List<byte>(4096);
        var oneByte = new byte[1];
        while (true)
        {
            if (ct.IsCancellationRequested) return null;
            int read = await stream.ReadAsync(oneByte, ct);
            if (read == 0) return null;
            headerBuffer.Add(oneByte[0]);

            if (headerBuffer.Count >= 4)
            {
                int tail = headerBuffer.Count;
                if (headerBuffer[tail - 4] == '\r' && headerBuffer[tail - 3] == '\n' &&
                    headerBuffer[tail - 2] == '\r' && headerBuffer[tail - 1] == '\n')
                    break;
            }

            if (headerBuffer.Count > 65536) return null;
        }

        string rawHeader = Encoding.UTF8.GetString(headerBuffer.ToArray());
        var lines = rawHeader.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        string method = requestLine[0].ToUpperInvariant();
        string rawPath = requestLine.Length >= 2 ? requestLine[1] : "/";

        string path, query;
        int qIdx = rawPath.IndexOf('?');
        if (qIdx >= 0)
        {
            path = Uri.UnescapeDataString(rawPath[..qIdx]);
            query = rawPath[(qIdx + 1)..];
        }
        else
        {
            path = Uri.UnescapeDataString(rawPath);
            query = string.Empty;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) break;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        byte[] body = [];
        if (headers.TryGetValue("content-length", out var clStr) &&
            int.TryParse(clStr, out int contentLength) && contentLength > 0)
        {
            body = new byte[contentLength];
            int totalRead = 0;
            while (totalRead < contentLength)
            {
                int n = await stream.ReadAsync(body.AsMemory(totalRead, contentLength - totalRead), ct);
                if (n == 0) break;
                totalRead += n;
            }
        }

        return new TcpListenerContextAdapter(client, stream, method, path, query, headers, body, rawHeader);
    }

    public Task<WebSocket>? AcceptWebSocketAsync(string? subProtocol = null)
    {
        if (!IsWebSocket) return null;
        return DoWebSocketUpgradeAsync(subProtocol);
    }

    private async Task<WebSocket> DoWebSocketUpgradeAsync(string? subProtocol)
    {
        if (!_headers.TryGetValue("sec-websocket-key", out var key))
            throw new InvalidOperationException("Missing Sec-WebSocket-Key header");

        string acceptKey = Convert.ToBase64String(
            SHA1.HashData(
                Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))); // hardcode

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 101 Switching Protocols\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append($"Sec-WebSocket-Accept: {acceptKey}\r\n");
        if (!string.IsNullOrEmpty(subProtocol))
            sb.Append($"Sec-WebSocket-Protocol: {subProtocol}\r\n");
        sb.Append("\r\n");

        byte[] handshake = Encoding.UTF8.GetBytes(sb.ToString());
        await _stream.WriteAsync(handshake);
        await _stream.FlushAsync();

        return WebSocket.CreateFromStream(_stream, new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = subProtocol,
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });
    }

    public async Task WriteResponse(int statusCode, string body, bool addCors = false, string? contentType = null)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string reasonPhrase = statusCode switch
        {
            200 => "OK", 201 => "Created", 204 => "No Content",
            400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found", 500 => "Internal Server Error",
            _ => "OK"
        };

        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {statusCode} {reasonPhrase}\r\n");
        sb.Append($"Content-Type: {contentType ?? "text/plain"}; charset=utf-8\r\n");
        sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
        if (addCors) AppendCorsHeaders(sb);
        sb.Append("Connection: close\r\n\r\n");

        byte[] headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        await _stream.WriteAsync(headerBytes);
        await _stream.WriteAsync(bodyBytes);
        await _stream.FlushAsync();
        Close();
    }

    public async Task ServeFile(string fullPath, string contentType)
    {
        await using var fs = File.OpenRead(fullPath);
        long length = fs.Length;

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 200 OK\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {length}\r\n");
        AppendCorsHeaders(sb);
        sb.Append("Connection: close\r\n\r\n");

        byte[] headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        await _stream.WriteAsync(headerBytes);
        await fs.CopyToAsync(_stream);
        await _stream.FlushAsync();
        Close();
    }

    private static void AppendCorsHeaders(StringBuilder sb)
    {
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, PATCH, PUT, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
    }

    private void Close()
    {
        try { _stream.Close(); } catch { /* ignored */ }
        try { _client.Close(); } catch { /* ignored */ }
    }
}