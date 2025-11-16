using System.Text;
using Microsoft.Extensions.Logging;

namespace SubathonManager.Core
{
    public class RotatingFileLoggerProvider : ILoggerProvider
    {
        private readonly string _logDir;
        private readonly TimeSpan _retention;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private string _currentLogFile;
        private DateTime _lastRotationDate = DateTime.Now.Date;

        public RotatingFileLoggerProvider(string logDirectory, int retentionDays = 30)
        {
            _logDir = logDirectory;
            _retention = TimeSpan.FromDays(retentionDays);
            Directory.CreateDirectory(_logDir);
            _currentLogFile = GetLogFilePath(DateTime.Now);
            _ = Task.Run(CleanupOldLogsAsync);
        }

        public ILogger CreateLogger(string categoryName) =>
            new RotatingFileLogger(categoryName, this);

        public void Dispose() => _lock.Dispose();

        private string GetLogFilePath(DateTime date) =>
            Path.Combine(_logDir, $"app_{date:yyyy-MM-dd}.log");

        private static string GetShortLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT",
            _ => "NONE"
        };
        
        internal async Task WriteAsync(string category, LogLevel level, string message, Exception? ex)
        {
            await _lock.WaitAsync();
            try
            {
                RotateIfNeeded();

                var sb = new StringBuilder()
                    .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
                    .Append('[').Append(GetShortLevel(level)).Append("] ")
                    .Append('[').Append(category).Append("] ")
                    .Append(message);

                if (ex != null)
                    sb.Append(" Exception: ").Append(ex);

                sb.AppendLine();

                await File.AppendAllTextAsync(_currentLogFile, sb.ToString(), Encoding.UTF8);
            }
            finally
            {
                _lock.Release();
            }
        }

        private void RotateIfNeeded()
        {
            var now = DateTime.Now;
            if (now.Date != _lastRotationDate)
            {
                _currentLogFile = GetLogFilePath(now);
                _lastRotationDate = now.Date;
                _ = Task.Run(CleanupOldLogsAsync);
            }
        }

        private async Task CleanupOldLogsAsync()
        {
            try
            {
                var files = Directory.GetFiles(_logDir, "app_*.log");
                var cutoff = DateTime.Now - _retention;

                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.StartsWith("app_") && 
                        DateTime.TryParseExact(name[4..], "yyyy-MM-dd", null,
                            System.Globalization.DateTimeStyles.None, out var date) &&
                        date < cutoff)
                    {
                        try { File.Delete(file); } catch { /**/ }
                    }
                }

                await Task.CompletedTask;
            }
            catch {/**/}
        }
    }

    internal class RotatingFileLogger : ILogger
    {
        private readonly string _category;
        private readonly RotatingFileLoggerProvider _provider;

        public RotatingFileLogger(string category, RotatingFileLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                                Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            _ = _provider.WriteAsync(_category, logLevel, message, exception);
        }
    }
}
