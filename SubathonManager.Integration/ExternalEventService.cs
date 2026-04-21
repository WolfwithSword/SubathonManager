using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Services;
// ReSharper disable NullableWarningSuppressionIsUsed

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
        if (!((SubathonEventType?)type).IsSubscription()) return false;

        data.TryGetValue("user", out JsonElement elemUser);
        string user = string.IsNullOrWhiteSpace(elemUser.GetString()) ? "EXTERNAL" : elemUser.GetString()!;
        
        if (type.GetSource() == SubathonEventSource.KoFi && !string.Equals(user, "SYSTEM"))
        {
            if (Utils.GetConnection(SubathonEventSource.KoFiTunnel,
                    nameof(SubathonEventSource.KoFiTunnel)).Status)
            {
                return false;
            }
        }
        
        data.TryGetValue("value", out JsonElement elemValue);
        
        string value = elemValue.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(elemValue.GetString()) 
            ? elemValue.GetString()! : "DEFAULT";
        if (string.IsNullOrWhiteSpace(value)) value = "DEFAULT";
        
        data.TryGetValue("amount", out JsonElement elemAmount);
        
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Currency = type == SubathonEventType.KoFiSub ? "member" : "sub",
            User = user,
            Value = $"{value}"
        };
        if (type != SubathonEventType.KoFiSub)
        {
            data.TryGetValue("seconds", out JsonElement elemSeconds);
            data.TryGetValue("points", out JsonElement elemPoints);
            if (elemSeconds.ValueKind == JsonValueKind.Number)
            {
                subathonEvent.SecondsValue = elemSeconds.GetDouble();
            }
            if (elemPoints.ValueKind == JsonValueKind.Number) {
                subathonEvent.PointsValue = elemPoints.GetInt16();
            }
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
    
    
    public static bool ProcessExternalOrder(Dictionary<string, JsonElement> data)
    {
        data.TryGetValue("type", out JsonElement elemType);
        if (elemType.ValueKind != JsonValueKind.String) return false;

        string typeStr = elemType.GetString()!;
        Enum.TryParse<SubathonEventType>(typeStr, ignoreCase: true, out var type);
        data.TryGetValue("user", out JsonElement elemUser);
        string user = string.IsNullOrWhiteSpace(elemUser.GetString()) ? "EXTERNAL" : elemUser.GetString()!;

        if (!((SubathonEventType?)type).IsOrder()) return false; 
        if (type.GetSource() == SubathonEventSource.KoFi && !string.Equals(user, "SYSTEM"))
        {
            if (Utils.GetConnection(SubathonEventSource.KoFiTunnel,
                    nameof(SubathonEventSource.KoFiTunnel)).Status)
            {
                return false;
            }
        }
        
        data.TryGetValue("currency", out JsonElement elemCurrency);
        if (elemCurrency.ValueKind != JsonValueKind.String) return false;

        string currency = elemCurrency.GetString()!.ToUpper();

        data.TryGetValue("amount", out JsonElement elemValue);
        if (elemValue.ValueKind != JsonValueKind.String) return false;
        
        int amt = 1;
        if(data.TryGetValue("quantity", out JsonElement elemQuant))
        {
            if (elemQuant.ValueKind == JsonValueKind.String)
            {
                if (!int.TryParse(elemQuant.GetString()!, out amt)) return false;
            }
            else if (elemQuant.ValueKind == JsonValueKind.Number)
            {
                amt =  elemQuant.GetInt32();
            }
            else
            {
                return false;
            }
        }
        if (!double.TryParse(elemValue.GetString()!, out var value)) return false;

        string orderVal = $"{value}";
        if (type != SubathonEventType.KoFiCommissionOrder)
        {
            string section = $"{type.GetSource()}";
            if (type.GetSource() == SubathonEventSource.GoAffPro)
            {
                section = $"{type}".Replace("Order", "");
            }
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            Enum.TryParse((config.Get(section, $"{type}.Mode", "Dollar")?.Trim() ?? "Dollar"), out OrderTypeModes mode);
            
            currency = mode switch
            {
                OrderTypeModes.Item => "items",
                OrderTypeModes.Order => "order",
                _ => currency
            };
            
            switch (mode)
            {
                case OrderTypeModes.Item:
                    orderVal = amt.ToString();
                    break;
                case OrderTypeModes.Order:
                    orderVal = "New";
                    break;
            }
        }
        
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Currency = currency,
            User = user,
            Value = orderVal,
            Source = user == "SYSTEM" ? SubathonEventSource.Simulated : ((SubathonEventType?)type).GetSource(),
            EventType = type,
            Amount = amt,
            SecondaryValue = $"{value}|{currency}"
        };

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
        if (!((SubathonEventType?)type).IsCurrencyDonation()) return false;
        data.TryGetValue("user", out JsonElement elemUser);
        string user = string.IsNullOrWhiteSpace(elemUser.GetString()) ? "EXTERNAL" : elemUser.GetString()!;
        

        if (type.GetSource() == SubathonEventSource.KoFi && !string.Equals(user, "SYSTEM"))
        {
            if (Utils.GetConnection(SubathonEventSource.KoFiTunnel,
                    nameof(SubathonEventSource.KoFiTunnel)).Status)
            {
                return false;
            }
        }
        
        data.TryGetValue("currency", out JsonElement elemCurrency);
        if (elemCurrency.ValueKind != JsonValueKind.String) return false;

        string currency = elemCurrency.GetString()!.ToUpper();

        data.TryGetValue("amount", out JsonElement elemValue);
        if (elemValue.ValueKind != JsonValueKind.String) return false;

        if (!double.TryParse(elemValue.GetString()!, out var value)) return false;
        
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Currency = currency,
            User = user,
            Value = $"{value}",
            Source = user == "SYSTEM" ? SubathonEventSource.Simulated : ((SubathonEventType?)type).GetSource(),
            EventType = type
        };

        data.TryGetValue("id", out JsonElement elemId);
        if (elemId.ValueKind == JsonValueKind.String && Guid.TryParse(elemId.GetString()!, out var id))
            subathonEvent.Id = id;

        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        
        return true;
    }
}