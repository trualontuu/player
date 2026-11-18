using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Player.Core;

namespace Player.Yandex;

/// <summary>
/// Яндекс Музыка через неофициальное API (как приложения для ТВ, библиотека MarshalX/yandex-music-api).
/// Вход общий с Island: если там уже вошли, токен берётся оттуда. Токен хранится только на этом компьютере (DPAPI).
/// </summary>
public static class Ym
{
    const string ClientId = "23cabbbdc6cd418abb4b39c32c41195d";
    const string ClientSecret = "53bc75238f0c4d08a118e51fe9203300";
    const string SignSalt = "XGRlBW9FXlekgbPrRHuSiA";
    const string Api = "https://api.music.yandex.net";
    const string OAuth = "https://oauth.yandex.ru";

    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    }) { Timeout = TimeSpan.FromSeconds(15) };

    static string? _token;
    public static string Uid { get; private set; } = "";
    public static string Login { get; private set; } = "";
    public static bool LoggedIn => _token != null;
    /// <summary>Не отправлять «Моей волне» отзывы о прослушивании (режим тестов).</summary>
    public static bool NoFeedback;

    static string OwnFile => Path.Combine(PlayerSettings.Dir, "yandex.bin");
    static string IslandFile => Path.Combine(Island.Core.Settings.Dir, "yandex.bin");

    // ---------------------------------------------------------------- токен
    public static void LoadToken()
    {
        foreach (var f in new[] { OwnFile, IslandFile })
        {
            try
            {
                if (!File.Exists(f)) continue;
                var parts = Encoding.UTF8.GetString(Dpapi(File.ReadAllBytes(f), false)).Split('\n');
                if (parts[0].Length == 0) continue;
                _token = parts[0];
                Uid = parts.Length > 1 ? parts[1] : "";
                Login = parts.Length > 2 ? parts[2] : "";
                return;
            }
            catch { }
        }
    }

    static void SaveToken()
    {
        Directory.CreateDirectory(PlayerSettings.Dir);
        File.WriteAllBytes(OwnFile, Dpapi(Encoding.UTF8.GetBytes($"{_token}\n{Uid}\n{Login}"), true));
    }

    public static void Logout()
    {
        _token = null; Uid = ""; Login = "";
        try { File.Delete(OwnFile); } catch { }
    }

    public sealed record DeviceCode(string UserCode, string Url, string Code, int Interval, int ExpiresIn);

    public static async Task<DeviceCode> StartLogin()
    {
        var id = new string(Enumerable.Range(0, 10).Select(_ => "abcdefghijklmnopqrstuvwxyz0123456789"[Random.Shared.Next(36)]).ToArray());
        using var r = await Http.PostAsync(OAuth + "/device/code", Form(("client_id", ClientId), ("device_id", id), ("device_name", "Player (" + Environment.MachineName + ")")));
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var e = d.RootElement;
        return new DeviceCode(Track.S(e, "user_code"), Track.S(e, "verification_url") is { Length: > 0 } u ? u : "https://ya.ru/device", Track.S(e, "device_code"),
            e.TryGetProperty("interval", out var i) ? i.GetInt32() : 5, e.TryGetProperty("expires_in", out var x) ? x.GetInt32() : 300);
    }

    public static async Task<bool> WaitLogin(DeviceCode dc, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(dc.ExpiresIn);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, dc.Interval)), ct);
            string body;
            try
            {
                using var r = await Http.PostAsync(OAuth + "/token", Form(("grant_type", "device_code"), ("code", dc.Code), ("client_id", ClientId), ("client_secret", ClientSecret)), ct);
                body = await r.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("access_token", out var t))
            {
                _token = t.GetString();
                await LoadAccount();
                SaveToken();
                return true;
            }
            var err = Track.S(d.RootElement, "error");
            if (err != "authorization_pending" && err != "slow_down") return false;
        }
        return false;
    }

    public static async Task LoadAccount()
    {
        var res = await Get("/account/status");
        var acc = res.GetProperty("account");
        Uid = acc.GetProperty("uid").ToString();
        Login = Track.S(acc, "login") is { Length: > 0 } l ? l : Track.S(acc, "displayName");
    }

    // ---------------------------------------------------------------- библиотека
    public static async Task<List<Track>> Likes()
    {
        var res = await Get($"/users/{Uid}/likes/tracks");
        var ids = res.GetProperty("library").GetProperty("tracks").EnumerateArray()
            .Select(t => (Track.S(t, "id"), Track.S(t, "albumId"))).ToList();
        lock (Track.LikedIds) { Track.LikedIds.Clear(); foreach (var (id, _) in ids) Track.LikedIds.Add(id); }
        return await Tracks(ids.Select(p => p.Item2.Length > 0 ? $"{p.Item1}:{p.Item2}" : p.Item1).ToList());
    }

    /// <summary>Полные данные треков пачками (порядок сохраняется).</summary>
    public static async Task<List<Track>> Tracks(IList<string> keys)
    {
        var map = new Dictionary<string, Track>();
        for (int i = 0; i < keys.Count; i += 250)
        {
            var part = keys.Skip(i).Take(250).ToList();
            var res = await Post("/tracks", ("track-ids", string.Join(",", part)), ("with-positions", "false"));
            foreach (var e in res.EnumerateArray())
                if (Track.From(e) is { } t) map[t.Id] = t;
        }
        var list = new List<Track>(keys.Count);
        foreach (var k in keys)
            if (map.TryGetValue(k.Split(':')[0], out var t)) list.Add(t);
        return list;
    }

    public static async Task<List<Playlist>> Playlists()
    {
        var res = await Get($"/users/{Uid}/playlists/list");
        return res.EnumerateArray().Select(ParsePlaylist).ToList();
    }

    static Playlist ParsePlaylist(JsonElement p)
    {
        var cover = p.TryGetProperty("cover", out var c) ? (Track.S(c, "uri") is { Length: > 0 } u ? u : c.TryGetProperty("itemsUri", out var iu) && iu.ValueKind == JsonValueKind.Array && iu.GetArrayLength() > 0 ? iu[0].GetString() ?? "" : "") : "";
        if (cover.Length == 0) cover = Track.S(p, "ogImage");
        p.TryGetProperty("owner", out var o);
        var ownerUid = Track.S(o, "uid");
        return new Playlist
        {
            Kind = Track.S(p, "kind"), Title = Track.S(p, "title"),
            Owner = ownerUid.Length > 0 ? ownerUid : Uid,
            OwnerName = ownerUid.Length == 0 || ownerUid == Uid ? "" : Track.S(o, "name"),
            TrackCount = p.TryGetProperty("trackCount", out var tc) && tc.TryGetInt32(out int n) ? n : 0,
            CoverUri = cover,
        };
    }

    static Artist ParseArtist(JsonElement a) => new()
    {
        Id = Track.S(a, "id"), Name = Track.S(a, "name"),
        CoverUri = a.TryGetProperty("cover", out var c) && Track.S(c, "uri") is { Length: > 0 } u ? u : Track.S(a, "ogImage"),
        Genre = a.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0 ? g[0].ToString() : "",
        TrackCount = a.TryGetProperty("counts", out var cn) && cn.ValueKind == JsonValueKind.Object && cn.TryGetProperty("tracks", out var tr) && tr.TryGetInt32(out int n) ? n : 0,
    };

    static Album ParseAlbum(JsonElement a)
    {
        var refs = a.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array
            ? ar.EnumerateArray().Select(x => (Track.S(x, "id"), Track.S(x, "name"))).Where(x => x.Item2.Length > 0).ToList() : new();
        return new Album
        {
            Id = Track.S(a, "id"), Title = Track.S(a, "title"), Year = Track.S(a, "year"),
            CoverUri = Track.S(a, "coverUri") is { Length: > 0 } c ? c : Track.S(a, "ogImage"),
            Type = Track.S(a, "type"), ArtistRefs = refs, Artists = string.Join(", ", refs.Select(x => x.Item2)),
            TrackCount = a.TryGetProperty("trackCount", out var tc) && tc.TryGetInt32(out int n) ? n : 0,
        };
    }

    static List<T> Results<T>(JsonElement res, string key, Func<JsonElement, T?> parse) where T : class
    {
        var list = new List<T>();
        if (res.TryGetProperty(key, out var sec) && sec.ValueKind == JsonValueKind.Object && sec.TryGetProperty("results", out var r))
            foreach (var e in r.EnumerateArray()) if (parse(e) is { } x) list.Add(x);
        return list;
    }

    public sealed record SearchResult(List<Track> Tracks, List<Artist> Artists, List<Album> Albums, List<Playlist> Playlists, string BestType);

    /// <summary>Поиск сразу по всему: треки, артисты, альбомы, плейлисты — один запрос.</summary>
    public static async Task<SearchResult> SearchAll(string text, CancellationToken ct = default)
    {
        var res = await Get("/search?type=all&page=0&nocorrect=false&text=" + Uri.EscapeDataString(text), ct);
        return new SearchResult(
            Results(res, "tracks", Track.From),
            Results<Artist>(res, "artists", e => ParseArtist(e)),
            Results<Album>(res, "albums", e => ParseAlbum(e)),
            Results<Playlist>(res, "playlists", e => ParsePlaylist(e)),
            res.TryGetProperty("best", out var b) ? Track.S(b, "type") : "");
    }

    public sealed record ArtistInfo(Artist Artist, List<Track> Popular, List<Album> Albums, List<Album> Singles, List<Artist> Similar);

    public static async Task<ArtistInfo> ArtistPage(string id)
    {
        var briefTask = Get($"/artists/{id}/brief-info");
        var albumsTask = Get($"/artists/{id}/direct-albums?page=0&page-size=100&sort-by=year");
        var b = await briefTask;
        var artist = ParseArtist(b.GetProperty("artist"));
        var popular = b.TryGetProperty("popularTracks", out var pt) ? pt.EnumerateArray().Select(Track.From).OfType<Track>().ToList() : new();
        var similar = b.TryGetProperty("similarArtists", out var sa) ? sa.EnumerateArray().Select(ParseArtist).ToList() : new();
        List<Album> all;
        try { var a = await albumsTask; all = a.GetProperty("albums").EnumerateArray().Select(ParseAlbum).ToList(); }
        catch { all = b.TryGetProperty("albums", out var al) ? al.EnumerateArray().Select(ParseAlbum).ToList() : new(); }
        return new ArtistInfo(artist, popular, all.Where(x => x.Type != "single").ToList(), all.Where(x => x.Type == "single").ToList(), similar);
    }

    public static async Task<(Album, List<Track>)> AlbumPage(string id)
    {
        var r = await Get($"/albums/{id}/with-tracks");
        var tracks = new List<Track>();
        if (r.TryGetProperty("volumes", out var v))
            foreach (var vol in v.EnumerateArray())
                foreach (var e in vol.EnumerateArray()) if (Track.From(e) is { } t) tracks.Add(t);
        return (ParseAlbum(r), tracks);
    }

    public static async Task<List<Track>> PlaylistTracks(Playlist p)
    {
        var res = await Get($"/users/{p.Owner}/playlists/{p.Kind}");
        var list = new List<Track>();
        var missing = new List<(int pos, string key)>();
        if (res.TryGetProperty("tracks", out var arr))
            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("track", out var te) && Track.From(te) is { } t) list.Add(t);
                else if (Track.S(item, "id") is { Length: > 0 } id)
                {
                    var alb = Track.S(item, "albumId");
                    missing.Add((list.Count + missing.Count, alb.Length > 0 ? $"{id}:{alb}" : id));
                }
            }
        if (missing.Count > 0)
        {
            var got = await Tracks(missing.Select(m => m.key).ToList());
            list.AddRange(got);
        }
        return list;
    }

    public static async Task<List<Track>> Search(string text, CancellationToken ct = default)
    {
        var res = await Get("/search?type=track&page=0&nocorrect=false&text=" + Uri.EscapeDataString(text), ct);
        var list = new List<Track>();
        if (res.TryGetProperty("tracks", out var tr) && tr.ValueKind == JsonValueKind.Object && tr.TryGetProperty("results", out var r))
            foreach (var e in r.EnumerateArray())
                if (Track.From(e) is { } t) list.Add(t);
        return list;
    }

    public static async Task SetLike(Track t, bool like)
    {
        if (t.IsSc) throw new Exception("лайки SoundCloud пока не поддерживаются");
        await Post($"/users/{Uid}/likes/tracks/" + (like ? "add-multiple" : "remove"), ("track-ids", t.Key));
        lock (Track.LikedIds) { if (like) Track.LikedIds.Add(t.Id); else Track.LikedIds.Remove(t.Id); }
        t.Liked = like;
    }

    public static Task Dislike(Track t) => Post($"/users/{Uid}/dislikes/tracks/add-multiple", ("track-ids", t.Key));

    // ---------------------------------------------------------------- Моя волна
    public static async Task<(WaveSession, List<Track>)> WaveStart(string[] seeds)
    {
        var res = await PostJson("/rotor/session/new", new { seeds, includeTracksInResponse = true });
        var s = new WaveSession { Id = Track.S(res, "radioSessionId"), BatchId = Track.S(res, "batchId"), Seeds = seeds };
        return (s, Sequence(res));
    }

    public static async Task<List<Track>> WaveMore(WaveSession s, IEnumerable<Track> recent)
    {
        var res = await PostJson($"/rotor/session/{s.Id}/tracks", new { queue = recent.Select(t => t.Key).ToArray() });
        if (Track.S(res, "batchId") is { Length: > 0 } b) s.BatchId = b;
        return Sequence(res);
    }

    static List<Track> Sequence(JsonElement res)
    {
        var list = new List<Track>();
        if (res.TryGetProperty("sequence", out var seq))
            foreach (var item in seq.EnumerateArray())
                if (item.TryGetProperty("track", out var te) && Track.From(te) is { } t && t.Available) list.Add(t);
        return list;
    }

    /// <summary>trackStarted / trackFinished / skip / like / dislike — волна по ним подстраивается.</summary>
    public static async Task WaveFeedback(WaveSession s, string type, Track t, double playedSeconds = 0)
    {
        if (NoFeedback || s.Id.Length == 0) return;
        try
        {
            object ev = type is "trackStarted"
                ? new { type, timestamp = DateTime.UtcNow.ToString("O"), trackId = t.Key }
                : new { type, timestamp = DateTime.UtcNow.ToString("O"), trackId = t.Key, totalPlayedSeconds = Math.Round(playedSeconds, 1) };
            await PostJson($"/rotor/session/{s.Id}/feedback", new { @event = ev, batchId = s.BatchId });
        }
        catch { }
    }

    // ---------------------------------------------------------------- поток
    /// <summary>Прямая ссылка на mp3 (подписанная, живёт ограниченное время).</summary>
    public static async Task<string> StreamUrl(Track t, int maxKbps)
    {
        var res = await Get($"/tracks/{t.Key}/download-info");
        var variants = res.EnumerateArray().Select(v => (codec: Track.S(v, "codec"), kbps: v.GetProperty("bitrateInKbps").GetInt32(), url: Track.S(v, "downloadInfoUrl"))).ToList();
        var mp3 = variants.Where(v => v.codec == "mp3").ToList();
        if (mp3.Count == 0) mp3 = variants;
        var pick = mp3.Where(v => v.kbps <= maxKbps).OrderByDescending(v => v.kbps).FirstOrDefault();
        if (pick.url == null) pick = mp3.OrderBy(v => v.kbps).First();

        using var q = new HttpRequestMessage(HttpMethod.Get, pick.url);
        q.Headers.TryAddWithoutValidation("Authorization", "OAuth " + _token);
        using var r = await Http.SendAsync(q);
        r.EnsureSuccessStatusCode();
        var x = XDocument.Parse(await r.Content.ReadAsStringAsync()).Root!;
        string host = (string?)x.Element("host") ?? "", path = (string?)x.Element("path") ?? "", ts = (string?)x.Element("ts") ?? "", s = (string?)x.Element("s") ?? "";
        var sign = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(SignSalt + path[1..] + s)));
        return $"https://{host}/get-mp3/{sign}/{ts}{path}";
    }

    // ---------------------------------------------------------------- HTTP
    static HttpRequestMessage Req(HttpMethod m, string path)
    {
        var q = new HttpRequestMessage(m, Api + path);
        q.Headers.TryAddWithoutValidation("X-Yandex-Music-Client", "YandexMusicAndroid/24023621");
        q.Headers.TryAddWithoutValidation("Accept-Language", "ru");
        if (_token != null) q.Headers.TryAddWithoutValidation("Authorization", "OAuth " + _token);
        return q;
    }

    static async Task<JsonElement> Get(string path, CancellationToken ct = default)
    {
        using var q = Req(HttpMethod.Get, path);
        return await Send(q, ct);
    }

    static async Task<JsonElement> Post(string path, params (string, string)[] form)
    {
        using var q = Req(HttpMethod.Post, path);
        q.Content = Form(form);
        return await Send(q, default);
    }

    static async Task<JsonElement> PostJson(string path, object body)
    {
        using var q = Req(HttpMethod.Post, path);
        q.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await Send(q, default);
    }

    static async Task<JsonElement> Send(HttpRequestMessage q, CancellationToken ct)
    {
        using var r = await Http.SendAsync(q, ct);
        if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("Яндекс не принял вход");
        r.EnsureSuccessStatusCode();
        using var d = JsonDocument.Parse(await r.Content.ReadAsStreamAsync(ct), default);
        return d.RootElement.TryGetProperty("result", out var res) ? res.Clone() : d.RootElement.Clone();
    }

    static FormUrlEncodedContent Form(params (string k, string v)[] kv) => new(kv.Select(p => new KeyValuePair<string, string>(p.k, p.v)));

    // ---------------------------------------------------------------- DPAPI
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    static byte[] Dpapi(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var inp = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            var outp = new Blob();
            bool ok = protect ? CryptProtectData(ref inp, "Player", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref outp)
                              : CryptUnprotectData(ref inp, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref outp);
            if (!ok) throw new InvalidOperationException("DPAPI");
            var res = new byte[outp.Size];
            Marshal.Copy(outp.Data, res, 0, outp.Size);
            LocalFree(outp.Data);
            return res;
        }
        finally { h.Free(); }
    }
}
