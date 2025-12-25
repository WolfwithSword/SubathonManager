using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core;
namespace SubathonManager.Data;

public class SubathonValueConfigHelper
{
    private readonly ILogger? _logger;
    private readonly IDbContextFactory<AppDbContext> _factory;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        },
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public SubathonValueConfigHelper(IDbContextFactory<AppDbContext>? factory, ILogger? logger)
    {
        _factory = factory ?? AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        _logger = logger ?? AppServices.Provider?.GetRequiredService<ILogger<SubathonValueConfigHelper>>();
    }
    
    public string GetAllAsJson()
    {
        using var db =  _factory.CreateDbContext();

        var values = db.SubathonValues
            .AsNoTracking()
            .ToList();

        var dtoList = values.Select(v => v.ToObject());

        return JsonSerializer.Serialize(dtoList, JsonOptions);
    }
    
    public async Task<string> GetAllAsJsonAsync(List<SubathonEventSource>? filterSources = null)
    {
        if (filterSources == null || filterSources.Count == 0) filterSources = Enum.GetValues<SubathonEventSource>().ToList();
        // filter by source list, future scope
        await using var db = await _factory.CreateDbContextAsync();

        var values = await db.SubathonValues
            .AsNoTracking()
            .ToListAsync();

        var dtoList = values.Where(v => filterSources.Contains((
            (SubathonEventType?)v.EventType).GetSource())).Select(v => v.ToObject());
        return JsonSerializer.Serialize(dtoList, JsonOptions);
    }

    public async Task<int> PatchFromJsonAsync(string json)
    {
        List<SubathonValueDto>? incoming = null;
        List<SubathonValueDto>? success = new ();
        try
        {
            incoming = JsonSerializer.Deserialize<List<SubathonValueDto>>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            string msg = "Could not parse Value Config Patch: " + json;
            ErrorMessageEvents.RaiseErrorEvent("ERROR", "SYSTEM", msg, DateTime.Now);
            _logger?.LogError(ex.Message, msg);
            return -1;
        }

        if (incoming == null || incoming.Count == 0)
            return -1;

        await using var db = await _factory.CreateDbContextAsync();

        var dbValues = await db.SubathonValues.ToListAsync();

        int patched = 0;

        foreach (var dto in incoming)
        {
            var match = dbValues.FirstOrDefault(v =>
                v.EventType == dto.EventType &&
                v.Meta == dto.Meta &&
                ((SubathonEventType?)v.EventType).GetSource() == dto.Source
            );

            if (match == null)
                continue;

            if (match.PatchByObject(dto))
            {
                patched++;
                success.Add(dto);
            }
        }

        try
        {
            if (patched > 0)
            {
                await db.SaveChangesAsync();
                var newData = await GetAllAsJsonAsync();
                SubathonEvents.RaiseSubathonValueConfigRequested(newData);
                SubathonEvents.RaiseSubathonValueConfigUpdatedRemote();
                SubathonEvents.RaiseSubathonValuesPatched(success);
            }
        }
        catch (Exception ex)
        {
            string msg = "Could not save Value Config Patch: " + json;
            ErrorMessageEvents.RaiseErrorEvent("ERROR", "SYSTEM", msg, DateTime.Now);
            _logger?.LogError(ex.Message, msg);
            return -1;
        }

        _logger?.LogInformation("Patched {Count} SubathonValues", patched);

        return patched;
    }

}