using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.Services;

[ExcludeFromCodeCoverage]
public class WheelSpinTriggerService(
    IDbContextFactory<AppDbContext> factory,
    CurrencyService currencyService,
    ILogger<WheelSpinTriggerService>? logger = null)
    : IDisposable, IAppService
{
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        SubathonEvents.SubathonEventProcessed += OnSubathonEventProcessed;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        SubathonEvents.SubathonEventProcessed -= OnSubathonEventProcessed;
        return Task.CompletedTask;
    }

    private async void OnSubathonEventProcessed(SubathonEvent ev, bool wasEffective)
    {
        if (ev.EventType == null) return;
        if (ev.Command != SubathonCommandType.None) return;

        var subType = ev.EventType.GetSubType();
        if (subType is SubathonEventSubType.Unknown or SubathonEventSubType.CommandLike
            or SubathonEventSubType.FollowLike or SubathonEventSubType.RaidLike
            or SubathonEventSubType.TrainLike or SubathonEventSubType.EventLike) return;

        if (subType == SubathonEventSubType.DonationLike &&
            (string.IsNullOrEmpty(ev.Currency) || ev.Currency == "???")) return;

        try
        {
            await ProcessTriggers(ev, subType);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error processing wheel spin triggers for event {EventType}: {Message}",
                ev.EventType, ex.Message);
        }
    }

    private async Task ProcessTriggers(SubathonEvent ev, SubathonEventSubType subType)
    {
        await using var db = await factory.CreateDbContextAsync();

        var isSubLike = subType is SubathonEventSubType.SubLike or SubathonEventSubType.GiftSubLike;

        List<WheelSpinTrigger> candidates = await db.WheelSpinTriggers
            .Where(t => t.IsEnabled && t.EventType == ev.EventType)
            .ToListAsync();

        if (candidates.Count == 0) return;

        // sub/membership events, filter by tier meta
        if (isSubLike)
        {
            candidates = candidates
                .Where(t => string.IsNullOrEmpty(t.TierValue) ||
                            string.Equals(t.TierValue, ev.Value, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        
        if (ev.EventType is SubathonEventType.GoAffProOrder or SubathonEventType.JuniperMerchSale
            or SubathonEventType.MakeShipPledge or SubathonEventType.MakeShipSale)
        {
            candidates = candidates
                .Where(t => OrderMetaFilter.Matches(ev.EventType, ev.EventTypeMeta, t.TierValue))
                .OrderByDescending(t => OrderMetaFilter.Specificity(ev.EventType, ev.EventTypeMeta, t.TierValue))
                .ToList();
        }
        
        if (candidates.Count == 0) return;

        // unique rules, there should only be one match; take first anyways
        // we do only apply for most specific at a time if we have generic then specific
        var trigger = candidates[0];

        int spinsToAdd = await CalculateSpins(ev, trigger, subType);
        if (spinsToAdd < 1) return;

        var currentSpins = await StateValueHelper.GetAsync<int>(factory, StateKeys.WheelSpinsOwed, 0);
        var newSpins = currentSpins + spinsToAdd;
        await StateValueHelper.SetAsync(factory, StateKeys.WheelSpinsOwed, newSpins);

        var history = new WheelSpinTriggerHistory
        {
            TriggerId = trigger.Id,
            TriggeredAt = DateTime.Now,
            TriggerUser = ev.User,
            TriggerSource = ev.Source,
            SpinsAdded = spinsToAdd,
            SubathonEventId = ev.Id,
            SubathonEventType = ev.EventType
        };
        db.WheelSpinTriggerHistories.Add(history);
        await db.SaveChangesAsync();

        history.Trigger = trigger;

        WheelEvents.RaiseSpinsOwedUpdateFromEvent(newSpins);
        WheelEvents.RaiseWheelSpinTriggerFired(trigger, history, newSpins);

        logger?.LogInformation("WheelSpinTrigger fired: {EventType} -> +{Spins} spins (user: {User})",
            ev.EventType, spinsToAdd, ev.User);
    }

    private async Task<int> CalculateSpins(SubathonEvent ev, WheelSpinTrigger trigger, SubathonEventSubType subType)
    {
        switch (subType)
        {
            case SubathonEventSubType.SubLike: // single item
                return trigger.SpinsToAdd;

            case SubathonEventSubType.GiftSubLike:
            {
                if (trigger.CountThreshold is null or <= 0)
                    return trigger.SpinsToAdd;
                int giftCount = ev.Amount;
                int multiplier = giftCount / trigger.CountThreshold.Value;
                return multiplier * trigger.SpinsToAdd;
            }

            case SubathonEventSubType.TokenLike:
            {
                if (trigger.CountThreshold is null or <= 0) return 0;
                if (!double.TryParse(ev.Value, out double tokenCount)) return 0;
                int multiplier = (int)tokenCount / trigger.CountThreshold.Value;
                return multiplier * trigger.SpinsToAdd;
            }

            case SubathonEventSubType.OrderLike:
            {
                if (trigger.CountThreshold is > 0)
                {
                    int itemCount = ev.Amount;
                    int multiplier = itemCount / trigger.CountThreshold.Value;
                    return multiplier * trigger.SpinsToAdd;
                }

                if (trigger.MoneyThreshold is > 0
                    && !string.IsNullOrEmpty(trigger.Currency) && !string.IsNullOrEmpty(ev.Currency)
                    && !string.IsNullOrEmpty(ev.Value))
                {
                    if (!double.TryParse(ev.Value, out double orderValue)) return 0;
                    double converted = await currencyService.ConvertAsync(orderValue, ev.Currency, trigger.Currency);
                    int multiplier = (int)(converted / trigger.MoneyThreshold.Value);
                    return multiplier * trigger.SpinsToAdd;
                }

                // per order and default is just a single item
                return trigger.SpinsToAdd;
            }

            case SubathonEventSubType.DonationLike:
            {
                if (trigger.MoneyThreshold is null or <= 0) return 0;
                if (string.IsNullOrEmpty(trigger.Currency) || string.IsNullOrEmpty(ev.Currency)) return 0;
                if (!double.TryParse(ev.Value, out double donationValue)) return 0;
                double converted = await currencyService.ConvertAsync(donationValue, ev.Currency, trigger.Currency);
                int multiplier = (int)(converted / trigger.MoneyThreshold.Value);
                return multiplier * trigger.SpinsToAdd;
            }

            default:
                return 0;
        }
    }

    public void Dispose()
    {
        SubathonEvents.SubathonEventProcessed -= OnSubathonEventProcessed;
    }
}
