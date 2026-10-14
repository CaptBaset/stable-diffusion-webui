using System.IO;
using System.Text.Json;

namespace HamRxBridge;

public enum SyncMode { Off, RadioToSdr, TwoWay }

public sealed class AppSettings
{
    public int RigIndex { get; set; } = 1;
    public SyncMode Sync { get; set; } = SyncMode.RadioToSdr;
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public double Volume { get; set; } = 0.7;
    public string KiwiPassword { get; set; } = "";
    public bool MuteOnTx { get; set; } = true;

    static string PathFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HamRxBridge", "settings.json");

    public static AppSettings Current { get; private set; } = Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFile)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
