using System.Text.Json;

namespace BwgTopBar;

internal sealed class ApplicationSettings
{
    public string Veid { get; set; } = "";
    public int RefreshMinutes { get; set; } = 2;
    public bool UseSsh { get; set; }
    public string SshHost { get; set; } = "";
    public string SshUser { get; set; } = "root";
    public int SshPort { get; set; } = 22;
    public string SshKeyPath { get; set; } = "";
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool TopMost { get; set; } = true;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BwgTopBar");
    private static readonly string ConfigPath = Path.Combine(Folder, "settings.json");

    public static ApplicationSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(ConfigPath)) ?? new();
        }
        catch (Exception) { /* 受损配置恢复为默认值，避免启动失败。 */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, ConfigPath, true);
    }
}
