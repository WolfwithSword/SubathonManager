using System.Text.RegularExpressions;

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
        if (input.Contains(":"))
        {
            return ParseDurationString(input);
        }

        return ParseLetterDurationString(input);

    }

    private static TimeSpan ParseColonDurationString(string input)
    {
        var parts = input.Split(":");
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
        var regex = new Regex(@"(\d+h|\d+m|\d+s)", RegexOptions.IgnoreCase);
        
        int days = 0, hours = 0, minutes = 0, seconds = 0;
        foreach(Match match in regex.Matches(input))
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
}