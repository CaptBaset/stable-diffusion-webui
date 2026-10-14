using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace HamRxBridge.Sdr;

public enum SdrKind { Kiwi, WebSdr }

public sealed class ReceiverEntry
{
    public SdrKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8073;
    public bool UseTls { get; set; }
    public int Users { get; set; } = -1;
    public int MaxUsers { get; set; } = -1;
    public string Antenna { get; set; } = "";
    public string Coverage { get; set; } = "";
    public string Software { get; set; } = "";
    public double Snr { get; set; } = double.NaN;
    public double Lat { get; set; } = double.NaN;
    public double Lon { get; set; } = double.NaN;
    public double DistanceKm { get; set; } = double.NaN;
    public bool Offline { get; set; }

    public string UsersText => Users < 0 ? "" : MaxUsers > 0 ? $"{Users}/{MaxUsers}" : Users.ToString();
    public string DistanceText => double.IsNaN(DistanceKm) ? "" : $"{DistanceKm:F0} km";
    public string SnrText => double.IsNaN(Snr) ? "" : Snr.ToString("F0");
    public bool Full => Users >= 0 && MaxUsers > 0 && Users >= MaxUsers;

    public string BaseUrl
    {
        get
        {
            if (Kind == SdrKind.Kiwi) return $"{(UseTls ? "https" : "http")}://{Host}:{Port}";
            var u = new Uri(Url);
            return u.GetLeftPart(UriPartial.Path);
        }
    }

    public static ReceiverEntry FromKiwiAddress(string text)
    {
        text = text.Trim();
        if (!text.Contains("://")) text = "http://" + text;
        var u = new Uri(text);
        return new ReceiverEntry
        {
            Kind = SdrKind.Kiwi,
            Name = u.Host,
            Url = text,
            Host = u.Host,
            Port = u.IsDefaultPort && u.Scheme == "http" ? 8073 : u.Port,
            UseTls = u.Scheme == "https"
        };
    }

    public static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

public static class Directories
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    static Directories() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("HamRxBridge/0.1");

    public const string KiwiListUrl = "http://kiwisdr.com/.public/";
    public const string WebSdrListUrl = "http://websdr.org/";

    // ---------------- KiwiSDR ----------------
    public static async Task<List<ReceiverEntry>> LoadKiwiAsync(CancellationToken ct)
    {
        string text = await Http.GetStringAsync(KiwiListUrl, ct);
        return ParseKiwi(text);
    }

    /// <summary>Tolerant key=value record parser. Records end at a blank line or a repeated key.</summary>
    public static List<ReceiverEntry> ParseKiwi(string text)
    {
        var result = new List<ReceiverEntry>();
        var cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (cur.Count > 0)
            {
                var e = FromKiwiRecord(cur);
                if (e != null) result.Add(e);
            }
            cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }
            var m = Regex.Match(line, @"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$");
            if (!m.Success) continue;
            string k = m.Groups[1].Value, v = m.Groups[2].Value.Trim();
            if (cur.ContainsKey(k)) Flush();
            cur[k] = v;
        }
        Flush();
        return result;
    }

    static ReceiverEntry? FromKiwiRecord(Dictionary<string, string> r)
    {
        string Get(string k) => r.TryGetValue(k, out var v) ? v : "";
        string url = Get("url");
        if (url.Length == 0) return null;
        ReceiverEntry e;
        try { e = ReceiverEntry.FromKiwiAddress(url); } catch { return null; }

        e.Name = Get("name");
        if (e.Name.Length == 0) e.Name = e.Host;
        e.Location = Get("loc");
        e.Antenna = Get("antenna");
        e.Software = Get("sdr_hw");
        string status = Get("status"), offline = Get("offline");
        e.Offline = offline.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("offline", StringComparison.OrdinalIgnoreCase);
        if (int.TryParse(Get("users"), out var u)) e.Users = u;
        if (int.TryParse(Get("users_max"), out var um)) e.MaxUsers = um;

        string bands = Get("bands");
        var bm = Regex.Match(bands, @"(\d+)\s*-\s*(\d+)");
        e.Coverage = bm.Success
            ? $"{double.Parse(bm.Groups[1].Value, CultureInfo.InvariantCulture) / 1e6:0.#}-{double.Parse(bm.Groups[2].Value, CultureInfo.InvariantCulture) / 1e6:0.#} MHz"
            : bands;

        var sm = Regex.Match(Get("snr"), @"-?\d+(\.\d+)?");
        if (sm.Success) e.Snr = double.Parse(sm.Value, CultureInfo.InvariantCulture);

        var gm = Regex.Match(Get("gps"), @"(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)");
        if (gm.Success)
        {
            e.Lat = double.Parse(gm.Groups[1].Value, CultureInfo.InvariantCulture);
            e.Lon = double.Parse(gm.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        return e;
    }

    // ---------------- WebSDR.org ----------------
    public static async Task<List<ReceiverEntry>> LoadWebSdrAsync(CancellationToken ct)
    {
        var list = new List<ReceiverEntry>
        {
            new() { Kind = SdrKind.WebSdr, Name = "University of Twente, NL (the original WebSDR)",
                    Location = "Enschede, Netherlands", Url = "http://websdr.ewi.utwente.nl:8901/" }
        };
        string html = await Http.GetStringAsync(WebSdrListUrl, ct);
        list.AddRange(ParseWebSdr(html));
        return list.GroupBy(x => x.BaseUrl.TrimEnd('/').ToLowerInvariant()).Select(g => g.First()).ToList();
    }

    public static List<ReceiverEntry> ParseWebSdr(string html)
    {
        var res = new List<ReceiverEntry>();
        foreach (Match m in Regex.Matches(html, "<a\\s[^>]*href=\"(https?://[^\"]+)\"[^>]*>(.*?)</a>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string url = m.Groups[1].Value;
            string label = Regex.Replace(m.Groups[2].Value, "<[^>]+>", " ");
            label = System.Net.WebUtility.HtmlDecode(Regex.Replace(label, @"\s+", " ")).Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) continue;
            // WebSDR servers almost always run on an explicit port (8901, 8902, ...)
            if (u.IsDefaultPort || u.Host.Contains("websdr.org")) continue;
            res.Add(new ReceiverEntry
            {
                Kind = SdrKind.WebSdr,
                Name = label.Length > 0 ? label : u.Host,
                Location = u.Host,
                Url = url
            });
        }
        return res;
    }
}
