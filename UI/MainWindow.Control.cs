using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Player.Yandex;
using Track = Player.Yandex.Track;

namespace Player.UI;

/// <summary>Команды из лаунчера (через ControlPipe). Выполняются в UI-потоке.</summary>
public partial class MainWindow
{
    readonly Dictionary<string, List<Track>> _ctlSearches = new();

    public Task<object> HandleControl(JsonElement req) => Dispatcher.InvokeAsync(() => Control(req)).Task.Unwrap();

    async Task<object> Control(JsonElement req)
    {
        string cmd = Track.S(req, "cmd"), id = Track.S(req, "id"), q = Track.S(req, "q");
        // плеер только что запущен лаунчером — ждём, пока подхватит вход (до 6 с)
        for (int i = 0; cmd != "state" && Ym.LoggedIn && Ym.Login.Length == 0 && i < 30; i++) await Task.Delay(200);
        switch (cmd)
        {
            case "state":
                return State();

            case "search":
            {
                if (!Ym.LoggedIn) return new { items = Array.Empty<object>() };
                var r = await Ym.SearchAll(q);
                var tracks = r.Tracks.Where(t => t.Available).ToList();
                // искали артиста — сначала его треки, а не чужие песни с его именем в названии
                if (r.BestType == "artist" && r.Artists.FirstOrDefault() is { } top)
                    tracks = tracks.OrderBy(t => t.Artists.Contains(top.Name, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
                lock (_ctlSearches) { if (_ctlSearches.Count > 20) _ctlSearches.Clear(); _ctlSearches[q] = tracks; }
                var items = new List<object>();
                var artists = r.Artists.Take(1)
                    .Select(a => (object)new { type = "artist", id = a.Id, title = a.Name, sub = "Артист", cover = Track.Img(a.CoverUri, 100) });
                var ts = tracks.Take(4).Select(t => (object)new { type = "track", id = t.Id, title = t.FullTitle, sub = t.Artists, cover = t.CoverUrl(100) });
                items.AddRange(r.BestType == "artist" ? artists.Concat(ts) : ts.Concat(artists));
                return new { items };
            }

            case "play":
            {
                List<Track>? list;
                lock (_ctlSearches) _ctlSearches.TryGetValue(q, out list);
                var t = list?.FirstOrDefault(x => x.Id == id) ?? Track.Find(id);
                if (t == null) return new { error = "трек не найден" };
                if (list == null || !list.Contains(t)) list = new List<Track> { t };
                _pb.PlayList(list, list.IndexOf(t), "Поиск: " + q);
                return State();
            }

            case "artist":
                ShowFromTray();
                OpenArtist(id);
                return new { ok = true };

            case "toggle":
                if (_pb.Current == null) await _pb.PlayWave(); else _pb.Toggle();
                break;
            case "next": _pb.Next(); break;
            case "prev": _pb.Prev(); break;
            case "like":
                if (_pb.Current is { IsSc: false }) await _pb.LikeCurrent();
                return State(); // без паузы: лайк уже записан в t.Liked

            case "wave":
            case "wave_cs2":
                _s.WaveScene = cmd == "wave_cs2" ? "cs2" : "";
                _s.SceneBeforeCs2 = null;
                _s.SaveSoon();
                BuildWaveChips();
                await _pb.PlayWave();
                break;

            case "show":
                ShowFromTray();
                return new { ok = true };

            // пульт с телефона
            case "volume":
                if (req.TryGetProperty("v", out var v) && v.TryGetDouble(out var vol)) SetVolume(vol);
                return State();
            case "seek":
                if (req.TryGetProperty("t", out var tt) && tt.TryGetDouble(out var sec) && _pb.CanSeek)
                {
                    var to = TimeSpan.FromSeconds(Math.Max(0, sec));
                    _seekTo = to; _seekHold = DateTime.UtcNow.AddSeconds(1.5);
                    _pb.Seek(to);
                }
                return State();

            default:
                return new { error = "неизвестная команда: " + cmd };
        }
        await Task.Delay(150); // состояние MediaPlayer обновляется не мгновенно
        return State();
    }

    /// <summary>Состояние для пульта на телефоне (Noir Connect).</summary>
    public Task<object> RemoteState() => Dispatcher.InvokeAsync(() =>
    {
        var t = _pb.Current;
        return (object)new
        {
            playing = _pb.IsPlaying,
            title = t?.FullTitle ?? "",
            artist = t?.Artists ?? "",
            cover = t?.CoverUrl(400) ?? "",
            liked = t?.Liked == true,
            sc = t?.IsSc == true,
            time = Math.Round(_pb.Position.TotalSeconds, 2),
            duration = Math.Round(_pb.Duration.TotalSeconds, 2),
            volume = Math.Round(_pb.Volume, 3),
            queue = _pb.QueueName,
            id = t?.Id ?? "",
        };
    }).Task;

    /// <summary>Подпись состояния: меняется, когда пульту есть что показать (трек, пауза, лайк, громкость, перемотка).</summary>
    public Task<string> RemoteSig() => Dispatcher.InvokeAsync(() =>
    {
        var t = _pb.Current;
        // при игре «якорь» = момент начала трека по часам: сдвигается только при перемотке
        var anchor = _pb.IsPlaying ? (long)((DateTime.UtcNow - _pb.Position).Ticks / TimeSpan.TicksPerSecond / 2) : (long)_pb.Position.TotalSeconds;
        return $"{(_pb.IsPlaying ? 1 : 0)}|{t?.Id}|{t?.Liked}|{_pb.Volume:0.00}|{anchor}";
    }).Task;

    object State()
    {
        var t = _pb.Current;
        return new
        {
            playing = _pb.IsPlaying,
            title = t?.FullTitle ?? "",
            artist = t?.Artists ?? "",
            liked = t?.Liked == true,
            sc = t?.IsSc == true, // у SoundCloud лайков нет — остров прячет сердечко
            queue = _pb.QueueName,
        };
    }
}
