using System;
using System.IO;
using System.Text.Json;

namespace MagicCursor;

public class ConfigData
{
    public string GeminiApiKey { get; set; } = string.Empty;
    public bool RunAtStartup { get; set; } = false;
}

public static class ConfigService
{
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagicCursor"
    );

    private static readonly string FilePath = Path.Combine(FolderPath, "config.json");

    public static ConfigData LoadConfig()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var config = JsonSerializer.Deserialize<ConfigData>(json);
                if (config != null)
                {
                    return config;
                }
            }
        }
        catch
        {
            // Fallback if file corrupted or inaccessible
        }

        return new ConfigData();
    }

    public static void SaveConfig(ConfigData config)
    {
        try
        {
            if (!Directory.Exists(FolderPath))
            {
                Directory.CreateDirectory(FolderPath);
            }

            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Fail silently
        }
    }
}
