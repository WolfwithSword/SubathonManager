using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;

namespace SubathonManager.Data;

public static class StateValueHelper
{
    private static T TypeDefault<T>()
    {
        if (typeof(T) == typeof(string)) return (T)(object)"";
        return default(T)!;
    }

    private static T Parse<T>(string value, T fallback)
    {
        try { return (T)Convert.ChangeType(value, typeof(T)); }
        catch { return fallback; }
    }

    public static T Get<T>(AppDbContext db, string name, T? defaultValue = default)
    {
        T def = defaultValue is not null ? defaultValue : TypeDefault<T>();
        var row = db.StateValues.AsNoTracking().FirstOrDefault(sv => sv.Name == name);
        return row == null ? def : Parse<T>(row.Value, def);
    }

    public static async Task<T> GetAsync<T>(IDbContextFactory<AppDbContext> factory, string name, T? defaultValue = default)
    {
        await using var db = await factory.CreateDbContextAsync();
        return Get<T>(db, name, defaultValue);
    }

    public static async Task SetAsync<T>(AppDbContext db, string name, T value) where T : notnull
    {
        string strVal = value.ToString() ?? "";
        string typeName = typeof(T).Name;
        var existing = await db.StateValues.FindAsync(name);
        if (existing == null)
            db.StateValues.Add(new StateValue { Name = name, Value = strVal, TypeName = typeName });
        else
        {
            existing.Value = strVal;
            existing.TypeName = typeName;
        }
        await db.SaveChangesAsync();
    }

    public static async Task SetAsync<T>(IDbContextFactory<AppDbContext> factory, string name, T value) where T : notnull
    {
        await using var db = await factory.CreateDbContextAsync();
        await SetAsync(db, name, value);
    }

    public static void Set<T>(AppDbContext db, string name, T value) where T : notnull
    {
        string strVal = value.ToString() ?? "";
        string typeName = typeof(T).Name;
        var existing = db.StateValues.Find(name);
        if (existing == null)
            db.StateValues.Add(new StateValue { Name = name, Value = strVal, TypeName = typeName });
        else
        {
            existing.Value = strVal;
            existing.TypeName = typeName;
        }
    }
}
