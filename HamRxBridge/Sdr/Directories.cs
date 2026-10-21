using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public double Snr { get; set; } = double.NaN;
    public double Lat { get; set; } = double.NaN;
    public double Lon { get; set; } = double.NaN;
    public bool Offline { get; set; }

    [JsonIgnore] public double DistanceKm { get; set; } = double.NaN;
    [JsonIgnore] public bool Favorite { get; set; }
    [JsonIgnore] public string Star => Favorite ? "\u2605" : "\u2606";
    [JsonIgnore] public string UsersText => Users < 0 ? "" : MaxUsers > 0 ? $"{Users}/{MaxUsers}" : Users.ToString();
    [JsonIgnore] public string DistanceText => double.IsNaN(DistanceKm) ? "" : $"{DistanceKm:N0}";
    [JsonIgnore] public string SnrText => double.IsNaN(Snr) ? "" : Snr.ToString("F0");
    [JsonIgnore] public bool Full => Users >= 0 && MaxUsers > 0 && Users >= MaxUsers;
    [JsonIgnore] public string Key => Kind == SdrKind.Kiwi ? $"kiwi:{Host.ToLowerInvariant()}:{Port}" : "web:" + BaseUrl.TrimEnd('/').ToLowerInvariant();

    [JsonIgnore]
    public string BaseUrl
    {
        get
        {
            if (Kind == SdrKind.Kiwi) return $"{(UseTls ? "https" : "http")}://{Host}:{Port}/";
            try { return new Uri(Url).GetLeftPart(UriPartial.Path); } catch { return Url; }
        }
    }

    public static ReceiverEntry FromKiwiAddress(string text)
    {
        text = text.Trim();
        if (!text.Contains("://")) text = "http://" + text;
        var u = new Uri(text);
        bool explicitPort = Regex.IsMatch(text, @"://[^/]+:\d+");
        return new ReceiverEntry
        {
            Kind = SdrKind.Kiwi,
            Name = u.Host,
            Url = text,
            Host = u.Host,
            Port = explicitPort ? u.Port : (u.Scheme == "https" ? 443 : 8073),
            UseTls = u.Scheme == "https"
        };
    }

    public static ReceiverEntry FromWebSdrAddress(string text)
    {
        text = text.Trim();
        if (!text.Contains("://")) text = "http://" + text;
        var u = new Uri(text);
        return new ReceiverEntry { Kind = SdrKind.WebSdr, Name = u.Host, Location = u.Host, Url = text, Host = u.Host, Port = u.Port };
    }

    public static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

public sealed record DirectoryResult(List<ReceiverEntry> Items, string Source, bool FromCache);

public static class Directories
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) HamRxBridge/1.0");
        return h;
    }

    static readonly string[] KiwiSources =
    {
        "http://kiwisdr.com/.public/",
        "http://kiwisdr.com/public/",
        "http://rx.linkfanel.net/kiwisdr_com.js",
    };

    const string WebSdrSource = "http://websdr.org/";

    static string CachePath(SdrKind k) => Path.Combine(AppSettings.Folder, k == SdrKind.Kiwi ? "kiwi_list.json" : "websdr_list.json");
    static string RawPath(string name) => Path.Combine(AppSettings.Folder, "raw_" + name + ".txt");

    public static async Task<DirectoryResult> LoadAsync(SdrKind kind, Action<string> progress, CancellationToken ct)
    {
        var errors = new List<string>();
        if (kind == SdrKind.Kiwi)
        {
            var merged = new Dictionary<string, ReceiverEntry>();
            string used = "";
            foreach (var src in KiwiSources)
            {
                progress("Loading " + src);
                try
                {
                    string text = await Http.GetStringAsync(src, ct);
                    SaveRaw(new Uri(src).Host + new Uri(src).AbsolutePath.Replace('/', '_'), text);
                    var list = ParseKiwiAny(text);
                    Log.Write($"Kiwi list {src}: {text.Length} chars, {list.Count} receivers");
                    foreach (var e in list) merged.TryAdd(e.Key, e);
                    if (list.Count > 0) used = used.Length == 0 ? src : used + " + " + src;
                    if (merged.Count > 300) break;   // a good primary source is enough
                }
                catch (Exception ex)
                {
                    errors.Add($"{src}: {ex.Message}");
                    Log.Write($"Kiwi list {src} failed: {ex.Message}");
                }
            }
            if (merged.Count > 0)
            {
                var items = merged.Values.ToList();
                SaveCache(kind, items);
                return new DirectoryResult(items, used, false);
            }
        }
        else
        {
            progress("Loading " + WebSdrSource);
            try
            {
                string html = await Http.GetStringAsync(WebSdrSource, ct);
                SaveRaw("websdr_org", html);
                var items = ParseWebSdr(html);
                Log.Write($"WebSDR list: {html.Length} chars, {items.Count} receivers");
                if (items.Count > 0)
                {
                    SaveCache(kind, items);
                    return new DirectoryResult(items, WebSdrSource, false);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                Log.Write("WebSDR list failed: " + ex.Message);
            }
        }

        var cached = LoadCache(kind);
        if (cached.Count > 0) return new DirectoryResult(cached, "saved copy (offline)", true);
        if (kind == SdrKind.WebSdr)
            return new DirectoryResult(new List<ReceiverEntry>
            {
                new() { Kind = SdrKind.WebSdr, Name = "University of Twente WebSDR", Location = "Enschede, Netherlands", Url = "http://websdr.ewi.utwente.nl:8901/" }
            }, "built in", true);
        throw new InvalidOperationException(errors.Count > 0 ? string.Join("  ", errors) : "No receivers found in any list");
    }

    static void SaveRaw(string name, string text)
    {
        try { Directory.CreateDirectory(AppSettings.Folder); File.WriteAllText(RawPath(name), text); } catch { }
    }

    static void SaveCache(SdrKind k, List<ReceiverEntry> items)
    {
        try { Directory.CreateDirectory(AppSettings.Folder); File.WriteAllText(CachePath(k), JsonSerializer.Serialize(items, JsonOpts)); } catch { }
    }

    static List<ReceiverEntry> LoadCache(SdrKind k)
    {
        try
        {
            if (File.Exists(CachePath(k)))
                return JsonSerializer.Deserialize<List<ReceiverEntry>>(File.ReadAllText(CachePath(k)), JsonOpts) ?? new();
        }
        catch { }
        return new();
    }

    static readonly JsonSerializerOptions JsonOpts = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    // ---------------------------------------------------------------- Kiwi parsing

    /// <summary>Handles the three formats seen in the wild: HTML with key=value comments, JS/JSON array, plain key=value lines.</summary>
    public static List<ReceiverEntry> ParseKiwiAny(string text)
    {
        List<ReceiverEntry> res;
        if (CommentKv.Matches(text).Count >= 3)
        {
            res = ParseKiwiHtmlComments(text);
            if (res.Count > 0) return res;
        }
        res = ParseKiwiJsObjects(text);
        if (res.Count > 0) return res;
        return ParseKiwiLines(text);
    }

    static readonly Regex JsObj = new(@"\{[^{}]*\}", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex JsKv = new(
        @"[""']?([A-Za-z_][A-Za-z0-9_]*)[""']?\s*:\s*(?:""((?:\\.|[^""\\])*)""|'((?:\\.|[^'\\])*)'|([^,}\s]+))",
        RegexOptions.Compiled);

    /// <summary>Array of JavaScript or JSON objects, e.g. var kiwisdr_com = [ { "name": "...", "url": "..." }, ];</summary>
    static List<ReceiverEntry> ParseKiwiJsObjects(string text)
    {
        var res = new List<ReceiverEntry>();
        foreach (Match om in JsObj.Matches(text))
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match kv in JsKv.Matches(om.Value))
            {
                string v = kv.Groups[2].Success ? kv.Groups[2].Value : kv.Groups[3].Success ? kv.Groups[3].Value : kv.Groups[4].Value;
                try { v = Regex.Unescape(v); } catch { }
                d.TryAdd(kv.Groups[1].Value, v);
            }
            if (!d.ContainsKey("url")) continue;
            var e = FromKiwiRecord(d, null);
            if (e != null) res.Add(e);
        }
        return res;
    }

    static readonly Regex CommentKv = new(@"<!--\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*-->", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex Href = new(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string? FirstReceiverHref(string text)
    {
        foreach (Match m in Href.Matches(text))
        {
            string h = m.Groups[1].Value;
            if (h.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !h.Contains("kiwisdr.com/public") && !h.Contains("kiwisdr.com/info") && !h.Contains("forum.kiwisdr"))
                return h;
        }
        return null;
    }

    static List<ReceiverEntry> ParseKiwiHtmlComments(string html)
    {
        var res = new List<ReceiverEntry>();

        // One chunk per receiver: prefer explicit entry containers, else cut before each status comment.
        bool byDiv = true;
        var cuts = Regex.Matches(html, @"<div[^>]*class\s*=\s*[""']?cl-entry", RegexOptions.IgnoreCase).Select(m => m.Index).ToList();
        if (cuts.Count < 2)
        {
            byDiv = false;
            cuts = Regex.Matches(html, @"<!--\s*status\s*=").Select(m => m.Index).ToList();
        }
        if (cuts.Count == 0) cuts.Add(0);
        cuts.Add(html.Length);

        int prevCommentEnd = 0;
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            string chunk = html.Substring(cuts[i], cuts[i + 1] - cuts[i]);
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int lastEnd = -1;
            foreach (Match m in CommentKv.Matches(chunk))
            {
                d.TryAdd(m.Groups[1].Value, System.Net.WebUtility.HtmlDecode(m.Groups[2].Value));
                lastEnd = m.Index + m.Length;
            }
            if (d.Count == 0) continue;

            // In div mode the link is inside the chunk. When cutting at status comments, a receiver's
            // own link sits just before its first comment, and the chunk tail holds the NEXT receiver's link.
            string? href = byDiv ? FirstReceiverHref(chunk) : null;
            if (!byDiv)
            {
                if (cuts[i] > prevCommentEnd)
                    href = FirstReceiverHref(html.Substring(prevCommentEnd, cuts[i] - prevCommentEnd));
                if (href == null && lastEnd > 0)
                    href = FirstReceiverHref(chunk[..lastEnd]);
            }
            if (lastEnd >= 0) prevCommentEnd = cuts[i] + lastEnd;

            var e = FromKiwiRecord(d, href);
            if (e != null) res.Add(e);
        }
        return res;
    }

    static List<ReceiverEntry> ParseKiwiLines(string text)
    {
        var result = new List<ReceiverEntry>();
        var cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Flush()
        {
            if (cur.Count > 0) { var e = FromKiwiRecord(cur, null); if (e != null) result.Add(e); }
            cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }
            var m = Regex.Match(line, @"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$");
            if (!m.Success) continue;
            if (cur.ContainsKey(m.Groups[1].Value)) Flush();
            cur[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        }
        Flush();
        return result;
    }

    static ReceiverEntry? FromKiwiRecord(Dictionary<string, string> r, string? href)
    {
        string Get(string k) => r.TryGetValue(k, out var v) ? v.Trim() : "";
        string url = Get("url");
        if (url.Length == 0) url = href ?? "";
        if (url.Length == 0) return null;
        ReceiverEntry e;
        try { e = ReceiverEntry.FromKiwiAddress(url); } catch { return null; }

        e.Name = Clean(Get("name"));
        if (e.Name.Length == 0) e.Name = e.Host;
        e.Location = Clean(Get("loc"));
        e.Antenna = Clean(Get("antenna"));
        string status = Get("status"), offline = Get("offline");
        e.Offline = offline.Equals("yes", StringComparison.OrdinalIgnoreCase) || offline == "true" ||
                    status.Equals("offline", StringComparison.OrdinalIgnoreCase);
        if (int.TryParse(Get("users"), out var u)) e.Users = u;
        if (int.TryParse(Get("users_max"), out var um)) e.MaxUsers = um;

        var bm = Regex.Match(Get("bands"), @"(\d+(?:\.\d+)?)\s*-\s*(\d+(?:\.\d+)?)");
        if (bm.Success)
        {
            double lo = double.Parse(bm.Groups[1].Value, CultureInfo.InvariantCulture);
            double hi = double.Parse(bm.Groups[2].Value, CultureInfo.InvariantCulture);
            e.Coverage = $"{lo / 1e6:0.#}-{hi / 1e6:0.#} MHz";
        }

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

    static string Clean(string s) => Regex.Replace(System.Net.WebUtility.HtmlDecode(s), @"\s+", " ").Trim();

    // ---------------------------------------------------------------- WebSDR parsing

    public static List<ReceiverEntry> ParseWebSdr(string html)
    {
        var res = new List<ReceiverEntry>();
        var seen = new HashSet<string>();
        foreach (Match m in Regex.Matches(html, "<a\\s[^>]*href\\s*=\\s*[\"']([^\"']+)[\"'][^>]*>(.*?)</a>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string url = m.Groups[1].Value.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) continue;
            if (u.Host.EndsWith("websdr.org", StringComparison.OrdinalIgnoreCase)) continue;
            bool looksLikeSdr = !u.IsDefaultPort || u.Host.Contains("sdr", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeSdr) continue;
            string label = Clean(Regex.Replace(m.Groups[2].Value, "<[^>]+>", " "));
            var e = new ReceiverEntry { Kind = SdrKind.WebSdr, Name = label.Length > 2 ? label : u.Host, Location = u.Host, Url = url, Host = u.Host, Port = u.Port };
            if (seen.Add(e.Key)) res.Add(e);
        }
        return res;
    }
}
