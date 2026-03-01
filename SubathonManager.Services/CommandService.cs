using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Enums;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;

namespace SubathonManager.Services;

public static class CommandService
{
    internal static IConfig? AppConfig { get; private set; }
    
    static CommandService()
    {
        AppConfig ??= AppServices.Provider?.GetRequiredService<IConfig>()!;
    }
    
    public static bool ChatCommandRequest(SubathonEventSource source, string message, string user, 
        bool isBroadcaster, bool isModerator, bool isVip, DateTime? timestamp,
        Guid? overrideGuid = null,
        SubathonCommandType cmdOverride = SubathonCommandType.None)
    {
        timestamp ??= DateTime.Now;
        if (source == SubathonEventSource.External)
            message = $"EXTERNAL {message}";
        SubathonCommandType command = ValidateCommand(message, cmdOverride);
        if (command == SubathonCommandType.Unknown) return false;
        
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = source,
            EventTimestamp = timestamp.Value,
            Command = command,
            EventType = SubathonEventType.Command
        };

        if (overrideGuid != null)
            subathonEvent.Id = overrideGuid.Value;

        bool validParams = ValidateParameters(subathonEvent, message);
        if (!validParams) return false;

        bool validUser = source == SubathonEventSource.External || 
                         ValidateUser(subathonEvent, user, isBroadcaster, isModerator, isVip);
        if (!validUser) return false;

        subathonEvent.User = user.Trim();

        if (subathonEvent.Command == SubathonCommandType.RefreshOverlays)
            OverlayEvents.RaiseOverlayRefreshAllRequested();
        else
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        return true;
    }

    internal static void SetConfig(IConfig config)
    {
        AppConfig = config;
    }

    private static bool ValidateUser(SubathonEvent subathonEvent, string user, bool isBroadcaster,
        bool isModerator, bool isVip)
    {
        if (isBroadcaster) return true;

        string configKey = $"Commands.{subathonEvent.Command}.permissions";
        // All Commands are under twitch section at the moment, despite being cross platform chat commands
        // Should update eventually, or is it too late
        if (isModerator && bool.TryParse(
                AppConfig!.Get("Chat", $"{configKey}.Mods"), out var modPerms) && modPerms)
            return true;
        if (isVip && bool.TryParse(
                AppConfig!.Get("Chat", $"{configKey}.VIPs"), out var vipPerms) && vipPerms)
            return true;
        
        string[] whitelist = AppConfig!.Get("Chat", $"{configKey}.Whitelist")!.ToLower().Split(',');

        if (whitelist.Contains(user.ToLower().Trim())) return true;
        
        return false;
    }

    private static SubathonCommandType ValidateCommand(string message, SubathonCommandType cmdOverride)
    {
        if (cmdOverride != SubathonCommandType.None && cmdOverride != SubathonCommandType.Unknown) return cmdOverride;
        string[] parts = message.Split(' ');
        string cmdName = parts[0].Substring(1, parts[0].Length -1 ).Trim();
        
        foreach (var keyData in AppConfig!.GetSection("Chat")!)
        {
            if (!keyData.KeyName.StartsWith("Commands.")) continue;

            if (!keyData.Value.Equals(cmdName, StringComparison.InvariantCultureIgnoreCase)) continue;
            if (Enum.TryParse<SubathonCommandType>(keyData.KeyName.Split('.')[1] ?? "Unknown", 
                    out SubathonCommandType command))
                return command;
        }
        
        return SubathonCommandType.Unknown;
    }

    private static bool ValidateParameters(SubathonEvent subathonEvent, string message)
    {
        string[] parts = message.Split(' ');

        if (subathonEvent.Command.IsParametersRequired() && parts.Length <= 1) return false;
         if (!subathonEvent.Command.IsParametersRequired())
        {
            subathonEvent.Value = $"{subathonEvent.Command}";
            return true;
        }

        bool isValid = false;
        switch (subathonEvent.Command)
        {
            case SubathonCommandType.AddMoney:
            case SubathonCommandType.SubtractMoney:
                if (parts.Length >= 3 && double.TryParse(parts[1], out double value))
                {
                    if (value <= 0) break;
                    var currencyService = AppServices.Provider.GetRequiredService<CurrencyService>();
                    string currency = parts[2];
                    if (!currencyService.IsValidCurrency(currency)) break;
                    subathonEvent.Value = $"{value:N2}";
                    subathonEvent.Currency = currency.ToUpper().Trim();
                    // event service sets it from command to donation adjustment
                    isValid = true;
                }
                break;
            case SubathonCommandType.AddPoints:
            case SubathonCommandType.SubtractPoints:
            case SubathonCommandType.SetPoints:
                if (parts.Length >= 2 && int.TryParse(parts[1], out var parsedInt1))
                {
                    subathonEvent.Value = $"{subathonEvent.Command} {parsedInt1}";
                    subathonEvent.PointsValue = parsedInt1;
                    subathonEvent.SecondsValue = 0;
                    isValid = parsedInt1 > 0 && subathonEvent.Command != SubathonCommandType.SetPoints ||
                              subathonEvent.Command == SubathonCommandType.SetPoints;
                }

                break;
            case SubathonCommandType.AddTime:
            case SubathonCommandType.SubtractTime:
            case SubathonCommandType.SetTime:
                string timeSetting = string.Join(' ', parts[1..]);
                TimeSpan time = Utils.ParseDurationString(timeSetting);
                if (time.Equals(TimeSpan.Zero)) return false;

                subathonEvent.Value = $"{subathonEvent.Command} {timeSetting}";
                subathonEvent.PointsValue = 0;
                subathonEvent.SecondsValue = time.TotalSeconds;
                isValid = true;
                break;
            case SubathonCommandType.SetMultiplier:
                double multiplier = double.MinValue;
                bool applyTime = false;
                bool applyPoints = false;
                string durationString = "";

                foreach (string part in parts)
                {
                    if (part.ToLower().Contains('x') && multiplier <= double.MinValue + 5)
                    {
                        if (!double.TryParse(part.ToLower().Split('x')[0].Trim(), out multiplier))
                        {
                            multiplier = double.MinValue;
                            continue;
                        }

                        if (part.ToLower().Contains('p'))
                            applyPoints = true;
                        if (part.ToLower().Contains('t'))
                            applyTime = true;
                    }

                    if (!part.StartsWith('!') && !part.ToLower().Contains('x') && part.Any(c => char.IsDigit(c)))
                    {
                        durationString += part;
                    }
                }

                if ((!applyPoints && !applyTime) || (multiplier.Equals(1) || !(multiplier > 0)))
                {
                    subathonEvent.Value = $"{SubathonCommandType.SetMultiplier} Failed";
                    subathonEvent.Command = SubathonCommandType.StopMultiplier;
                    isValid = true;
                }

                else
                {
                    if (multiplier <= double.MinValue + 5) return false;
                    TimeSpan duration = Utils.ParseDurationString(durationString);
                    durationString = duration == TimeSpan.Zero ? "x" : ((int)duration.TotalSeconds).ToString();
                    string dataStr = $"{multiplier}|{durationString}s|{applyPoints}|{applyTime}";

                    subathonEvent.Value = dataStr;
                    isValid = true;
                }

                break;
        }

        return isValid;
    }
}