using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using SubathonManager.Core.Enums;
namespace SubathonManager.Core;

public class Utils
{
    public static TimeSpan ParseDurationString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            Console.WriteLine($"Error - Invalid TimeString to Parse: {input}");
            return TimeSpan.Zero;
        }

        input = input.Trim();
        if (input.Contains(':'))
            return ParseColonDurationString(input);

        return ParseLetterDurationString(input);

    }

    private static TimeSpan ParseColonDurationString(string input)
    {
        var parts = input.Replace(".", ":").Split(":");
        int[] values = Array.ConvertAll(parts, p =>
        {
            if (int.TryParse(p, out var v)) return v;
            return 0;
        });
        int days = 0, hours = 0, minutes = 0, seconds = 0;
        
        switch (values.Length)
        {
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

    private static TimeSpan ParseLetterDurationString(string input)
    {
        if (input.All(char.IsDigit))
        {
            return new TimeSpan(0, 0, 0, int.Parse(input));
        }
        var regex = new Regex(@"(\d+d|\d+h|\d+m|\d+s)", RegexOptions.IgnoreCase);
        
        int days = 0, hours = 0, minutes = 0, seconds = 0;
        foreach(Match match in regex.Matches(input.ToLower()))
        {
            if (!match.Success) continue;
            if (match.ToString().ToLower().Contains("d"))
            {
                days += int.Parse(match.ToString().Replace("d", ""));
            }
            else if (match.ToString().ToLower().Contains("h"))
            {
                hours += int.Parse(match.ToString().Replace("h", ""));
            }
            else if (match.ToString().ToLower().Contains("m"))
            {
                minutes += int.Parse(match.ToString().Replace("m", ""));
            }
            else if (match.ToString().ToLower().Contains("s"))
            {
                seconds += int.Parse(match.ToString().Replace("s", ""));
            }
        }
        return  new TimeSpan(days, hours, minutes, seconds);
    }

    public static Guid CreateGuidFromUniqueString(string key)
    {       
        using var sha1 = SHA1.Create();
        byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(key));

        byte[] guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);

        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | (5 << 4));
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }
    
    public static string TryParseCurrency(string amountString)
    {
        string currency = "";
        var match = Regex.Match(amountString, @"^(?<code>[A-Z]{3})(?![A-Z])");
        if (match.Success)
            currency = match.Groups["code"].Value;
        else
        {
            if (amountString.Contains('$'))
            {
                if (amountString.StartsWith("A$")) currency = "AUD";
                else if (amountString.StartsWith("CA$")) currency = "CAD";
                else if (amountString.StartsWith("R$")) currency = "BRL";
                else if (amountString.StartsWith("HK$")) currency = "HKD";
                else if (amountString.StartsWith("MX$")) currency = "MXN";
                else if (amountString.StartsWith("NT$")) currency = "TWD";
                else if (amountString.StartsWith("NZ$")) currency = "NZD";
            }
            else if (amountString.Contains("₨"))
            {
                if (amountString.StartsWith("PK₨")) currency = "PKR";
                else if (amountString.StartsWith("LK₨")) currency = "LKR";
                else if (amountString.StartsWith("MU₨")) currency = "MUR";
                else if (amountString.StartsWith("NP₨")) currency = "NPR";
            }
            else
            {
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
    
    public static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            value = value.Replace("\"", "\"\"");
            return $"\"{value}\"";
        }
        return value;
    }
    
    public static (bool, double) GetAltCurrencyUseAsDonation(IConfig config, SubathonEventType? eventType)
    {
        double modifier = 1;
        if (!eventType.IsCheerType())
            return (false, 1);
        if (eventType != SubathonEventType.TwitchCheer && eventType != SubathonEventType.PicartoTip)
        {
            double.TryParse(config.Get("Extensions", $"{eventType}.Modifier", "1"), out modifier);
        }

        bool.TryParse(config.Get("Currency", "BitsLikeAsDonation", "False"), out bool useAsDonation);
        return (useAsDonation, modifier);
    }
    
    public sealed class ServiceReconnectState : IDisposable
    {
        public TimeSpan Backoff = TimeSpan.FromSeconds(2);
        public int MaxRetries = 100;
        public TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
        public CancellationTokenSource? Cts;
        public readonly SemaphoreSlim Lock = new(1, 1);
        public int Retries = 0;
        
        private TimeSpan InitialBackOff { get; init; }
        private TimeSpan InitialMaxBackOff { get; init; }
        private int InitialMaxRetries { get; init; }

        public ServiceReconnectState(TimeSpan backoff, int maxRetries, TimeSpan maxBackoff)
        {
            Backoff = backoff;
            MaxRetries = maxRetries;
            MaxBackoff = maxBackoff;
            InitialBackOff = Backoff;
            InitialMaxBackOff = MaxBackoff;
            InitialMaxRetries = MaxRetries;
        }
        
        public ServiceReconnectState() {
            InitialBackOff = Backoff;
            InitialMaxBackOff = MaxBackoff;
            InitialMaxRetries = MaxRetries;
            
        }

        public async Task<bool> IsReconnecting()
        {
            return !(await Lock.WaitAsync(0));
        }

        public void Reset()
        {
            Backoff = InitialBackOff;
            MaxRetries = InitialMaxRetries;
            MaxBackoff = InitialMaxBackOff;
            Retries = 0;
        }

        public void Dispose()
        {
            Cts?.Cancel();
            Cts?.Dispose();
        }
    }

}