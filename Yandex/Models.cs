using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using Player.UI;

namespace Player.Yandex;

/// <summary>
/// Трек. Один объект на один id во всём приложении (Track.From), поэтому лайк и «сейчас играет»
/// сразу видны во всех списках без пересинхронизации.
/// </summary>
public sealed class Track : INotifyPropertyChanged
{
    public string Id = "", AlbumId = "";
    public string Title { get; private set; } = "";
    public string? Version { get; private set; }
    public string Artists { get; private set; } = "";
    public string Album { get; private set; } = "";
    public int DurationMs { get; private set; }
    public string CoverUri = "";
    public bool Available { get; private set; } = true;
    /// <summary>Исполнители с id — для перехода на страницу артиста.</summary>
    public List<(string Id, string Name)> ArtistRefs { get; private set; } = new();

    public string Key => AlbumId.Length > 0 ? $"{Id}:{AlbumId}" : Id;
    public string FullTitle => Version is { Length: > 0 } ? $"{Title} ({Version})" : Title;
    public string Duration => DurationMs <= 0 ? "" : TimeSpan.FromMilliseconds(DurationMs).ToString(DurationMs >= 3600000 ? @"h\:mm\:ss" : @"m\:ss");
    public string CoverUrl(int px) => Img(CoverUri, px);

    /// <summary>Адрес обложки нужного размера. Яндекс: "avatars.../%%" → 200x200; SoundCloud: "https://...-%%.jpg" → t500x500/large.</summary>
    public static string Img(string uri, int px) =>
        uri.Length == 0 ? "" :
        uri.StartsWith("http") ? uri.Replace("%%", px > 120 ? "t500x500" : "large") :
        "https://" + uri.Replace("%%", $"{px}x{px}");

    /// <summary>Трек SoundCloud (id "sc:…"). Лайков и волны у них нет.</summary>
    public bool IsSc => Id.StartsWith("sc:");
    internal string ScStream = "", ScAuth = "";
    /// <summary>Громкость трека по EBU R128 (LUFS) от Яндекса; у SoundCloud её нет.</summary>
    public double? Lufs { get; private set; }

    bool _liked, _playing;
    public bool Liked { get => _liked; set { if (_liked != value) { _liked = value; On(nameof(Liked)); } } }
    public bool IsPlaying { get => _playing; set { if (_playing != value) { _playing = value; On(nameof(IsPlaying)); } } }

    ImageSource? _cover;
    bool _coverAsked;
    /// <summary>Обложка грузится, только когда строку реально показали (виртуализация списка).</summary>
    public ImageSource? Cover
    {
        get
        {
            if (_cover == null && !_coverAsked && CoverUri.Length > 0)
            {
                _coverAsked = true;
                Covers.Load(CoverUrl(100), 44, img => { _cover = img; On(nameof(Cover)); });
            }
            return _cover;
        }
    }
    public void ResetCover() { _cover = null; _coverAsked = false; On(nameof(Cover)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    void On(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    // ---------- разбор JSON ----------
    static readonly Dictionary<string, Track> All = new();
    public static readonly HashSet<string> LikedIds = new();

    public static Track? From(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idEl)) return null;
        var id = idEl.ToString();
        lock (All)
        {
            if (!All.TryGetValue(id, out var t)) All[id] = t = new Track { Id = id };
            t.Title = S(e, "title");
            t.Version = S(e, "version") is { Length: > 0 } v ? v : null;
            if (e.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array)
            {
                t.ArtistRefs = ar.EnumerateArray().Select(a => (S(a, "id"), S(a, "name"))).Where(x => x.Item2.Length > 0).ToList();
                t.Artists = string.Join(", ", t.ArtistRefs.Select(x => x.Item2));
            }
            if (e.TryGetProperty("albums", out var al) && al.ValueKind == JsonValueKind.Array && al.GetArrayLength() > 0)
            {
                t.AlbumId = al[0].TryGetProperty("id", out var aid) ? aid.ToString() : "";
                t.Album = S(al[0], "title");
            }
            if (e.TryGetProperty("durationMs", out var d) && d.TryGetInt32(out int ms)) t.DurationMs = ms;
            if (e.TryGetProperty("r128", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("i", out var li) && li.TryGetDouble(out double lufs)) t.Lufs = lufs;
            t.CoverUri = S(e, "coverUri") is { Length: > 0 } c ? c : S(e, "ogImage");
            t.Available = !e.TryGetProperty("available", out var av) || av.ValueKind != JsonValueKind.False;
            t._liked = LikedIds.Contains(id);
            return t;
        }
    }

    /// <summary>Трек SoundCloud из api-v2. Берём открытый HLS (сначала AAC, потом MP3); только DRM или 30-секундное превью — недоступен.</summary>
    public static Track? FromSc(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idEl)) return null;
        var id = "sc:" + idEl;
        lock (All)
        {
            if (!All.TryGetValue(id, out var t)) All[id] = t = new Track { Id = id };
            if (!e.TryGetProperty("title", out _)) return t;
            e.TryGetProperty("user", out var u);
            t.Title = S(e, "title");
            var who = S(u, "username");
            t.ArtistRefs = new() { ("sc:" + S(u, "id"), who) };
            t.Artists = who;
            t.Album = e.TryGetProperty("publisher_metadata", out var pm) ? S(pm, "album_title") : "";
            t.DurationMs = e.TryGetProperty("full_duration", out var fd) && fd.TryGetInt32(out int f) ? f
                         : e.TryGetProperty("duration", out var d) && d.TryGetInt32(out int ms) ? ms : 0;
            var art = S(e, "artwork_url");
            if (art.Length == 0) art = S(u, "avatar_url");
            t.CoverUri = art.Replace("-large.", "-%%.");
            t.ScAuth = S(e, "track_authorization");
            t.ScStream = "";
            if (S(e, "policy") != "BLOCK" && e.TryGetProperty("media", out var m) && m.TryGetProperty("transcodings", out var tc))
            {
                var open = tc.EnumerateArray()
                    .Where(x => x.TryGetProperty("format", out var fm) && S(fm, "protocol") is "hls" or "progressive"
                                && !(x.TryGetProperty("snipped", out var sn) && sn.ValueKind == JsonValueKind.True))
                    .OrderBy(x => S(x.GetProperty("format"), "mime_type").Contains("mp4a") ? 0 : 1)
                    .ThenBy(x => S(x, "quality") == "hq" ? 0 : S(x, "quality") == "sq" ? 1 : 2)
                    .ToList();
                if (open.Count > 0) t.ScStream = S(open[0], "url");
            }
            t.Available = t.ScStream.Length > 0;
            return t;
        }
    }

    public static Track? Find(string id) { lock (All) return All.TryGetValue(id, out var t) ? t : null; }

    internal static string S(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v.ToString() : "";
}

public sealed class Playlist
{
    public string Kind = "", Owner = "", Title = "", OwnerName = "";
    public int TrackCount;
    public string CoverUri = "";
}

public sealed class Artist
{
    public string Id = "", Name = "", CoverUri = "", Genre = "";
    public int TrackCount;
}

public sealed class Album
{
    public string Id = "", Title = "", Year = "", CoverUri = "", Type = "", Artists = "";
    public List<(string Id, string Name)> ArtistRefs = new();
    public int TrackCount;
    public string Kind => Type switch { "single" => "Сингл", "ep" => "EP", "compilation" => "Сборник", "podcast" => "Подкаст", "playlist" => "Плейлист", _ => "Альбом" };
}

/// <summary>Плитка в сетке: артист (круглая), альбом или плейлист. Обложка грузится, когда плитку показали.</summary>
public sealed class Card : INotifyPropertyChanged
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool Round { get; init; }
    public string CoverUri { get; init; } = "";
    public object Item { get; init; } = null!;

    ImageSource? _cover;
    bool _asked;
    public ImageSource? Cover
    {
        get
        {
            if (_cover == null && !_asked && CoverUri.Length > 0)
            {
                _asked = true;
                Covers.Load(Track.Img(CoverUri, 200), 150, img => { _cover = img; PropertyChanged?.Invoke(this, new(nameof(Cover))); });
            }
            return _cover;
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public static Card Of(Artist a) => new() { Title = a.Name, Subtitle = "Артист", Round = true, CoverUri = a.CoverUri, Item = a };
    public static Card Of(Album a, bool withArtist = true) => new()
    {
        Title = a.Title, CoverUri = a.CoverUri, Item = a,
        Subtitle = withArtist ? (a.Year.Length > 0 ? $"{a.Year} · {a.Artists}" : a.Artists) : (a.Year.Length > 0 ? $"{a.Year} · {a.Kind}" : a.Kind),
    };
    public static Card Of(Playlist p) => new()
    {
        Title = p.Title, CoverUri = p.CoverUri, Item = p,
        Subtitle = p.OwnerName.Length > 0 ? p.OwnerName : $"{p.TrackCount} треков",
    };
}

public sealed class WaveSession
{
    public string Id = "", BatchId = "";
    public string[] Seeds = Array.Empty<string>();
}
