using System.Text.Json;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Services;

namespace SubathonManager.Integration;

public static class ExternalEventService
{

    public static bool ProcessExternalCommand(Dictionary<string, JsonElement> data)
    {
        data.TryGetValue("command", out JsonElement elemCmd);
        if (elemCmd.ValueKind == JsonValueKind.String && Enum.TryParse<SubathonCommandType>
                (elemCmd.GetString(), ignoreCase: true, out SubathonCommandType cmd) )
        {
            data.TryGetValue("user", out JsonElement elemUser);
            string user = string.IsNullOrWhiteSpace(elemUser.GetString()) ? "EXTERNAL" : elemUser.GetString()!;
            
            data.TryGetValue("message", out JsonElement elemMsg);
            string msg = elemMsg.ValueKind == JsonValueKind.String ? elemMsg.GetString()! : "";
            
            return CommandService.ChatCommandRequest(SubathonEventSource.External, msg, user, true,
                false, false, DateTime.Now, null, cmd);
        }

        return false;
    }
    
    public static bool ProcessExternalSub(Dictionary<string, JsonElement> data)
    {
        data.TryGetValue("type", out JsonElement elemType);
        string typeStr = elemType.GetString()!;
        Enum.TryParse<SubathonEventType>(typeStr, ignoreCase: true, out var type);
        
        data.TryGetValue("user", out JsonElement elemUser);
        string user = string.IsNullOrWhiteSpace(elemUser.GetString()) ? "EXTERNAL" : elemUser.GetString()!;
        
        data.TryGetValue("value", out JsonElement elemValue);
        
        string value = elemValue.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(elemValue.GetString()) 
            ? elemValue.GetString()! : "External";
        if (string.IsNullOrWhiteSpace(value)) value = "External";
        
        data.TryGetValue("amount", out JsonElement elemAmount);
        
        SubathonEvent subathonEvent = new SubathonEvent();
        subathonEvent.Currency = type == SubathonEventType.KoFiSub ? "member" : "sub";
        subathonEvent.User = user;
        subathonEvent.Value = $"{value}";
        if (type != SubathonEventType.KoFiSub)
        {
            data.TryGetValue("seconds", out JsonElement elemSeconds);
            data.TryGetValue("points", out JsonElement elemPoints);
            if (elemSeconds.ValueKind != JsonValueKind.Number || elemPoints.ValueKind != JsonValueKind.Number)
                return false;
            subathonEvent.SecondsValue = elemSeconds.GetDouble();
            subathonEvent.PointsValue = elemPoints.GetInt16();
        }

        subathonEvent.Amount = elemAmount.ValueKind == JsonValueKind.Number ? elemAmount.GetInt16() : 1;
        subathonEvent.Source = user == "SYSTEM" ? SubathonEventSource.Simulated : ((SubathonEventType?)type).GetSource();
        subathonEvent.EventType = type;
        
        data.TryGetValue("id", out JsonElement elemId);
        if (elemId.ValueKind == JsonValueKind.String && Guid.TryParse(elemId.GetString()!, out var id))
            subathonEvent.Id = id;
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        return true;
    }
    
    
    public static bool ProcessExternalDonation(Dictionary<string, JsonElement> data)
    {
        data.TryGetValue("type", out JsonElement elemType);
        if (elemType.ValueKind != JsonValueKind.String) return false;

        string typeStr = elemType.GetString()!;
        Enum.TryParse<SubathonEventType>(typeStr, ignoreCase: true, out var type);
        
        data.TryGetValue("currency", out JsonElement elemCurrency);
        if (elemCurrency.ValueKind != JsonValueKind.String) return false;

        string currency = elemCurrency.GetString()!.ToUpper();
        
        data.TryGetValue("user", out JsonElement elemUser);
        string user = string.IsNullOrWhiteSpace(elemUser.GetString()) ? "EXTERNAL" : elemUser.GetString()!;
        
        
        data.TryGetValue("amount", out JsonElement elemValue);
        if (elemValue.ValueKind != JsonValueKind.String) return false;

        if (!double.TryParse(elemValue.GetString()!, out var value)) return false;
        
        SubathonEvent subathonEvent = new SubathonEvent();
        subathonEvent.Currency = currency;
        subathonEvent.User = user;
        subathonEvent.Value = $"{value}";
        subathonEvent.Source = user == "SYSTEM" ? SubathonEventSource.Simulated : ((SubathonEventType?)type).GetSource();
        subathonEvent.EventType = type;
        
        data.TryGetValue("id", out JsonElement elemId);
        if (elemId.ValueKind == JsonValueKind.String && Guid.TryParse(elemId.GetString()!, out var id))
            subathonEvent.Id = id;

        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        
        return true;
    }
}