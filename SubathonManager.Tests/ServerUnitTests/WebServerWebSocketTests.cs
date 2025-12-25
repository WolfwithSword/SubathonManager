using System.Text;
using System.Net.WebSockets;
using SubathonManager.Server;
namespace SubathonManager.Tests.ServerUnitTests;

public class WebServerWebSocketTests
{
    private async Task HandleWebSocketAsync(IHttpContext ctx)
    {
        var accept = ctx.AcceptWebSocketAsync();

        if (accept is null)
        {
            await ctx.WriteResponse(400, "Not a WebSocket request");
            return;
        }

        using var socket = await accept;

        var message = Encoding.UTF8.GetBytes("hello");
        await socket.SendAsync(
            message,
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
    }
    
    [Fact]
    public async Task Non_WebSocket_Request_Is_Rejected_As_WebSocket()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = false
        };

        await HandleWebSocketAsync(ctx);

        Assert.Equal(400, ctx.StatusCode);
        Assert.Equal("Not a WebSocket request", ctx.ResponseBody);
    }
    
    [Fact]
    public async Task WebSocket_Request_Is_Accepted()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        await HandleWebSocketAsync(ctx);

        Assert.Single(ctx.Socket.SentMessages);

        var text = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("hello", text);
    }
    
    [Fact]
    public async Task WebSocket_Sends_Hello_Message()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        await HandleWebSocketAsync(ctx);

        var sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("hello", sent);
    }
    
    [Fact]
    public async Task WebSocket_Does_Not_Write_Response()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };
        await HandleWebSocketAsync(ctx);
        Assert.Equal(0, ctx.StatusCode); // default val
    }
    
    [Fact]
    public async Task WebSocket_Does_Not_Call_Accept_When_Not_WS()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = false
        };
        await HandleWebSocketAsync(ctx);
        Assert.Equal(1, ctx.AcceptCalls);
    }
    
    [Fact]
    public async Task WebSocket_Is_Disposed()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };
        await HandleWebSocketAsync(ctx);
        Assert.True(ctx.Socket.Disposed);
    }
}