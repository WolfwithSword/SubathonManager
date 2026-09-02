using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;

namespace SubathonManager.Data;

public class SubathonValueConfigHelper {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        Converters = {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        },
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger? _logger;

    public SubathonValueConfigHelper(IDbContextFactory<AppDbContext>? factory, ILogger? logger) {
        _factory = factory ?? AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        _logger = logger ?? AppServices.Provider?.GetRequiredService<ILogger<SubathonValueConfigHelper>>();
    }

    public string GetAllAsJson() {
        using AppDbContext db = _factory.CreateDbContext();

        List<SubathonValue> values = db.SubathonValues
            .AsNoTracking()
            .ToList();

        IEnumerable<SubathonValueDto> dtoList = values.Select(v => v.ToObject());

        return JsonSerializer.Serialize(dtoList, JsonOptions);
    }

    public async Task<string> GetAllAsJsonAsync(List<SubathonEventSource>? filterSources = null) {
        if (filterSources == null || filterSources.Count == 0)
            filterSources = Enum.GetValues<SubathonEventSource>().ToList();
        // filter by source list, future scope
        await using AppDbContext db = await _factory.CreateDbContextAsync();

        List<SubathonValue> values = await db.SubathonValues
            .AsNoTracking()
            .ToListAsync();

        IEnumerable<SubathonValueDto> dtoList = values.Where(v => filterSources.Contains((
            (SubathonEventType?)v.EventType).GetSource())).Select(v => v.ToObject());
        return JsonSerializer.Serialize(dtoList, JsonOptions);
    }

    public async Task<int> PatchFromJsonDataAsync(JsonElement data) {
        string json = JsonSerializer.Serialize(data, JsonOptions);
        return await PatchFromJsonAsync(json);
    }

    public async Task<int> PatchFromJsonAsync(string json) {
        List<SubathonValueDto>? incoming = null;
        List<SubathonValueDto>? success = new();
        try {
            incoming = JsonSerializer.Deserialize<List<SubathonValueDto>>(json, JsonOptions);
        }
        catch (Exception ex) {
            string msg = "Could not parse Value Config Patch: " + json;
            ErrorMessageEvents.RaiseErrorEvent("ERROR", "SYSTEM", msg, DateTime.Now);
            _logger?.LogError(ex.Message, msg);
            return -1;
        }

        if (incoming == null || incoming.Count == 0)
            return -1;

        await using AppDbContext db = await _factory.CreateDbContextAsync();

        List<SubathonValue> dbValues = await db.SubathonValues.ToListAsync();

        var patched = 0;

        foreach (SubathonValueDto dto in incoming) {
            string meta = dto.Meta;
            if (dto is { Source: SubathonEventSource.GoAffPro, EventType: SubathonEventType.GoAffProOrder }
                && !int.TryParse(dto.Meta, out _)) {
                if (!GoAffProStoreRegistry.TryGetByInternalName(dto.Meta, out GoAffProStore? store)
                    && !GoAffProOrderHelper.TryGetStoreByOrderKey(dto.Meta, out store))
                    continue;
                meta = store.SiteId.ToString();
            }

            SubathonValue? match = dbValues.FirstOrDefault(v =>
                v.EventType == dto.EventType &&
                v.Meta == meta &&
                ((SubathonEventType?)v.EventType).GetSource() == dto.Source
            );

            if (match == null)
                continue;

            if (match.PatchByObject(dto)) {
                patched++;
                success.Add(dto);
            }
        }

        try {
            if (patched > 0) {
                await db.SaveChangesAsync();
                string newData = await GetAllAsJsonAsync();
                SubathonEvents.RaiseSubathonValueConfigRequested(newData);
                SubathonEvents.RaiseSubathonValueConfigUpdatedRemote();
                SubathonEvents.RaiseSubathonValuesPatched(success);
            }
        }
        catch (Exception ex) {
            string msg = "Could not save Value Config Patch: " + json;
            ErrorMessageEvents.RaiseErrorEvent("ERROR", "SYSTEM", msg, DateTime.Now);
            _logger?.LogError(ex.Message, msg);
            return -1;
        }

        _logger?.LogInformation("Patched {Count} SubathonValues", patched);

        return patched;
    }
}