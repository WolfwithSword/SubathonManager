using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;

namespace SubathonManager.Data;

public static class StateValueHelper {
    private static T TypeDefault<T>() {
        if (typeof(T) == typeof(string)) return (T)(object)"";
        return default!;
    }

    private static T Parse<T>(string value, T fallback) {
        try {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch {
            return fallback;
        }
    }

    public static T Get<T>(AppDbContext db, string name, T? defaultValue = default) {
        T def = defaultValue is not null ? defaultValue : TypeDefault<T>();
        StateValue? row = db.StateValues.AsNoTracking().FirstOrDefault(sv => sv.Name == name);
        return row == null ? def : Parse(row.Value, def);
    }

    public static async Task<T> GetAsync<T>(IDbContextFactory<AppDbContext> factory, string name,
        T? defaultValue = default) {
        await using AppDbContext db = await factory.CreateDbContextAsync();
        return Get(db, name, defaultValue);
    }

    public static async Task SetAsync<T>(AppDbContext db, string name, T value) where T : notnull {
        string strVal = value.ToString() ?? "";
        string typeName = typeof(T).Name;
        StateValue? existing = await db.StateValues.FindAsync(name);
        if (existing == null) {
            db.StateValues.Add(new StateValue { Name = name, Value = strVal, TypeName = typeName });
        }
        else {
            existing.Value = strVal;
            existing.TypeName = typeName;
        }

        await db.SaveChangesAsync();
    }

    public static async Task SetAsync<T>(IDbContextFactory<AppDbContext> factory, string name, T value)
        where T : notnull {
        await using AppDbContext db = await factory.CreateDbContextAsync();
        await SetAsync(db, name, value);
    }

    public static async Task<int> AddIntAsync(IDbContextFactory<AppDbContext> factory, string name, int delta) {
        await using AppDbContext db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT OR IGNORE INTO StateValues (Name, Value, TypeName) VALUES ({name}, '0', 'Int32')");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE StateValues SET Value = CAST(MAX(0, CAST(Value AS INTEGER) + {delta}) AS TEXT), TypeName = 'Int32' WHERE Name = {name}");
        return Get(db, name, 0);
    }

    public static void Set<T>(AppDbContext db, string name, T value) where T : notnull {
        string strVal = value.ToString() ?? "";
        string typeName = typeof(T).Name;
        StateValue? existing = db.StateValues.Find(name);
        if (existing == null) {
            db.StateValues.Add(new StateValue { Name = name, Value = strVal, TypeName = typeName });
        }
        else {
            existing.Value = strVal;
            existing.TypeName = typeName;
        }
    }
}