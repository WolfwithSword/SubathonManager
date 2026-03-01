using System.Net;
using System.Reflection.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Text;
using Xunit.Sdk;

namespace SubathonManager.Tests.Utility;
public class MockWebServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Dictionary<(string method, string path), (int statusCode, string body)> _routes = new();

    public string BaseUrl { get; }

    public MockWebServerHost(int port = 0)
    {
        port = port == 0 ? GetFreePort() : port;
        BaseUrl = $"http://127.0.0.1:{port}/";
        
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(BaseUrl);
        _app = builder.Build();
        
        _app.Use(async (context, next) =>
        {
            // useful for breakpoint checking
            Console.WriteLine($"[MockServer] Incoming: {context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
            await next();
        });
        
        _app.Run(async context =>
        {
            try
            {
                var path = context.Request.Path.Value ?? "/";
                if (path.Contains('?')) // won't come up but just in case...
                {
                    path = path.Substring(0, path.IndexOf('?'));
                }
                var key = (context.Request.Method, path);
                
                if (_routes.TryGetValue(key, out var response))
                {
                    context.Response.StatusCode = response.statusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(response.body);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("Not Found");
                }
            }
            catch (Exception ex)
            { /**/ }
        });
        _app.StartAsync().GetAwaiter().GetResult();

    }

    public MockWebServerHost OnGet(string path, string responseBody, int statusCode = 200)
        => AddRoute("GET", path, responseBody, statusCode);

    public MockWebServerHost OnPost(string path, string responseBody, int statusCode = 200)
        => AddRoute("POST", path, responseBody, statusCode);

    private MockWebServerHost AddRoute(string method, string path, string body, int statusCode)
    {
        _routes[(method, path)] = (statusCode, body);
        return this;
    }
    
    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}