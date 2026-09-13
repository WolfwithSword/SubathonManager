using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace SubathonManager.Tests.Utility;

public class MockWebServerHost : IAsyncDisposable {
    private readonly WebApplication _app;

    private readonly ConcurrentDictionary<(string method, string path), int> _callCounts = new();
    private int _getCallCount;
    private int _postCallCount;

    private readonly Dictionary<(string method, string path), Func<HttpRequest, (int statusCode, string body)>>
        _dynamicRoutes = new();

    private readonly Dictionary<(string method, string path), (int statusCode, string body)> _routes = new();

    public MockWebServerHost(int port = 0) {
        port = port == 0 ? GetFreePort() : port;
        BaseUrl = $"http://127.0.0.1:{port}/";

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(BaseUrl);
        _app = builder.Build();

        _app.Use(async (context, next) => {
            if (context.Request.Method == "GET") Interlocked.Increment(ref _getCallCount);
            else if (context.Request.Method == "POST") Interlocked.Increment(ref _postCallCount);
            _callCounts.AddOrUpdate((context.Request.Method, context.Request.Path.Value ?? "/"), 1, (_, c) => c + 1);
            // useful for breakpoint checking
            Console.WriteLine(
                $"[MockServer] Incoming: {context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
            await next();
        });

        _app.Run(async context => {
            try {
                string path = context.Request.Path.Value ?? "/";
                if (path.Contains('?')) // won't come up but just in case...
                    path = path.Substring(0, path.IndexOf('?'));
                (string Method, string path) key = (context.Request.Method, path);

                if (_dynamicRoutes.TryGetValue(key,
                        out Func<HttpRequest, (int statusCode, string body)>? handler)) {
                    (int statusCode, string body) dynamicResponse = handler(context.Request);
                    context.Response.StatusCode = dynamicResponse.statusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(dynamicResponse.body);
                }
                else if (_routes.TryGetValue(key, out (int statusCode, string body) response)) {
                    context.Response.StatusCode = response.statusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(response.body);
                }
                else {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("Not Found");
                }
            }
            catch {
                /**/
            }
        });
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public string BaseUrl { get; }
    public int GetCallCount => Volatile.Read(ref _getCallCount);
    public int PostCallCount => Volatile.Read(ref _postCallCount);

    public int CallCount(string method, string path) {
        return _callCounts.GetValueOrDefault((method, path));
    }

    public async ValueTask DisposeAsync() {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public MockWebServerHost OnGet(string path, string responseBody, int statusCode = 200) {
        return AddRoute("GET", path, responseBody, statusCode);
    }

    public MockWebServerHost OnPost(string path, string responseBody, int statusCode = 200) {
        return AddRoute("POST", path, responseBody, statusCode);
    }

    public MockWebServerHost OnGetDynamic(string path, Func<HttpRequest, (int statusCode, string body)> handler) {
        _dynamicRoutes[("GET", path)] = handler;
        return this;
    }

    private MockWebServerHost AddRoute(string method, string path, string body, int statusCode) {
        _routes[(method, path)] = (statusCode, body);
        return this;
    }

    private static int GetFreePort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}