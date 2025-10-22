using IniParser;
using IniParser.Model;

namespace SubathonManager.Core
{
    public static class Config
    {        
        private static readonly string ConfigPath = Path.GetFullPath(Path.Combine(string.Empty
            , "data/config.ini"));
        
        public static readonly string DataFolder = Path.GetFullPath(Path.Combine(string.Empty
            , "data"));

        private static readonly FileIniDataParser Parser = new();
        public static IniData Data { get; private set; } = new();

        public static string TwitchClientId { get; } = "jsykjc9k0yqkbqg4ttsfgnwwqmoxfh";

        public static void LoadOrCreateDefault()
        {
            string folder = Path.GetFullPath(Path.Combine(string.Empty, 
                "data"));
            Directory.CreateDirectory(folder);
            
            // TODO impl logger
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

        private static void CreateDefault()
        {
            Data = new IniData();
            Data["Server"]["Port"] = "14040";
            Data["Database"]["Path"] = GetDatabasePath();
            Data["StreamElements"]["JWT"] = "";

            Data["Discord"]["Events.WebhookUrl"] = "";
            Data["Discord"]["WebhookUrl"] = "";
            
            foreach (Core.Enums.SubathonEventType type in Enum.GetValues(typeof(Core.Enums.SubathonEventType)))
            {
                Data["Discord"][$"Events.Log.{type}"] = $"{false}";
            }
            Data["Discord"]["Events.Log.Simulated"] = $"{false}";
        }

        public static void Save()
        {
            Parser.WriteFile(ConfigPath, Data);
        }

        public static string GetDatabasePath()
        {
            if (Data["Database"]["Path"] == null)
            {
                string folder = Path.GetFullPath(Path.Combine(string.Empty, 
                    "data"));
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "subathonmanager.db");
            }

            return Data["Database"]["Path"];
        }
    }
}
