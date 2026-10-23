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

    /// <summary>CW pitch in Hz. Radios show the signal frequency in CW and play it at this tone (IC-7300 default 600).</summary>
    public int CwPitch { get; set; } = 600;

    // receiver side DSP (KiwiSDR): noise blanker, noise reduction, autonotch
    public bool NoiseBlanker { get; set; }
    public bool NoiseReduction { get; set; }
    public bool AutoNotch { get; set; }

    // receiver
    public double Volume { get; set; } = 0.7;
    public bool KiwiCompression { get; set; } = true;
    public string KiwiPassword { get; set; } = "";
    public int KiwiZoom { get; set; } = 7;
    public double LastHz { get; set; } = 7_100_000;
    public ReceiverEntry? LastReceiver { get; set; }
    public bool LastWasNative { get; set; } = true;
    /// <summary>Old favourite keys (before My receivers existed); migrated when seen in a list.</summary>
    public List<string> Favorites { get; set; } = new();

    /// <summary>The operator's saved receivers, kept in full so they open without going online.</summary>
    public List<ReceiverEntry> MyReceivers { get; set; } = new();

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

    public bool IsFavorite(ReceiverEntry e) => MyReceivers.Any(r => r.Key == e.Key);

    public void ToggleFavorite(ReceiverEntry e)
    {
        if (IsFavorite(e)) RemoveReceiver(e);
        else SaveReceiver(e);
    }

    /// <summary>Add or update a saved receiver. Keeps a name the operator gave it.</summary>
    public void SaveReceiver(ReceiverEntry e)
    {
        var copy = JsonSerializer.Deserialize<ReceiverEntry>(JsonSerializer.Serialize(e, Json), Json)!;
        int i = MyReceivers.FindIndex(r => r.Key == e.Key);
        if (i >= 0)
        {
            if (!string.IsNullOrWhiteSpace(MyReceivers[i].Name)) copy.Name = MyReceivers[i].Name;
            MyReceivers[i] = copy;
        }
        else MyReceivers.Add(copy);
        Favorites.Remove(e.Key);
        Save();
    }

    public void RemoveReceiver(ReceiverEntry e)
    {
        MyReceivers.RemoveAll(r => r.Key == e.Key);
        Favorites.Remove(e.Key);
        Save();
    }

    /// <summary>Move favourites from the old key-only list into My receivers when their details are known.</summary>
    public void MigrateFavorites(IEnumerable<ReceiverEntry> seen)
    {
        if (Favorites.Count == 0) return;
        foreach (var e in seen)
            if (Favorites.Contains(e.Key) && !IsFavorite(e)) SaveReceiver(e);
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
