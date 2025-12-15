using IniParser;
using IniParser.Model;
using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core
{
    [ExcludeFromCodeCoverage]
    public class Config : IConfig
    {        
        private static readonly string ConfigPath = Path.GetFullPath(Path.Combine(string.Empty
            , "data/config.ini"));
        
        public static readonly string DataFolder = Path.GetFullPath(Path.Combine(string.Empty
            , "data"));

        public static readonly string AppFolder = Path.GetFullPath(".");

        private static readonly FileIniDataParser Parser = new();
        private static IniData Data { get; set; } = new();

        public static string TwitchClientId { get; } = "jsykjc9k0yqkbqg4ttsfgnwwqmoxfh";

        public virtual IniParser.Model.KeyDataCollection GetSection(string section)
        {
            return Data[section];
        }
        
        public virtual void LoadOrCreateDefault()
        {
            string folder = Path.GetFullPath(Path.Combine(string.Empty, 
                "data"));
            Directory.CreateDirectory(folder);
            
            Console.WriteLine($"[Config] Checking config at {ConfigPath}");
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(ConfigPath))
            {
                Console.WriteLine("[Config] Creating new config.ini...");
                CreateDefault();
                Save();
            }
            else
            {
                try
                {
                    Data = Parser.ReadFile(ConfigPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Config] Failed to read config.ini: {ex.Message}");
                    Console.WriteLine("[Config] Recreating config with defaults.");
                    CreateDefault();
                    Save();
                }
            }
        }

        private void CreateDefault()
        {
            Data = new IniData();
            Data["Server"]["Port"] = "14040";
            Data["StreamElements"]["JWT"] = "";

            Data["Discord"]["Events.WebhookUrl"] = "";
            Data["Discord"]["WebhookUrl"] = "";
            
            foreach (Core.Enums.SubathonEventType type in Enum.GetValues(typeof(Core.Enums.SubathonEventType)))
            {
                Data["Discord"][$"Events.Log.{type}"] = $"{false}";
            }
            Data["Discord"]["Events.Log.Simulated"] = $"{false}";
        }

        public virtual void Save()
        {
            Parser.WriteFile(ConfigPath, Data);
        }

        public virtual string GetDatabasePath()
        {
            return GetDatabasePathStatic();
        }

        public static string GetDatabasePathStatic()
        {
            string folder = Path.GetFullPath(Path.Combine(string.Empty,
                "data"));
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "subathonmanager.db");
        }

        public virtual string? Get(string section, string key, string? defaultValue = "")
        {
            return Data[section][key] ?? defaultValue;
        }

        public virtual void Set(string section, string key, string? value)
        {
            Data[section][key] = value ?? string.Empty;
        }
    }
}
