using System.Net;
using System.Text;
using System.Text.Json;
using SubathonManager.Data;

namespace SubathonManager.Server;

public partial class WebServer
{
    private async Task<bool> HandleApiReqeustAsync(HttpListenerContext ctx, string path)
    {
        // todo, double click send event to set new widget in editor ui if in edit mode
        if (path.StartsWith("/api/update-position/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 3)
            {
                string widgetId = parts[2];
                    
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    body = await reader.ReadToEndAsync();
                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                    
                if (data == null)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid update data"));
                    ctx.Response.Close();
                    return true;
                }
                    
                var widgetHelper = new WidgetEntityHelper();
                bool success = await widgetHelper.UpdateWidgetPosition(widgetId, data);
                if (success)
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("OK"));
                    ctx.Response.Close();
                    return true;
                }
                ctx.Response.StatusCode = 404;
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Widget Not Found"));
                ctx.Response.Close();
                return true;
            }
            ctx.Response.StatusCode = 400;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid Widget ID"));
            ctx.Response.Close();
            return true;
        }
        return false;
    }
}