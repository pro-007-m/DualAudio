using System.Text.Json;

namespace DualAudio;

internal sealed class AppSettings
{
    public string? SourceId { get; set; }
    public string? FirstId { get; set; }
    public string? SecondId { get; set; }
    public int FirstDelay { get; set; } = 120;
    public int SecondDelay { get; set; }
    public int FirstVolume { get; set; } = 80;
    public int SecondVolume { get; set; } = 80;
    public bool SwitchWindowsOutput { get; set; } = true;
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualAudio");
    private static string Pathname => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Pathname)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Pathname + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(Pathname + ".tmp", Pathname, true);
    }
}
