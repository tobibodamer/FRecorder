using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace FRecorder2;

public class Settings
{
    public string? SelectedInputDeviceId { get; set; }
    public string? SelectedOutputDeviceId { get; set; }

    public bool AlwaysUseDefaultInputDevice { get; set; } = true;
    public bool AlwaysUseDefaultOutputDevice { get; set; } = true;

    public bool AutoStartRecording { get; set; } = true;

    public uint RecordDurationInSeconds { get; set; } = 20;

    public float SystemSoundsVolume { get; set; } = 1;
    public float MicVolume { get; set; } = 1;
    public bool SeperateTracks { get; set; } = false;


    public string RecordingFolder { get; set; }
      = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "FRecorder");

    public string FileNameTemplate { get; set; } = "Sound_{Timestamp}";

    public Task SaveAsync()
    {
        try
        {
            string jsonString = JsonSerializer.Serialize(this);

            var directory = Path.GetDirectoryName(App.SettingsFileName);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return File.WriteAllTextAsync(App.SettingsFileName, jsonString, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save settings.");
        }

        return Task.CompletedTask;
    }

    public static async Task<Settings?> LoadFromFile()
    {
        try
        {
            if (!File.Exists(App.SettingsFileName))
            {
                return null;
            }

            var jsonString = await File.ReadAllTextAsync(App.SettingsFileName, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<Settings>(jsonString);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not load settings");
        }

        return null;
    }
}