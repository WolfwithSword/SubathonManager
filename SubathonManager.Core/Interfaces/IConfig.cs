namespace SubathonManager.Core.Interfaces;

public interface IConfig
{
    string GetDatabasePath();
    void Save();
    void LoadOrCreateDefault();
    
    bool MigrateConfig();
    
    IniParser.Model.KeyDataCollection? GetSection(string sectionName);

    string? Get(string section, string key, string? defaultValue = "");
    bool GetBool(string section, string key, bool defaultValue = false);
    string? GetFromEncoded(string section, string key, string? defaultValue = "");
    
    bool Set(string section, string key, string value); /** Returns true if successfully changed, false if it was already equal **/

    bool SetBool(string section, string key, bool? value);
    bool SetEncoded(string section, string key, string value);
}