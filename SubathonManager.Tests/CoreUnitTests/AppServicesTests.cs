using Moq;
using System.Reflection;

namespace SubathonManager.Tests.CoreUnitTests
{
    public class AppServicesTests
    {
        private readonly Mock<Microsoft.Extensions.Logging.ILogger> _loggerMock;

        public AppServicesTests()
        {
            _loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger>();
        }

        [Fact]
        public void AppVersion_IsNotNullOrEmpty()
        {
            string version = AppServices.AppVersion;
            Assert.False(string.IsNullOrWhiteSpace(version));
        }

        [Fact]
        public void GetVersion_Returns_VersionInstance()
        {
            MethodInfo? mi = typeof(AppServices).GetMethod("GetVersion", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(mi);
            var result = mi!.Invoke(null, Array.Empty<object?>());
            Assert.IsType<Version>(result);
            var ver = (Version)result!;
            Assert.True(ver.Major >= 0);
            Assert.True(ver.Minor >= 0);
            Assert.True(ver.Build >= -1); 
        }

        [Fact]
        public async Task CheckForUpdate_ReturnsSafeTuple_OnFailureOrNoUpdate()
        {
            var logger = _loggerMock.Object;
            var res = await AppServices.CheckForUpdate(logger);
            Assert.IsType<ValueTuple<bool, string?, string?>>(res);
            Assert.False(res.Item1);
        }

        [Fact]
        public async Task InstallUpdate_ReturnsFalse_WhenAssetIsNull()
        {
            var logger = _loggerMock.Object;
            bool installed = await AppServices.InstallUpdate(null, logger);
            Assert.False(installed);
        }

        [Fact]
        public async Task DownloadAndInstall_ReturnsFalse_WhenDownloadOrInstallFails()
        {
            var logger = _loggerMock.Object;
            bool result = await AppServices.DownloadAndInstall(logger);
            Assert.False(result);
        }
    }
}