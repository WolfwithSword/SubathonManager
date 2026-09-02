using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Services;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Integration;

public static class ExternalEventService {
    public static bool ProcessExternalCommand(Dictionary<string, JsonElement> data) {
        data.TryGetValue("command", out JsonElement elemCmd);
        if (elemCmd.ValueKind == JsonValueKind.String && Enum.TryParse
                (elemCmd.GetString(), true, out SubathonCommandType cmd)) {
            var source = SubathonEventSource.External;
            if (data.TryGetValue("source", out JsonElement elemSrc) && elemSrc.ValueKind == JsonValueKind.String
                                                                    && Enum.TryParse(elemSrc.GetString(), true,
                                                                        out SubathonEventSource parsedSrc)
                                                                    && parsedSrc.IsExternalSource())
                source = parsedSrc;

            data.TryGetValue("user", out JsonElement elemUser);
            var fallbackUser = "EXTERNAL";
            string user = string.IsNullOrWhiteSpace(elemUser.GetString()) ? fallbackUser : elemUser.GetString()!;

            data.TryGetValue("message", out JsonElement elemMsg);
            string msg = elemMsg.ValueKind == JsonValueKind.String ? elemMsg.GetString()! : "";

            return CommandService.ChatCommandRequest(source, msg, user, true,
                false, false, DateTime.Now, null, cmd);
        }

        return false;
    }

    public static bool ProcessExternalSub(Dictionary<string, JsonElement> data) {
        data.TryGetValue("type", out JsonElement elemType);
        string typeStr = elemType.GetString()!;
        Enum.TryParse<SubathonEventType>(typeStr, true, out SubathonEventType type);
        if (!((SubathonEventType?)type).IsSubscription()) return false;

        data.TryGetValue("user", out JsonElement elemUser);
        string user = string.IsNullOrWhiteSpace(elemUser.GetString()) ? "EXTERNAL" : elemUser.GetString()!;

        if (type.GetSource() == SubathonEventSource.KoFi && !string.Equals(user, "SYSTEM"))
            if (Utils.GetConnection(SubathonEventSource.KoFiTunnel,
                    nameof(SubathonEventSource.KoFiTunnel)).Status)
                return false;

        data.TryGetValue("value", out JsonElement elemValue);

        string value = elemValue.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(elemValue.GetString())
            ? elemValue.GetString()!
            : "DEFAULT";
        if (string.IsNullOrWhiteSpace(value)) value = "DEFAULT";

        data.TryGetValue("amount", out JsonElement elemAmount);

        var subathonEvent = new SubathonEvent {
            Currency = type == SubathonEventType.KoFiSub ? "member" : "sub",
            User = user,
            Value = $"{value}",
            EventTypeMeta = $"{value}"
        };
        if (type != SubathonEventType.KoFiSub) {
            data.TryGetValue("seconds", out JsonElement elemSeconds);
            data.TryGetValue("points", out JsonElement elemPoints);
            if (elemSeconds.ValueKind == JsonValueKind.Number) subathonEvent.SecondsValue = elemSeconds.GetDouble();
            if (elemPoints.ValueKind == JsonValueKind.Number) subathonEvent.PointsValue = elemPoints.GetInt16();
        }

        if (elemAmount.ValueKind == JsonValueKind.String) {
            subathonEvent.Amount = 1;
            if (int.TryParse(elemAmount.GetString(), out int amtInt) && amtInt > 0)
                subathonEvent.Amount = amtInt;
        }
        else {
            subathonEvent.Amount = elemAmount.ValueKind == JsonValueKind.Number ? elemAmount.GetInt16() : 1;
        }

        subathonEvent.Source =
            user == "SYSTEM" ? SubathonEventSource.Simulated : ((SubathonEventType?)type).GetSource();
        subathonEvent.EventType = type;

        data.TryGetValue("id", out JsonElement elemId);
        if (elemId.ValueKind == JsonValueKind.String && Guid.TryParse(elemId.GetString()!, out Guid id))
            subathonEvent.Id = id;

        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        return true;
    }


    public static bool ProcessExternalOrder(Dictionary<string, JsonElement> data) {
        data.TryGetValue("type", out JsonElement elemType);
        if (elemType.ValueKind != JsonValueKind.String) return false;

        string typeStr = elemType.GetString()!;
        Enum.TryParse<SubathonEventType>(typeStr, true, out SubathonEventType type);

        string? goAffProMeta = null;
        if (type != SubathonEventType.GoAffProOrder &&
            GoAffProOrderHelper.TryGetStoreByOrderKey(typeStr, out GoAffProStore? keyStore)) {
            type = SubathonEventType.GoAffProOrder;
            goAffProMeta = keyStore.SiteId.ToString();
        }
        else if (type.GetLegacyGoAffProSiteId() > 0) {
            goAffProMeta = type.GetLegacyGoAffProSiteId().ToString();
            type = SubathonEventType.GoAffProOrder;
        }

        data.TryGetValue("user", out JsonElement elemUser);
        string user = string.IsNullOrWhiteSpace(elemUser.GetString()) ? "EXTERNAL" : elemUser.GetString()!;

        if (!((SubathonEventType?)type).IsOrder()) return false;
        if (type.GetSource() == SubathonEventSource.KoFi && !string.Equals(user, "SYSTEM"))
            if (Utils.GetConnection(SubathonEventSource.KoFiTunnel,
                    nameof(SubathonEventSource.KoFiTunnel)).Status)
                return false;

        data.TryGetValue("currency", out JsonElement elemCurrency);
        if (elemCurrency.ValueKind != JsonValueKind.String) return false;

        string currency = elemCurrency.GetString()!.ToUpper();

        data.TryGetValue("amount", out JsonElement elemValue);
        if (elemValue.ValueKind != JsonValueKind.String) return false;

        var amt = 1;
        if (data.TryGetValue("quantity", out JsonElement elemQuant)) {
            if (elemQuant.ValueKind == JsonValueKind.String) {
                if (!int.TryParse(elemQuant.GetString()!, out amt)) return false;
            }
            else if (elemQuant.ValueKind == JsonValueKind.Number) {
                amt = elemQuant.GetInt32();
            }
            else {
                return false;
            }
        }

        if (!double.TryParse(elemValue.GetString()!, out double value)) return false;

        var orderVal = $"{value}";
        if (type != SubathonEventType.KoFiCommissionOrder) {
            var section = $"{type.GetSource()}";
            var modeKey = $"{type}";
            if (type == SubathonEventType.GoAffProOrder) {
                section = nameof(SubathonEventSource.GoAffPro);
                modeKey = GoAffProOrderHelper.TryGetStore(goAffProMeta, out GoAffProStore? store)
                    ? store.InternalName
                    : $"{type}";
            }

            var config = AppServices.Provider.GetRequiredService<IConfig>();
            OrderTypeModes mode = config.GetOrderTypeMode(section, modeKey, OrderTypeModes.Dollar);

            currency = mode switch {
                OrderTypeModes.Item => "items",
                OrderTypeModes.Order => "order",
                _ => currency
            };

            switch (mode) {
                case OrderTypeModes.Item:
                    orderVal = amt.ToString();
                    break;
                case OrderTypeModes.Order:
                    orderVal = "New";
                    break;
            }
        }

        var subathonEvent = new SubathonEvent {
            Currency = currency,
            User = user,
            Value = orderVal,
            Source = user == "SYSTEM" ? SubathonEventSource.Simulated : ((SubathonEventType?)type).GetSource(),
            EventType = type,
            EventTypeMeta = goAffProMeta,
            Amount = amt,
            SecondaryValue = $"{value}|{currency}"
        };

        data.TryGetValue("id", out JsonElement elemId);
        if (elemId.ValueKind == JsonValueKind.String && Guid.TryParse(elemId.GetString()!, out Guid id))
            subathonEvent.Id = id;

        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);

        return true;
    }

    public static bool ProcessExternalDonation(Dictionary<string, JsonElement> data) {
        data.TryGetValue("type", out JsonElement elemType);
        if (elemType.ValueKind != JsonValueKind.String) return false;

        string typeStr = elemType.GetString()!;
        Enum.TryParse<SubathonEventType>(typeStr, true, out SubathonEventType type);
        if (!((SubathonEventType?)type).IsCurrencyDonation()) return false;
        data.TryGetValue("user", out JsonElement elemUser);
        string user = string.IsNullOrWhiteSpace(elemUser.GetString()) ? "EXTERNAL" : elemUser.GetString()!;


        if (type.GetSource() == SubathonEventSource.KoFi && !string.Equals(user, "SYSTEM"))
            if (Utils.GetConnection(SubathonEventSource.KoFiTunnel,
                    nameof(SubathonEventSource.KoFiTunnel)).Status)
                return false;

        data.TryGetValue("currency", out JsonElement elemCurrency);
        if (elemCurrency.ValueKind != JsonValueKind.String) return false;

        string currency = elemCurrency.GetString()!.ToUpper();

        data.TryGetValue("amount", out JsonElement elemValue);
        if (elemValue.ValueKind != JsonValueKind.String) return false;

        if (!double.TryParse(elemValue.GetString()!, out double value)) return false;

        var subathonEvent = new SubathonEvent {
            Currency = currency,
            User = user,
            Value = $"{value}",
            Source = user == "SYSTEM" ? SubathonEventSource.Simulated : ((SubathonEventType?)type).GetSource(),
            EventType = type
        };

        data.TryGetValue("id", out JsonElement elemId);
        if (elemId.ValueKind == JsonValueKind.String && Guid.TryParse(elemId.GetString()!, out Guid id))
            subathonEvent.Id = id;

        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);

        return true;
    }
}