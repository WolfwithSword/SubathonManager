using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.UI;

public partial class App {
    private void WireRegistryPersistence() {
        GoAffProStoreRegistry.StoreDiscovered += store => {
            Task.Run(async () => {
                try {
                    await using AppDbContext db2 = await _factory!.CreateDbContextAsync();
                    if (!db2.GoAffProStores.Any(s => s.SiteId == store.SiteId))
                        db2.GoAffProStores.Add(store);
                    var meta = store.SiteId.ToString();
                    if (!db2.SubathonValues.Any(sv =>
                            sv.EventType == SubathonEventType.GoAffProOrder && sv.Meta == meta))
                        db2.SubathonValues.Add(new SubathonValue
                            { EventType = SubathonEventType.GoAffProOrder, Seconds = 12, Meta = meta });
                    await db2.SaveChangesAsync();
                }
                catch (Exception ex) {
                    _logger?.LogError(ex, "Failed to persist discovered GoAffPro store {SiteId}", store.SiteId);
                }

                var cfg = AppServices.Provider.GetRequiredService<IConfig>();
                Utils.DonationSettings[store.InternalName] =
                    cfg.GetBool("GoAffPro", $"{store.InternalName}.CommissionAsDonation");
            });
        };

        MakeShipTrackingRegistry.TrackingUpdated += tracking => {
            Task.Run(async () => {
                try {
                    await using AppDbContext db2 = await _factory!.CreateDbContextAsync();
                    MakeShipTracking? row = db2.MakeShipTrackings.FirstOrDefault(t => t.Id == tracking.Id);
                    if (row == null) return;
                    string oldName = row.Name;
                    row.Name = tracking.Name;
                    row.ShopifyProductId = tracking.ShopifyProductId;
                    row.ProductType = tracking.ProductType;
                    row.Sales = tracking.Sales;
                    row.Orders = tracking.Orders;
                    if (!string.IsNullOrWhiteSpace(oldName) && oldName != "DEFAULT" &&
                        !string.Equals(oldName, tracking.Name, StringComparison.Ordinal)) {
                        List<SubathonValue> overrides = db2.SubathonValues.Where(sv =>
                            (sv.EventType == SubathonEventType.MakeShipPledge ||
                             sv.EventType == SubathonEventType.MakeShipSale)
                            && sv.Meta == oldName).ToList();
                        foreach (SubathonValue sv in overrides)
                            sv.Meta = tracking.Name;

                        List<WheelSpinTrigger> triggers = db2.WheelSpinTriggers.Where(t =>
                            (t.EventType == SubathonEventType.MakeShipPledge ||
                             t.EventType == SubathonEventType.MakeShipSale)
                            && t.TierValue == oldName).ToList();
                        foreach (WheelSpinTrigger t in triggers)
                            t.TierValue = tracking.Name;

                        List<SubathonPrompt> prompts = db2.SubathonPrompts.Where(p =>
                            (p.FilterEventType == SubathonEventType.MakeShipPledge ||
                             p.FilterEventType == SubathonEventType.MakeShipSale)
                            && p.FilterMeta == oldName).ToList();
                        foreach (SubathonPrompt p in prompts)
                            p.FilterMeta = tracking.Name;
                    }

                    await db2.SaveChangesAsync();
                }
                catch (Exception ex) {
                    _logger?.LogError(ex, "Failed to persist MakeShip tracking {Id}", tracking.Id);
                }
            });
        };

        JuniperStoreRegistry.StoreUpdated += store => {
            Task.Run(async () => {
                try {
                    await using AppDbContext db2 = await _factory!.CreateDbContextAsync();
                    JuniperStore? row = db2.JuniperStores.FirstOrDefault(s => s.RowId == store.RowId);
                    if (row == null) return;
                    row.Enabled = store.Enabled;
                    row.LastFetched = store.LastFetched;
                    await db2.SaveChangesAsync();
                }
                catch (Exception ex) {
                    _logger?.LogError(ex, "Failed to persist Juniper store {Name}", store.StoreName);
                }
            });
        };

        JuniperStoreRegistry.ProductUpdated += product => {
            Task.Run(async () => {
                try {
                    await using AppDbContext db2 = await _factory!.CreateDbContextAsync();
                    JuniperProduct? row = db2.JuniperProducts.FirstOrDefault(p => p.ProductId == product.ProductId);
                    if (row == null) return;
                    row.ProductName = product.ProductName;
                    row.Valid = product.Valid;
                    row.LastFetched = product.LastFetched;
                    await db2.SaveChangesAsync();
                }
                catch (Exception ex) {
                    _logger?.LogError(ex, "Failed to persist Juniper product {Id}", product.ProductId);
                }
            });
        };
    }
}
