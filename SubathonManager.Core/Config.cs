using System;
using System.IO;
using System.Reflection;
using IniParser;
using IniParser.Model;

namespace SubathonManager.Core
{
    public static class Config
    {        
        private static readonly string ConfigPath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!
            , "data/config.ini");
            // Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");

        private static readonly FileIniDataParser Parser = new();
        public static IniData Data { get; private set; } = new();

        public static string TwitchClientId { get; } = "jsykjc9k0yqkbqg4ttsfgnwwqmoxfh";

        public static void LoadOrCreateDefault()
        {
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
        }

        public static void Save()
        {
            Parser.WriteFile(ConfigPath, Data);
        }

        public static string GetDatabasePath()
        {
            if (Data["Database"]["Path"] == null)
            {
                return Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!
                    , "data/subathonmanager.db");
            }

            return Data["Database"]["Path"];
        }
    }
}
