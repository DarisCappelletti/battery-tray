using System.IO;
using System.Text.Json;
using BTDeviceBattery.Models;

namespace BTDeviceBattery.Services;

/// <summary>JSON settings at %APPDATA%\BTDeviceBattery\settings.json, atomic writes.</summary>
public sealed class SettingsService
{
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BTDeviceBattery");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public AppSettings Current { get; private set; } = new();

    public static SettingsService Load()
    {
        var service = new SettingsService();
        try
        {
            if (File.Exists(FilePath))
                service.Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings: start fresh rather than crash.
        }
        return service;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // Read-only folder or disk full: settings just won't persist.
        }
    }
}