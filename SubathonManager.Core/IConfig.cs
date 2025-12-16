namespace SubathonManager.Core;

public interface IConfig
{
    string GetDatabasePath();
    void Save();
    void LoadOrCreateDefault();
    
    bool MigrateConfig();
    
    IniParser.Model.KeyDataCollection? GetSection(string sectionName);

    string? Get(string section, string key, string? defaultValue = "");
    void Set(string section, string key, string value);
}