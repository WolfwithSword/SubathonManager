using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Core;

public static class Utils
{
    
    public static readonly Dictionary<string, bool> DonationSettings = new Dictionary<string, bool>(); 
    
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

    public static Guid CreateGuidFromUniqueString(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Guid.Empty;
        using var sha1 = SHA1.Create();
        byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(key));

        byte[] guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);

        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | (5 << 4));
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return new Guid(guidBytes);
    }

    private string GetTruncatedHash(SHA256 sha256, string part, int length = 4)
    {
        return Convert.ToHexString(sha256.ComputeHash(
                Encoding.UTF8.GetBytes(part)))
            .ToLowerInvariant().Substring(0, length);
    }
    
    // todo if widgets are not in a root then when i make these additional resource folders, they will not link up nicely
    
    // what if Overlay has new field -> ImportedPath 
    // only filled on imported overlays (or duplicated from imported)
    // and we put all external deps in here as a lookup source of truth
    // problem -> if the user then manually adds more? But those will self resolve first so no lookup ez
    // problem 2 -> if they re-export it, all previous should be relative and won't re-hash...
    //              clear the folder, it only is set on import
    
    
    
    // IDEA
    // All files in an export must be relative to the folder widgets are all in or their children.
    // but you can add more folders/files if they are at root or lower of widgets found in overlay
    // PROBLEM when if structure is like root/widgets/htmls, root/css, root/media. It breaks.
    // for fullpath variables unless we can make it relative, we set it to empty for user, export/import error treatment
    // 
    
    
    // problem also when exporting widgets, we will need to still handle dupe names at root by hashing its path during export
    // to be a new folder in import
    
    
    /**
     *  OVERLAY X
     *  Has widgets timer, points, timer (but diff)
     * 
     *  C:/cool/widgets/timer.html  -> driver for widget
     *  C:/cool/widgets/points.html
     *  C:/cool/widgets/timer.css  -> used by both timer and points html in cool/widgets as relative path
     *  F:/not/widgets/timer.html -> driver for widget
     *  C:/cool/resources/smile.png  -> referred to by timer.css in cool/widgets as ../resources/smile.png
     *  H:/sounds/boom.mp3  -> variable inside of points.html
     *
     * Export structure
     *  overlay-1234.smo
     *      a1b2c3
     *          timer.html
     *          points.html
     *          timer.css   (how does this get in here)
     *      g5f3f1
     *          timer.html
     *      ???
     *
     *
     * Idea
     * For each html file, we include its folder path in a list, for parent directory.
     * We say "hey, we are going to zip up the whole folder this is in"
     * allow user to add other folders?
     *    if existing folders are children of parent folders, then we collapse into them and edit path refs for htmls
     * no full paths will be allowed essentially
     * if full path is a variable, we can download it as a resource thingy and overwrite :) 
     *
     * Export structure
     * added C:/cool as folder to zip
     *  overlay-1234.smo
     *      w1q2q4  (Hash the root folder selected as a string and make a folder in zip) (maybe be truncated)
     *         resources/
     *          smile.png
     *         widgets/
     *          timer.html
     *          points.html
     *          timer.css   (how does this get in here)
     *      g5f3f1
     *          timer.html
     *      ???
     *
     *
     *  shared - take lowest common root of folders for html paths
     *  - allow specifying a higher parent of an existing path
     *  - -preview maybe?
     *  -
     *
     *  user will see
     *  C:/cool/widgets
     *  F:/notcool/widgets
     * user then can set
     *  C:/cool
     *  F:/notcool/widgets
     *
     * if external was set as variable, we will handle including it as a loose file in a resources folder at overlay root
     *
     * ability to 1) add new files and folders to export
     * ability to 2) raise level of export folders for widgets
     * if they coincide at some level? rewrite var to the resolved one from higher level
     *
     * preview of full file tree(s)?
     *  - separate from folder selection maybe for raising levels?
     *    - or show what the *root* level folder is for it all
     *    - or we auto detect it based on the Lowest Complete Shared Root  - nvm this is weird for when multiple
     *       - instead, we have them preselect roots and then go inside to folder
     *       - aka - you select roots, and then filter contents
     *             - yeah
     *          - or maybe not, if it is shared then we can do like empty path stuff except children
     *            and like, only top level is hashed. This also works. Lowest complete shared root but only filtered on files
     *
     *       it will show initially, the parent of all html files, also let you manually add stuff.
     *       then, we detect lowest shared roots and make em hashed at that level, retain below.
     *
     *       when things are added, we show the folder but only the file selected (if folder is selected, all contents are)
     *           -- typical file tree select stuff maybe 
     * 
     *  - can still do specific folder or file adding?
     *  - could allow ignoring certain files for zip?  
     */
    
    public static string CreateHashPath(string path)
    {
        using var sha256 = SHA256.Create(); 
        
        
        var hashedPath = "";
        var split = path.Replace("\\","/").Split("/");
        foreach (var s in split)
        {
            // keep track if last? 
            hashedPath += s;
        }
        // if last element contains . then we do nothing to it
         

        return hashedPath;
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
        if (string.IsNullOrWhiteSpace(value)) return "";
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

        bool useAsDonation = config.GetBool("Currency", "BitsLikeAsDonation", false);
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
    
    public static class SingleInstanceHelper
    {
        public const int WM_SHOWAPP = 0x0400 + 1;

        [DllImport("user32")]
        public static extern bool PostMessage(
            IntPtr hwnd,
            int msg,
            IntPtr wparam,
            IntPtr lparam);
    }

}