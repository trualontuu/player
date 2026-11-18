using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Player.Core;

namespace Player.Yandex;

/// <summary>
/// SoundCloud через api-v2 — тот же API, что у их сайта. Ключ (client_id) берётся из settings.json, а когда SoundCloud
/// его меняет (401/403) — заново достаётся из скриптов soundcloud.com. Треки с DRM (encrypted-hls) не играем:
/// они помечаются недоступными. Открытые идут как HLS (AAC 160k) — его играет системный MediaPlayer.
/// Id всех объектов SoundCloud начинаются с "sc:", чтобы не пересекаться с Яндексом.
/// </summary>
public static class Sc
{
    const string Api = "https://api-v2.soundcloud.com";
    const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }) { Timeout = TimeSpan.FromSeconds(15) };

    static PlayerSettings? _s;
    static readonly SemaphoreSlim RefreshGate = new(1, 1);

    static Sc() => Http.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);

    public static void Init(PlayerSettings s) => _s = s;

    static string ClientId => _s?.ScClientId ?? "";

    // ---------------------------------------------------------------- ключ
    /// <summary>Достаёт свежий client_id со страницы soundcloud.com (он лежит в одном из последних скриптов).</summary>
    static async Task RefreshClientId(string stale)
    {
        await RefreshGate.WaitAsync();
        try
        {
            if (ClientId != stale && ClientId.Length > 0) return; // пока ждали, ключ уже обновили
            var html = await Http.GetStringAsync("https://soundcloud.com/");
            var scripts = Regex.Matches(html, @"https://a-v2\.sndcdn\.com/assets/[^""]+\.js").Select(m => m.Value).Reverse();
            foreach (var js in scripts)
            {
                var m = Regex.Match(await Http.GetStringAsync(js), @"client_id\s*:\s*""([A-Za-z0-9]{32})""");
                if (!m.Success) continue;
                if (_s != null) { _s.ScClientId = m.Groups[1].Value; _s.SaveSoon(); }
                return;
            }
            throw new Exception("не нашёл ключ SoundCloud");
        }
        finally { RefreshGate.Release(); }
    }

    static async Task<JsonElement> Get(string pathAndQuery, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            var id = ClientId;
            if (id.Length == 0) { await RefreshClientId(id); id = ClientId; }
            var url = (pathAndQuery.StartsWith("http") ? pathAndQuery : Api + pathAndQuery);
            url += (url.Contains('?') ? "&" : "?") + "client_id=" + id;
            using var r = await Http.GetAsync(url, ct);
            if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 0)
            {
                await RefreshClientId(id);
                continue;
            }
            if (!r.IsSuccessStatusCode) throw new Exception($"SoundCloud: {(int)r.StatusCode}");
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStreamAsync(ct));
            return doc.RootElement.Clone();
        }
    }

    // ---------------------------------------------------------------- разбор
    static string S(JsonElement e, string n) => Track.S(e, n);

    /// <summary>Обложка SoundCloud: "...-large.jpg" → "...-%%.jpg", размер подставит CoverUrl.</summary>
    static string Art(string url) => url.Length == 0 ? "" : url.Replace("-large.", "-%%.");

    static Artist ParseUser(JsonElement u) => new()
    {
        Id = "sc:" + S(u, "id"),
        Name = S(u, "username"),
        CoverUri = Art(S(u, "avatar_url")),
        Genre = "SoundCloud",
        TrackCount = u.TryGetProperty("track_count", out var tc) && tc.TryGetInt32(out int n) ? n : 0,
    };

    static Album ParsePlaylist(JsonElement p)
    {
        p.TryGetProperty("user", out var u);
        var art = S(p, "artwork_url");
        if (art.Length == 0 && p.TryGetProperty("tracks", out var tr) && tr.ValueKind == JsonValueKind.Array && tr.GetArrayLength() > 0) art = S(tr[0], "artwork_url");
        if (art.Length == 0) art = S(u, "avatar_url");
        var type = S(p, "set_type") is { Length: > 0 } st ? st : (p.TryGetProperty("is_album", out var ia) && ia.ValueKind == JsonValueKind.True ? "album" : "playlist");
        var date = S(p, "release_date") is { Length: >= 4 } rd ? rd : S(p, "created_at");
        return new Album
        {
            Id = "sc:" + S(p, "id"), Title = S(p, "title"), Year = date.Length >= 4 ? date[..4] : "",
            CoverUri = Art(art), Type = type is "album" or "ep" or "single" or "compilation" ? type : "playlist",
            ArtistRefs = new() { ("sc:" + S(u, "id"), S(u, "username")) }, Artists = S(u, "username"),
            TrackCount = p.TryGetProperty("track_count", out var tc) && tc.TryGetInt32(out int n) ? n : 0,
        };
    }

    static List<T> Coll<T>(JsonElement res, Func<JsonElement, T?> parse) where T : class
    {
        var list = new List<T>();
        var arr = res.ValueKind == JsonValueKind.Array ? res : res.TryGetProperty("collection", out var c) ? c : default;
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray()) if (parse(e) is { } x) list.Add(x);
        return list;
    }

    static List<Track> Tracks(JsonElement res) => Coll(res, Track.FromSc);

    // ---------------------------------------------------------------- запросы
    public static async Task<Ym.SearchResult> SearchAll(string q, CancellationToken ct = default)
    {
        var e = Uri.EscapeDataString(q);
        var tracks = Get($"/search/tracks?q={e}&limit=30", ct);
        var users = Get($"/search/users?q={e}&limit=12", ct);
        var albums = Get($"/search/albums?q={e}&limit=12", ct);
        var lists = Get($"/search/playlists_without_albums?q={e}&limit=12", ct);
        var t = Tracks(await tracks);
        // сначала играбельные — треки с DRM всё равно серые
        t = t.Where(x => x.Available).Concat(t.Where(x => !x.Available)).ToList();
        var u = Coll(await users, x => (Artist?)ParseUser(x));
        var al = Coll(await albums, x => (Album?)ParsePlaylist(x));
        var pl = Coll(await lists, x => (Album?)ParsePlaylist(x));
        // лучший результат: если первый найденный пользователь называется ровно как запрос — артист
        var best = u.Count > 0 && string.Equals(u[0].Name.Trim(), q.Trim(), StringComparison.OrdinalIgnoreCase) ? "artist" : "track";
        return new Ym.SearchResult(t, u, al.Concat(pl).ToList(), new List<Playlist>(), best);
    }

    public static async Task<Ym.ArtistInfo> ArtistPage(string scId)
    {
        var id = scId[3..];
        var user = Get($"/users/{id}");
        var top = Get($"/users/{id}/toptracks?limit=30");
        var albums = Get($"/users/{id}/albums?limit=30");
        var lists = Get($"/users/{id}/playlists_without_albums?limit=30");
        Task<JsonElement>? related = Get($"/users/{id}/relatedartists?limit=12");
        var artist = ParseUser(await user);
        var popular = Tracks(await top);
        popular = popular.Where(x => x.Available).Concat(popular.Where(x => !x.Available)).ToList();
        List<Album> Distinct(List<Album> a) => a.GroupBy(x => x.Id).Select(g => g.First()).ToList();
        List<Album> al = new(), pl = new();
        try { al = Distinct(Coll(await albums, x => (Album?)ParsePlaylist(x))); } catch { }
        try { pl = Distinct(Coll(await lists, x => (Album?)ParsePlaylist(x))); } catch { }
        List<Artist> sim = new();
        try { sim = Coll(await related, x => (Artist?)ParseUser(x)); } catch { }
        return new Ym.ArtistInfo(artist, popular, al, pl, sim);
    }

    /// <summary>Плейлист/альбом: у длинных SoundCloud присылает подробно только первые треки, остальные — одни id, догружаем пачками.</summary>
    public static async Task<(Album, List<Track>)> PlaylistPage(string scId)
    {
        var p = await Get($"/playlists/{scId[3..]}");
        var album = ParsePlaylist(p);
        var full = new Dictionary<string, Track>();
        var order = new List<string>();
        var missing = new List<string>();
        if (p.TryGetProperty("tracks", out var tr))
            foreach (var e in tr.EnumerateArray())
            {
                var id = S(e, "id");
                order.Add(id);
                if (e.TryGetProperty("title", out _) && Track.FromSc(e) is { } t) full[id] = t;
                else missing.Add(id);
            }
        for (int i = 0; i < missing.Count; i += 50)
            foreach (var t in Tracks(await Get("/tracks?ids=" + string.Join("%2C", missing.Skip(i).Take(50)))))
                full[t.Id[3..]] = t;
        return (album, order.Where(full.ContainsKey).Select(id => full[id]).ToList());
    }

    /// <summary>«Станция»: похожие треки — аналог волны для SoundCloud.</summary>
    public static async Task<List<Track>> Related(Track t)
    {
        var list = Tracks(await Get($"/tracks/{t.Id[3..]}/related?limit=50"));
        return list.Where(x => x.Available).ToList();
    }

    /// <summary>Ссылка на поток: открытый HLS (AAC, иначе MP3) → у transcoding спрашиваем подписанный адрес плейлиста.</summary>
    public static async Task<string> StreamUrl(Track t)
    {
        if (t.ScStream.Length == 0) throw new Exception("трек защищён (DRM) — SoundCloud не отдаёт его сторонним плеерам");
        var res = await Get($"{t.ScStream}?track_authorization={Uri.EscapeDataString(t.ScAuth)}");
        return S(res, "url") is { Length: > 0 } u ? u : throw new Exception("SoundCloud не дал ссылку");
    }
}
