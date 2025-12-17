using SubathonManager.Server;
namespace SubathonManager.Tests.ServerUnitTests;

public class WebServerTests
{
    [Theory]
    [InlineData("test.html", "text/html")]
    [InlineData("test/image.png", "image/png")]
    [InlineData("file.unknown", "application/octet-stream")]
    [InlineData("test.css", "text/css")]
    [InlineData("test.js", "application/javascript")]
    [InlineData("test.json", "application/json")]
    [InlineData("test/image.jpg", "image/jpeg")]
    [InlineData("test/image.gif", "image/gif")]
    [InlineData("test/image.webp", "image/webp")]
    [InlineData("test/image.avif", "image/avif")]
    [InlineData("test/image.bmp", "image/bmp")]
    [InlineData("test/image.svg", "image/svg+xml")]
    [InlineData("test/image.ico", "image/x-icon")]
    [InlineData("test/videos/video.mp4", "video/mp4")]
    [InlineData("test/video.m4v", "video/x-m4v")]
    [InlineData("test/video.webm", "video/webm")]
    [InlineData("test/video.ogv", "video/ogg")]
    [InlineData("test/sound.mp3", "audio/mpeg")]
    [InlineData("test/sound.wav", "audio/wav")]
    [InlineData("test/sound.ogg", "audio/ogg")]
    [InlineData("test/sound.opus", "audio/opus")]
    [InlineData("test/sound.m4a", "audio/mp4")]
    [InlineData("test/font.woff", "font/woff")]
    [InlineData("test/font.woff2", "font/woff2")]
    [InlineData("test/font.ttf", "font/ttf")]
    [InlineData("test/font.otf", "font/otf")]
    [InlineData("test/data.txt", "text/plain")]
    [InlineData("test/data.csv", "text/csv")]
    [InlineData("test/data.xml", "application/xml")]
    public void ContentType_IsCorrect(string file, string expected)
    {
        Assert.Equal(expected, WebServer.GetContentType(file));
    }
    
}