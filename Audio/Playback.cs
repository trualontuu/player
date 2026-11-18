using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Player.Core;
using Player.Yandex;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Streaming.Adaptive;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Player.Audio;

/// <summary>
/// Воспроизведение через системный Windows.Media.Playback.MediaPlayer: декодирует Windows (почти ноль CPU),
/// а SMTC даёт медиаклавиши, плашку громкости Windows и карточку в Island без единой строчки интеграции.
/// Все события отсюда приходят уже в UI-потоке.
/// </summary>
public sealed class Playback : IDisposable
{
    readonly MediaPlayer _mp = new() { AudioCategory = MediaPlayerAudioCategory.Media, AutoPlay = true };
    readonly Dispatcher _ui;
    readonly PlayerSettings _s;

    public List<Track> Queue { get; private set; } = new();
    public int Index { get; private set; } = -1;
    public Track? Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;
    public string QueueName { get; private set; } = "";
    public WaveSession? Wave { get; private set; }
    public bool IsWave => Wave != null;

    public event Action? TrackChanged;
    public event Action? StateChanged;
    public event Action<string>? Error;
    /// <summary>Очередь изменилась: новый список, перемешали, добавили/убрали/передвинули трек, волна догрузилась.</summary>
    public event Action? QueueChanged;

    // исходный порядок списка — чтобы выключить перемешивание и вернуть как было
    List<Track> _original = new();
    // треки, которые пользователь сам поставил в очередь: «добавить в очередь» кладёт после них
    readonly HashSet<Track> _manual = new();

    // заранее подготовленная ссылка на следующий трек
    readonly Dictionary<string, (string url, DateTime at)> _urls = new();
    int _loadGen;
    bool _waveLoading;
    DateTime _startedAt;
    readonly Random _rnd = new();
    readonly List<int> _history = new(); // для «назад» при перемешивании

    public Playback(Dispatcher ui, PlayerSettings s)
    {
        _ui = ui;
        _s = s;
        ApplyVolume();
        Sc.Init(s);
        _mp.MediaEnded += (_, _) => _ui.BeginInvoke(() => { if (!_disposed) OnEnded(); });
        _mp.MediaFailed += (_, e) => _ui.BeginInvoke(() => { if (_disposed) return; Error?.Invoke("Не удалось воспроизвести: " + e.ErrorMessage); Next(auto: true); });
        _mp.PlaybackSession.PlaybackStateChanged += (_, _) => _ui.BeginInvoke(() => { if (!_disposed) { ApplyPendingSeek(); StateChanged?.Invoke(); } });

        var cm = _mp.CommandManager;
        cm.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.NextReceived += (_, e) => { e.Handled = true; _ui.BeginInvoke(() => Next()); };
        cm.PreviousReceived += (_, e) => { e.Handled = true; _ui.BeginInvoke(Prev); };
    }

    bool _disposed;
    MediaPlaybackState State => _disposed ? MediaPlaybackState.None : _mp.PlaybackSession.PlaybackState;
    public bool IsPlaying => State is MediaPlaybackState.Playing or MediaPlaybackState.Buffering or MediaPlaybackState.Opening;
    public bool CanSeek => !_disposed && _mp.PlaybackSession.CanSeek;
    public string StateName => State.ToString();
    public bool IsBuffering => State is MediaPlaybackState.Buffering or MediaPlaybackState.Opening;
    public TimeSpan Position => _disposed ? TimeSpan.Zero : _mp.PlaybackSession.Position;
    public TimeSpan Duration
    {
        get
        {
            var d = _disposed ? TimeSpan.Zero : _mp.PlaybackSession.NaturalDuration;
            return d > TimeSpan.Zero ? d : TimeSpan.FromMilliseconds(Current?.DurationMs ?? 0);
        }
    }

    /// <summary>Громкость, которую выбрал пользователь (ползунок). В плеер уходит она же × коэффициент выравнивания.</summary>
    public double Volume
    {
        get => _s.Volume;
        set { _s.Volume = Math.Clamp(value, 0, 1); ApplyVolume(); _s.SaveSoon(); }
    }

    // ---------------------------------------------------------------- выравнивание громкости
    // ориентир — типичная громкость рэп-трека: такие звучат как без выравнивания, громче — тише, тише — чуть громче
    const double TargetLufs = -9;
    double _gain = 1;

    public double Gain => _gain;

    public bool Normalize
    {
        get => _s.Normalize;
        set { _s.Normalize = value; _s.SaveSoon(); _gain = GainFor(Current); ApplyVolume(); }
    }

    /// <summary>Коэффициент громкости трека. У SoundCloud данных нет — 1. Усиление ограничено ×1,6 и самим максимумом плеера.</summary>
    double GainFor(Track? t)
    {
        if (!_s.Normalize || t?.Lufs is not double lufs) return 1;
        return Math.Clamp(Math.Pow(10, (TargetLufs - lufs) / 20), 0.3, 1.6);
    }

    void ApplyVolume()
    {
        if (_disposed) return;
        _mp.Volume = Math.Clamp(_s.Volume * _gain, 0, 1);
    }

    // ---------------------------------------------------------------- очередь
    public void PlayList(IList<Track> list, int index, string name)
    {
        WaveFeedbackOnLeave();
        Wave = null;
        Queue = list.Where(t => t.Available).ToList();
        if (Queue.Count == 0) return;
        _original = Queue.ToList();
        _manual.Clear();
        var start = index >= 0 && index < list.Count ? list[index] : Queue[0];
        Index = Math.Max(0, Queue.IndexOf(start));
        if (index < 0 && _s.Shuffle) Index = _rnd.Next(Queue.Count);
        // перемешиваем заранее: порядок очереди и есть порядок игры, поэтому «Далее» всегда правдивое
        if (_s.Shuffle) ShuffleRest(moveCurrentFirst: true);
        QueueName = name;
        _history.Clear();
        QueueChanged?.Invoke();
        _ = Load();
    }

    /// <summary>Только для --snap: подменить зёрна волны (PLAYER_SEEDS через запятую).</summary>
    public static string[]? SeedsOverride;

    void ShuffleRest(bool moveCurrentFirst)
    {
        var cur = Current;
        if (cur == null) return;
        if (moveCurrentFirst) { Queue.RemoveAt(Index); Queue.Insert(0, cur); Index = 0; }
        for (int i = Queue.Count - 1; i > Index + 1; i--)
        {
            int j = _rnd.Next(Index + 1, i + 1);
            (Queue[i], Queue[j]) = (Queue[j], Queue[i]);
        }
    }

    /// <summary>Включили/выключили перемешивание посреди списка: перемешиваем то, что впереди, или возвращаем исходный порядок.</summary>
    public void SetShuffle(bool on)
    {
        if (IsWave || Queue.Count < 2 || Current is not { } cur) return;
        if (on) ShuffleRest(moveCurrentFirst: false);
        else
        {
            var extra = Queue.Where(t => !_original.Contains(t)).ToList();
            Queue = _original.ToList();
            Index = Queue.IndexOf(cur);
            if (Index < 0) { Queue.Insert(0, cur); Index = 0; }
            Queue.InsertRange(Index + 1, extra.Where(t => t != cur));
        }
        _history.Clear();
        QueueChanged?.Invoke();
    }

    // ---------------------------------------------------------------- ручная очередь
    public bool IsManual(Track t) => _manual.Contains(t);

    /// <summary>Сразу после текущего.</summary>
    public void PlayNext(Track t)
    {
        if (!t.Available) return;
        if (Current == null) { PlayList(new List<Track> { t }, 0, t.FullTitle); return; }
        if (t == Current) return;
        int at = Queue.IndexOf(t, Index + 1);
        if (at > 0) Queue.RemoveAt(at);
        Queue.Insert(Index + 1, t);
        _manual.Add(t);
        QueueChanged?.Invoke();
    }

    /// <summary>После уже добавленных вручную; в обычном списке — в конец.</summary>
    public void AddToQueue(Track t)
    {
        if (!t.Available) return;
        if (Current == null) { PlayList(new List<Track> { t }, 0, t.FullTitle); return; }
        int at = Index + 1;
        if (IsWave) while (at < Queue.Count && _manual.Contains(Queue[at])) at++;
        else at = Queue.Count;
        Queue.Insert(at, t);
        _manual.Add(t);
        QueueChanged?.Invoke();
    }

    public void RemoveAt(int i)
    {
        if (i < 0 || i >= Queue.Count || i == Index) return;
        _manual.Remove(Queue[i]);
        Queue.RemoveAt(i);
        if (i < Index) Index--;
        _history.Clear();
        QueueChanged?.Invoke();
    }

    /// <summary>Перетаскивание в списке «Далее»: оба индекса — позиции в очереди после текущего.</summary>
    public void Move(int from, int to)
    {
        if (from == to || from <= Index || to <= Index || from >= Queue.Count) return;
        var t = Queue[from];
        Queue.RemoveAt(from);
        Queue.Insert(Math.Min(to, Queue.Count), t);
        _history.Clear();
        QueueChanged?.Invoke();
    }

    public static string[] WaveSeeds(PlayerSettings s)
    {
        if (SeedsOverride != null) return SeedsOverride;
        var seeds = new List<string> { "user:onyourwave" };
        // CS2: волна от треков-образцов (Drake «Away From Home», Lil Uzi Vert «x2») — американский хайп-трэп
        if (s.WaveScene == "cs2" && s.Cs2Seeds.Length > 0) return s.Cs2Seeds;
        if (s.WaveScene == "cs2") return new[] { "user:onyourwave", "mood:aggressive", "mood:energetic" };
        if (s.WaveScene == "cs2phonk") return new[] { "user:onyourwave", "mood:aggressive", "genre:phonkgenre" };
        if (s.WaveDiversity.Length > 0) seeds.Add("settingDiversity:" + s.WaveDiversity);
        if (s.WaveMood.Length > 0) seeds.Add("settingMoodEnergy:" + s.WaveMood);
        return seeds.ToArray();
    }

    /// <summary>Бесконечная «Моя волна». Новые треки подгружаются, когда в очереди остаётся пара штук.</summary>
    public async Task PlayWave() => await PlayWave(null, null);

    /// <summary>Волна по своим зёрнам — например, «волна по артисту» (artist:id).</summary>
    public async Task PlayWave(string[]? seeds, string? name)
    {
        WaveFeedbackOnLeave();
        _waveLoading = true;
        try
        {
            _cs2 = seeds == null && _s.WaveScene.StartsWith("cs2");
            var (session, tracks) = await Ym.WaveStart(seeds ?? WaveSeeds(_s));
            // CS2: выкидываем артистов из стоп-листа; если отсеялось всё — просим у волны ещё
            for (int tries = 0; _cs2 && tries < 3; tries++)
            {
                var ok = tracks.Where(t => !Excluded(t)).ToList();
                if (ok.Count > 0 || tracks.Count == 0) { tracks = ok; break; }
                tracks = await Ym.WaveMore(session, tracks);
            }
            if (tracks.Count == 0) { Error?.Invoke("Волна не прислала треков"); return; }
            Wave = session;
            Queue = tracks;
            Index = 0;
            QueueName = name ?? (_s.WaveScene.StartsWith("cs2") ? "Моя волна · CS2" : "Моя волна");
            _history.Clear();
            _manual.Clear();
            QueueChanged?.Invoke();
            await Load();
        }
        catch (Exception e) { Error?.Invoke("Моя волна: " + e.Message); }
        finally { _waveLoading = false; }
    }

    async Task TopUpWave()
    {
        if (Wave == null || _waveLoading || Index < Queue.Count - 2) return;
        _waveLoading = true;
        try
        {
            var more = await Ym.WaveMore(Wave, Queue.Skip(Math.Max(0, Queue.Count - 10)));
            var have = Queue.Select(t => t.Id).ToHashSet();
            Queue.AddRange(more.Where(t => !(_cs2 && Excluded(t)) && have.Add(t.Id)));
            if (Queue.Count > 200) { int cut = Math.Min(Index - 20, Queue.Count - 200); if (cut > 0) { Queue.RemoveRange(0, cut); Index -= cut; } }
            QueueChanged?.Invoke();
        }
        catch { }
        finally { _waveLoading = false; }
    }

    // ---------------------------------------------------------------- управление
    public void Toggle()
    {
        if (Current == null) { if (Queue.Count == 0) _ = PlayWave(); return; }
        if (State == MediaPlaybackState.Playing) _mp.Pause();
        else if (_mp.Source == null) _ = Load();
        else _mp.Play();
    }

    public void Pause() => _mp.Pause();

    public void Next(bool auto = false)
    {
        if (Queue.Count == 0) return;
        if (!auto) WaveFeedback("skip");
        else WaveFeedback("trackFinished");

        if (auto && _s.Repeat == 2 && !IsWave) { _ = Load(); return; }
        _history.Add(Index);
        if (Index + 1 < Queue.Count) Index++;
        else if (IsWave) { _ = TopUpThenNext(); return; }
        else if (_s.Repeat == 1 || !auto) { Index = 0; if (_s.Shuffle) ShuffleRest(false); QueueChanged?.Invoke(); }
        else { _mp.Pause(); _mp.PlaybackSession.Position = TimeSpan.Zero; StateChanged?.Invoke(); return; }
        _ = Load();
    }

    async Task TopUpThenNext()
    {
        await TopUpWave();
        if (Index + 1 < Queue.Count) { Index++; await Load(); }
    }

    public void Prev()
    {
        if (Queue.Count == 0) return;
        if (Position.TotalSeconds > 3) { Seek(TimeSpan.Zero); return; }
        // назад — туда, откуда пришли (после клика по треку в «Далее»), иначе просто на предыдущий в очереди
        if (_history.Count > 0 && _history[^1] < Queue.Count) { Index = _history[^1]; _history.RemoveAt(_history.Count - 1); }
        else if (Index > 0) Index--;
        else { Seek(TimeSpan.Zero); return; }
        _ = Load();
    }

    public void PlayAt(int i)
    {
        if (i < 0 || i >= Queue.Count || i == Index) return;
        WaveFeedback("skip");
        _history.Add(Index);
        Index = i;
        _ = Load();
    }

    TimeSpan? _pendingSeek;

    void ApplyPendingSeek()
    {
        if (_pendingSeek is { } t && !_disposed && _mp.PlaybackSession.CanSeek)
        {
            _pendingSeek = null;
            try { _mp.PlaybackSession.Position = t; } catch { }
        }
    }

    /// <summary>Поток может стать перематываемым без смены состояния (HLS SoundCloud уже «играет») — проверяем ещё 5 с.</summary>
    async Task RetryPendingSeek()
    {
        for (int i = 0; i < 25 && _pendingSeek != null; i++)
        {
            await Task.Delay(200);
            ApplyPendingSeek();
        }
    }

    public void Seek(TimeSpan t)
    {
        if (_disposed) return;
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        // трек ещё открывается — запоминаем и перематываем, как только станет можно
        if (!_mp.PlaybackSession.CanSeek) { _pendingSeek = t; _ = RetryPendingSeek(); return; }
        _pendingSeek = null;
        try { _mp.PlaybackSession.Position = t; } catch { }
    }

    void OnEnded() => Next(auto: true);

    // ---------------------------------------------------------------- загрузка трека
    async Task Load()
    {
        _pendingSeek = null;
        var t = Current;
        if (t == null) return;
        _gain = GainFor(t);
        ApplyVolume();
        int gen = ++_loadGen;
        foreach (var q in Queue) q.IsPlaying = false;
        t.IsPlaying = true;
        TrackChanged?.Invoke();

        string url;
        try { url = await UrlFor(t); }
        catch (Exception e)
        {
            if (gen != _loadGen) return;
            Error?.Invoke($"«{t.Title}»: {e.Message}");
            await Task.Delay(800);
            if (gen == _loadGen) Next(auto: true);
            return;
        }
        if (gen != _loadGen) return;

        MediaSource src;
        if (t.IsSc)
        {
            // SoundCloud отдаёт HLS — через AdaptiveMediaSource, иначе MediaPlayer может не понять плейлист без .m3u8 в адресе
            var ams = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(url));
            if (gen != _loadGen) return;
            if (ams.Status != AdaptiveMediaSourceCreationStatus.Success)
            {
                Error?.Invoke($"«{t.Title}»: SoundCloud-поток не открылся ({ams.Status})");
                await Task.Delay(800);
                if (gen == _loadGen) Next(auto: true);
                return;
            }
            src = MediaSource.CreateFromAdaptiveMediaSource(ams.MediaSource);
        }
        else src = MediaSource.CreateFromUri(new Uri(url));
        var item = new MediaPlaybackItem(src);
        var p = item.GetDisplayProperties();
        p.Type = MediaPlaybackType.Music;
        p.MusicProperties.Title = t.FullTitle;
        p.MusicProperties.Artist = t.Artists;
        p.MusicProperties.AlbumTitle = t.Album;
        if (t.CoverUri.Length > 0) p.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(t.CoverUrl(400)));
        item.ApplyDisplayProperties(p);
        _mp.Source = item;
        _mp.Play();
        _startedAt = DateTime.UtcNow;
        WaveFeedback("trackStarted");

        _ = Prefetch();
        if (IsWave) _ = TopUpWave();
    }

    async Task<string> UrlFor(Track t)
    {
        if (_urls.TryGetValue(t.Id, out var u) && DateTime.UtcNow - u.at < TimeSpan.FromMinutes(10)) return u.url;
        var url = t.IsSc ? await Sc.StreamUrl(t) : await Ym.StreamUrl(t, _s.MaxKbps);
        _urls[t.Id] = (url, DateTime.UtcNow);
        if (_urls.Count > 20) foreach (var k in _urls.OrderBy(p => p.Value.at).Take(10).Select(p => p.Key).ToList()) _urls.Remove(k);
        return url;
    }

    /// <summary>Ссылку на следующий трек готовим заранее — переход без паузы на запросы.</summary>
    async Task Prefetch()
    {
        if (_s.Shuffle && !IsWave) return;
        if (Index + 1 >= Queue.Count) return;
        try { await UrlFor(Queue[Index + 1]); } catch { }
    }

    // ---------------------------------------------------------------- отзывы волне
    void WaveFeedback(string type)
    {
        if (Wave == null || Current == null || Current.IsSc) return;
        double played = type == "trackStarted" ? 0 : Math.Min(Position.TotalSeconds, (DateTime.UtcNow - _startedAt).TotalSeconds);
        _ = Ym.WaveFeedback(Wave, type, Current, played);
    }

    void WaveFeedbackOnLeave() { if (Wave != null && Current != null && IsPlaying) WaveFeedback("skip"); }

    public async Task LikeCurrent()
    {
        var t = Current;
        if (t == null) return;
        try
        {
            await Ym.SetLike(t, !t.Liked);
            if (t.Liked && Wave != null) _ = Ym.WaveFeedback(Wave, "like", t);
        }
        catch (Exception e) { Error?.Invoke("Лайк: " + e.Message); }
    }

    /// <summary>«Не нравится» в волне: дизлайк и сразу следующий.</summary>
    bool _cs2;
    /// <summary>Играет CS2-волна (обычная «Моя волна» в режиме CS2, не волна по артисту).</summary>
    public bool IsCs2Wave => IsWave && _cs2;

    bool Excluded(Track t) =>
        t.ArtistRefs.Any(a => _s.Cs2Exclude.Any(x => string.Equals(x, a.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>«Не для CS2»: главный артист трека — в стоп-лист, его треки уходят из очереди, играет следующий.
    /// В Яндекс дизлайк не отправляется — вне игры трек может нравиться.</summary>
    public string? NotForCs2()
    {
        var t = Current;
        if (t == null || t.ArtistRefs.Count == 0) return null;
        var name = t.ArtistRefs[0].Name;
        if (!_s.Cs2Exclude.Contains(name, StringComparer.OrdinalIgnoreCase)) { _s.Cs2Exclude.Add(name); _s.SaveSoon(); }
        for (int i = Queue.Count - 1; i > Index; i--) if (Excluded(Queue[i])) Queue.RemoveAt(i);
        if (Wave != null) _ = Ym.WaveFeedback(Wave, "skip", t, Position.TotalSeconds);
        QueueChanged?.Invoke();
        Next();
        return name;
    }

    public async Task DislikeCurrent()
    {
        var t = Current;
        if (t == null) return;
        try
        {
            await Ym.Dislike(t);
            if (Wave != null) _ = Ym.WaveFeedback(Wave, "dislike", t);
        }
        catch (Exception e) { Error?.Invoke("Дизлайк: " + e.Message); }
        Next();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mp.Pause(); _mp.Dispose(); } catch { }
    }
}
