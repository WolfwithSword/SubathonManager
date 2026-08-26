using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;

namespace SubathonManager.Tests.CoreUnitTests;

public class AppServicesTests {
    private readonly Mock<ILogger> _loggerMock = new();

    [Fact]
    public void AppVersion_IsNotNullOrEmpty() {
        string version = AppServices.AppVersion;
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public void GetVersion_Returns_VersionInstance() {
        MethodInfo? mi = typeof(AppServices).GetMethod("GetVersion", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(mi);
        object? result = mi!.Invoke(null, []);
        Assert.IsType<Version>(result);
        var ver = (Version)result!;
        Assert.True(ver.Major >= 0);
        Assert.True(ver.Minor >= 0);
        Assert.True(ver.Build >= -1);
    }

    [Fact]
    public async Task CheckForUpdate_ReturnsSafeTuple() {
        ILogger logger = _loggerMock.Object;
        (bool, string?, string?) res = await AppServices.CheckForUpdate(logger);
        Assert.IsType<ValueTuple<bool, string?, string?>>(res);
    }

    [Fact]
    public async Task InstallUpdate_ReturnsFalse_WhenAssetIsNull() {
        ILogger logger = _loggerMock.Object;
        bool installed = await AppServices.InstallUpdate(null, logger);
        Assert.False(installed);
    }
}