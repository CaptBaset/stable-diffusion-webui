using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using HamRxBridge.Sdr;

namespace HamRxBridge;

public enum SyncMode { Off, RadioToSdr, TwoWay }
public enum CatSource { OmniRig, IcomCiv, KenwoodYaesu, None }

public sealed class AppSettings
{
    // operator
    public string Callsign { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lon { get; set; }

    // radio control
    public CatSource Cat { get; set; } = CatSource.OmniRig;
    public int RigIndex { get; set; } = 1;
    public string CatPort { get; set; } = "";
    public int CatBaud { get; set; } = 19200;
    public string CivAddress { get; set; } = "94";
    public bool KenwoodRtsCts { get; set; } = true;
    public SyncMode Sync { get; set; } = SyncMode.RadioToSdr;
    public bool MuteOnTx { get; set; } = true;

    // receiver
    public double Volume { get; set; } = 0.7;
    public bool KiwiCompression { get; set; } = true;
    public string KiwiPassword { get; set; } = "";
    public int KiwiZoom { get; set; } = 7;
    public double LastHz { get; set; } = 7_100_000;
    public ReceiverEntry? LastReceiver { get; set; }
    public bool LastWasNative { get; set; } = true;
    public List<string> Favorites { get; set; } = new();

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HamRxBridge");
    static string FilePath => Path.Combine(Folder, "settings.json");

    // Receiver entries carry NaN for unknown SNR or position, which plain JSON cannot hold.
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static AppSettings Current { get; } = Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
    }

    public bool IsFavorite(ReceiverEntry e) => Favorites.Contains(e.Key);

    public void ToggleFavorite(ReceiverEntry e)
    {
        if (!Favorites.Remove(e.Key)) Favorites.Add(e.Key);
        Save();
    }
}

public static class Log
{
    static readonly object Gate = new();

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Folder);
                string path = Path.Combine(AppSettings.Folder, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
