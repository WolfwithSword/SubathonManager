using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;

namespace SubathonManager.Core;

public static class Utils {
    public static readonly Dictionary<string, bool> DonationSettings = new();

    private static readonly ConcurrentDictionary<(SubathonEventSource Source, string Service), IntegrationConnection>
        ConnectionDetails = new();

    public static string? PendingOverlayImportPath { get; set; }
    public static string? PendingWidgetPackImportPath { get; set; }
    public static OAuthCallback? PendingOAuthCallback { get; set; }

    public static IEnumerable<IntegrationConnection> GetAllConnections() {
        return ConnectionDetails.Values;
    }

    public static IntegrationConnection GetConnection(SubathonEventSource source, string service) {
        ConnectionDetails.TryGetValue((source, service), out IntegrationConnection? conn);
        if (conn != null) return conn;
        conn = new IntegrationConnection {
            Source = source,
            Service = service,
            Name = "",
            Status = false,
            Configured = false
        };
        UpdateConnection(conn);

        return conn;
    }

    public static void UpdateConnection(IntegrationConnection connection) {
        (SubathonEventSource Source, string Service) key = (connection.Source, connection.Service);

        ConnectionDetails.AddOrUpdate(
            key,
            connection,
            (_, _) => connection
        );
    }

    public static TimeSpan ParseDurationString(string input) {
        if (string.IsNullOrWhiteSpace(input)) {
            Console.WriteLine($"Error - Invalid TimeString to Parse: {input}");
            return TimeSpan.Zero;
        }

        input = input.Trim();
        if (input.Contains(':'))
            return ParseColonDurationString(input);

        return ParseLetterDurationString(input);
    }

    private static TimeSpan ParseColonDurationString(string input) {
        string[] parts = input.Replace(".", ":").Split(":");
        int[] values = Array.ConvertAll(parts, p => {
            if (int.TryParse(p, out int v)) return v;
            return 0;
        });
        int days = 0, hours = 0, minutes = 0, seconds = 0;

        switch (values.Length) {
            case 4:
                days = values[0];
                hours = values[1];
                minutes = values[2];
                seconds = values[3];
                break;
            case 3:
                hours = values[0];
                minutes = values[1];
                seconds = values[2];
                break;
            case 2:
                minutes = values[0];
                seconds = values[1];
                break;
            case 1:
                seconds = values[0];
                break;
        }

        return new TimeSpan(days, hours, minutes, seconds);
    }

    private static TimeSpan ParseLetterDurationString(string input) {
        if (input.All(char.IsDigit)) return new TimeSpan(0, 0, 0, int.Parse(input));
        var regex = new Regex(@"(\d+d|\d+h|\d+m|\d+s)", RegexOptions.IgnoreCase);

        int days = 0, hours = 0, minutes = 0, seconds = 0;
        foreach (Match match in regex.Matches(input.ToLower())) {
            if (!match.Success) continue;
            if (match.ToString().ToLower().Contains("d"))
                days += int.Parse(match.ToString().Replace("d", ""));
            else if (match.ToString().ToLower().Contains("h"))
                hours += int.Parse(match.ToString().Replace("h", ""));
            else if (match.ToString().ToLower().Contains("m"))
                minutes += int.Parse(match.ToString().Replace("m", ""));
            else if (match.ToString().ToLower().Contains("s")) seconds += int.Parse(match.ToString().Replace("s", ""));
        }

        return new TimeSpan(days, hours, minutes, seconds);
    }

    public static Guid TryParseGuid(string? value) {
        if (value != null && Guid.TryParse(value, out Guid g)) return g;
        return CreateGuidFromUniqueString(value ?? Guid.NewGuid().ToString());
    }

    public static Guid CreateGuidFromUniqueString(string? key) {
        if (string.IsNullOrWhiteSpace(key)) return Guid.Empty;
        using var sha1 = SHA1.Create();
        byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(key));

        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);

        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | (5 << 4));
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }

    public static string TryParseCurrency(string amountString) {
        var currency = "";
        Match match = Regex.Match(amountString, @"^(?<code>[A-Z]{3})(?![A-Z])");
        if (match.Success) {
            currency = match.Groups["code"].Value;
        }
        else {
            if (amountString.Contains('$')) {
                if (amountString.StartsWith("A$")) currency = "AUD";
                else if (amountString.StartsWith("CA$")) currency = "CAD";
                else if (amountString.StartsWith("R$")) currency = "BRL";
                else if (amountString.StartsWith("HK$")) currency = "HKD";
                else if (amountString.StartsWith("MX$")) currency = "MXN";
                else if (amountString.StartsWith("NT$")) currency = "TWD";
                else if (amountString.StartsWith("NZ$")) currency = "NZD";
            }
            else if (amountString.Contains("₨")) {
                if (amountString.StartsWith("PK₨")) currency = "PKR";
                else if (amountString.StartsWith("LK₨")) currency = "LKR";
                else if (amountString.StartsWith("MU₨")) currency = "MUR";
                else if (amountString.StartsWith("NP₨")) currency = "NPR";
            }
            else {
                if (amountString.StartsWith("₩")) currency = "KRW";
                else if (amountString.StartsWith("₱")) currency = "PHP";
                else if (amountString.StartsWith("₫")) currency = "VND";
                else if (amountString.StartsWith("₦")) currency = "NGN";
                else if (amountString.StartsWith("₴")) currency = "UAH";
                else if (amountString.StartsWith("₲")) currency = "PYG"; // 
                else if (amountString.StartsWith("₡")) currency = "CRC";
                else if (amountString.StartsWith("₺")) currency = "TRY";
                else if (amountString.StartsWith("₼")) currency = "AZN";
                else if (amountString.StartsWith("₸")) currency = "KZT";
                else if (amountString.StartsWith("₭")) currency = "LAK";
                else if (amountString.StartsWith("₾")) currency = "GEL";
                else if (amountString.StartsWith("₮")) currency = "MNT";
                else if (amountString.StartsWith("₹")) currency = "INR";
                else if (amountString.StartsWith("₣")) currency = "CHF";
            }
        }

        return currency;
    }

    public static string EscapeCsv(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')) {
            value = value.Replace("\"", "\"\"");
            return $"\"{value}\"";
        }

        return value;
    }

    public static (bool, double) GetAltCurrencyUseAsDonation(IConfig config, SubathonEventType? eventType) {
        double modifier = 1;
        if (!eventType.IsToken())
            return (false, 1);
        if (eventType != SubathonEventType.TwitchCheer && eventType != SubathonEventType.PicartoTip)
            double.TryParse(config.Get("Extensions", $"{eventType}.Modifier", "1"), out modifier);

        bool useAsDonation = config.GetBool("Currency", "BitsLikeAsDonation");
        return (useAsDonation, modifier);
    }

    public static DateTime? GetAccessTokenExpiry(string accessToken) {
        try {
            string[] parts = accessToken.Split('.');
            if (parts.Length != 3)
                return null;
            string payload = parts[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4) {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            string json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("exp", out JsonElement expProp))
                return null;

            long expUnix = expProp.GetInt64();
            return DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
        }
        catch {
            return null;
        }
    }

    public static bool IsCommissionAsDonation(IConfig config, SubathonEvent ev) {
        if (!ev.EventType.IsOrder()) return false;

        if (ev.EventType == SubathonEventType.GoAffProOrder) {
            if (string.IsNullOrEmpty(ev.EventTypeMeta)) return false;
            if (!GoAffProOrderHelper.TryGetStore(ev.EventTypeMeta, out GoAffProStore? store)) return false;
            return config.GetBool("GoAffPro", $"{store.InternalName}.CommissionAsDonation");
        }

        return config.GetBool(
            ev.EventType.GetSource().ToString(),
            $"{ev.EventType.ToString()?.Split("Order")[0]}.CommissionAsDonation",
            ev.EventType.GetSource() != SubathonEventSource.GoAffPro);
    }

    public sealed class ServiceReconnectState : IDisposable {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public TimeSpan Backoff = TimeSpan.FromSeconds(2);
        public CancellationTokenSource? Cts;
        public bool InfiniteRetries;
        public TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
        public int MaxRetries = 100;
        public int Retries;

        public ServiceReconnectState(TimeSpan backoff, int maxRetries, TimeSpan maxBackoff,
            bool infiniteRetries = false) {
            Backoff = backoff;
            MaxRetries = maxRetries;
            MaxBackoff = maxBackoff;
            InitialBackOff = Backoff;
            InitialMaxBackOff = MaxBackoff;
            InitialMaxRetries = MaxRetries;
            InfiniteRetries = infiniteRetries;
        }

        public ServiceReconnectState() {
            InitialBackOff = Backoff;
            InitialMaxBackOff = MaxBackoff;
            InitialMaxRetries = MaxRetries;
        }

        private TimeSpan InitialBackOff { get; }
        private TimeSpan InitialMaxBackOff { get; }
        private int InitialMaxRetries { get; }

        public void Dispose() {
            Cts?.Cancel();
            Cts?.Dispose();
        }

        public async Task<bool> IsReconnecting() {
            return !await Lock.WaitAsync(0);
        }

        public void Reset() {
            Backoff = InitialBackOff;
            MaxRetries = InitialMaxRetries;
            MaxBackoff = InitialMaxBackOff;
            Retries = 0;
        }
    }


    [ExcludeFromCodeCoverage]
    public static class SingleInstanceHelper {
        public const int WM_SHOWAPP = 0x0400 + 1;
        public const int WM_COPYDATA = 0x004A;

        [DllImport("user32")]
        public static extern bool PostMessage(
            IntPtr hwnd,
            int msg,
            IntPtr wparam,
            IntPtr lparam);

        [DllImport("user32")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

        public static void SendStringMessage(IntPtr hWnd, ProtocolMessageType type, string message) {
            byte[] bytes = Encoding.Unicode.GetBytes(message);

            var cds = new COPYDATASTRUCT {
                cbData = bytes.Length,
                lpData = Marshal.AllocHGlobal(bytes.Length)
            };

            Marshal.Copy(bytes, 0, cds.lpData, bytes.Length);

            SendMessage(hWnd, WM_COPYDATA, IntPtr.Zero, ref cds);

            Marshal.FreeHGlobal(cds.lpData);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }
    }
}