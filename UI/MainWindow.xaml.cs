using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Island.Core;
using Player.Audio;
using Player.Core;
using Player.Yandex;

namespace Player.UI;

public partial class MainWindow : Window
{
    readonly Playback _pb;
    readonly PlayerSettings _s;
    readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    List<Track> _shown = new();
    string _shownName = "";
    List<Track>? _likes;
    List<Playlist> _playlists = new();
    CancellationTokenSource? _searchCts;
    Track? _nowTrack;
    bool _seeking, _settingVol;
    TimeSpan? _seekTo;
    DateTime _seekHold;
    double _volBeforeMute = 0.6;

    public bool SnapMode;

    public MainWindow(Playback pb, PlayerSettings s)
    {
        _pb = pb;
        _s = s;
        InitializeComponent();
        ApplyBounds();

        // окно
        BtnMin.Click += (_, _) => WindowState = WindowState.Minimized;
        BtnMax.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        BtnClose.Click += (_, _) => HideToTray();
        StateChanged += (_, _) =>
        {
            BtnMax.Content = WindowState == WindowState.Maximized ? "" : "";
            // развёрнутое окно без рамки залезает за край экрана на толщину рамки
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            UpdateTimer();
        };
        IsVisibleChanged += (_, _) => UpdateTimer();

        // навигация, обзор, очередь — MainWindow.Browse.cs
        InitBrowse();

        // волна
        WavePlay.Click += async (_, _) => { if (_pb.IsWave) _pb.Toggle(); else await _pb.PlayWave(); };
        HeroNext.Click += (_, _) => _pb.Next();
        HeroDislike.Click += async (_, _) => await Dislike();
        TuneToggle.Click += (_, _) =>
        {
            bool open = TunePanel.Visibility != Visibility.Visible;
            TunePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            TuneToggle.Content = open ? "Скрыть \u25B4" : "Настроить \u25BE";
            if (open) Enter(TunePanel, 6);
            FitHero();
        };
        WavePage.SizeChanged += (_, _) => FitHero();
        BuildWaveChips();
        AutoCs2.Click += (_, _) => { _s.AutoCs2 = !_s.AutoCs2; _s.SaveSoon(); BuildWaveChips(); UpdateCs2Watch(); };
        _cs2Timer.Tick += async (_, _) => await CheckCs2();

        // списки
        ListPlay.Click += (_, _) =>
        {
            if (_pb.QueueName == _shownName && !_pb.IsWave && _pb.Current != null) _pb.Toggle();
            else _pb.PlayList(_shown, 0, _shownName);
        };
        ListShuffle.Click += (_, _) =>
        {
            _s.Shuffle = true; _s.SaveSoon(); PaintModes();
            _pb.PlayList(_shown, -1, _shownName);
        };

        // поиск
        SearchText.TextChanged += (_, _) =>
        {
            SearchHint.Visibility = SearchText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchTimer.Stop(); _searchTimer.Start();
        };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RunSearch(SearchText.Text.Trim()); };

        // нижняя панель
        BtnPlay.Click += (_, _) => _pb.Toggle();
        BtnNext.Click += (_, _) => _pb.Next();
        BtnPrev.Click += (_, _) => _pb.Prev();
        BtnShuffle.Click += (_, _) => { _s.Shuffle = !_s.Shuffle; _s.SaveSoon(); PaintModes(); _pb.SetShuffle(_s.Shuffle); };
        BtnRepeat.Click += (_, _) => { _s.Repeat = (_s.Repeat + 1) % 3; _s.SaveSoon(); PaintModes(); };
        NowLike.Click += async (_, _) => await _pb.LikeCurrent();
        BtnDislike.Click += async (_, _) => await Dislike();
        BtnMute.Click += (_, _) =>
        {
            if (_pb.Volume > 0) { _volBeforeMute = _pb.Volume; SetVolume(0); }
            else SetVolume(_volBeforeMute > 0.02 ? _volBeforeMute : 0.5);
        };
        Vol.ValueChanged += (_, e) => { if (!_settingVol) { _pb.Volume = e.NewValue; PaintVolume(); } };
        Vol.MouseWheel += (_, e) => { SetVolume(_pb.Volume + (e.Delta > 0 ? 0.05 : -0.05)); e.Handled = true; };
        SetVolume(_s.Volume);

        // перемотка: тянуть можно с любой точки полосы; позицию плеера меняем, когда отпустили
        SliderDrag.Attach(Seek, () => _seeking = true, () =>
        {
            if (!_seeking) return;
            _seeking = false;
            var d = _pb.Duration;
            if (d <= TimeSpan.Zero) return;
            var to = TimeSpan.FromMilliseconds(Seek.Value * d.TotalMilliseconds);
            _pb.Seek(to);
            // пока поток догоняет новую позицию, не даём таймеру отбросить ползунок назад
            _seekTo = to; _seekHold = DateTime.UtcNow.AddSeconds(2);
        });
        SliderDrag.Attach(Vol, null, () => _s.SaveSoon());
        Seek.ValueChanged += (_, _) => { if (_seeking) PosText.Text = Fmt(TimeSpan.FromMilliseconds(Seek.Value * _pb.Duration.TotalMilliseconds)); };

        _pb.TrackChanged += OnTrackChanged;
        _pb.StateChanged += OnStateChanged;
        _pb.Error += ShowToast;
        _tick.Tick += (_, _) => UpdatePosition();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

        LoginBtn.Click += async (_, _) => await DoLogin();
        PreviewKeyDown += OnKey;
        Closing += (_, e) => { if (!_quitting) { e.Cancel = true; HideToTray(); } };

        PaintModes();
        OnTrackChanged();
    }

    // ---------------------------------------------------------------- старт
    public async Task Start()
    {
        Ym.LoadToken();
        if (!Ym.LoggedIn) { ShowLogin(); return; }
        try
        {
            await Ym.LoadAccount();
            Account.Text = Ym.Login;
            AccountLetter.Text = Ym.Login.Length > 0 ? Ym.Login[..1].ToUpperInvariant() : "";
        }
        catch (UnauthorizedAccessException) { Ym.Logout(); ShowLogin(); return; }
        catch (Exception e) { ShowToast("Нет связи с Яндекс Музыкой: " + e.Message); }

        switch (_s.Page)
        {
            case "likes": NavLikes.IsChecked = true; break;
            case "search": NavSearch.IsChecked = true; break;
            default: NavWave.IsChecked = true; break;
        }
        _ = LoadPlaylists();
        if (_likes == null) _ = LoadLikes(); // сердечки во всех списках
    }

    async Task DoLogin()
    {
        try
        {
            LoginBtn.IsEnabled = false;
            var dc = await Ym.StartLogin();
            LoginCode.Text = dc.UserCode;
            LoginCode.Visibility = Visibility.Visible;
            LoginText.Text = $"Откройте {dc.Url.Replace("https://", "")} и введите код:";
            Process.Start(new ProcessStartInfo(dc.Url) { UseShellExecute = true })?.Dispose();
            if (await Ym.WaitLogin(dc, CancellationToken.None))
            {
                LoginPage.Visibility = Visibility.Collapsed;
                await Start();
            }
            else { LoginText.Text = "Код не подтвердили. Попробуйте ещё раз."; LoginCode.Visibility = Visibility.Collapsed; }
        }
        catch (Exception e) { LoginText.Text = "Не получилось: " + e.Message; }
        finally { LoginBtn.IsEnabled = true; }
    }

    void ShowLogin() => LoginPage.Visibility = Visibility.Visible;

    async Task LoadPlaylists()
    {
        try { _playlists = await Ym.Playlists(); }
        catch { return; }
        PlaylistNav.Children.Clear();
        foreach (var p in _playlists)
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var thumb = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(5), Margin = new Thickness(-4, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255)), Child = img,
                Clip = new RectangleGeometry(new Rect(0, 0, 30, 30), 5, 5),
            };
            var url = CoverUrl(p.CoverUri, 100);
            if (url.Length > 0) Covers.Load(url, 30, i => img.Source = i);
            var row = new DockPanel();
            DockPanel.SetDock(thumb, Dock.Left);
            row.Children.Add(thumb);
            row.Children.Add(new TextBlock { Text = p.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton
            {
                Style = (Style)FindResource("NavBtn"), GroupName = "nav", Tag = p, Height = 42,
                Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            rb.Checked += (_, _) => { if (!_quietNav) _ = Go(RootNav(rb, () => ShowPlaylist(p)), true); };
            PlaylistNav.Children.Add(rb);
        }
    }

    public async Task LoadLikes()
    {
        try
        {
            _likes = await Ym.Likes();
            if (_shownName == "Мне нравится") ShowList("Мне нравится", _likes);
        }
        catch (Exception e) { ShowToast("«Мне нравится»: " + e.Message); }
    }

    // ---------------------------------------------------------------- страницы
    void ShowWave()
    {
        _s.Page = "wave"; _s.SaveSoon();
        ShowPage(WavePage);
        _shownName = "";
        WaveArt.SetPlaying(_pb.IsWave && _pb.IsPlaying);
    }

    public async Task ShowLikes()
    {
        _s.Page = "likes"; _s.SaveSoon();
        SetListArt("ПЛЕЙЛИСТ", "\uEB52", "");
        if (_likes != null) { ShowList("Мне нравится", _likes); return; }
        ShowList("Мне нравится", new List<Track>(), "Загружаю…");
        await LoadLikes();
    }

    public async Task ShowPlaylist(Playlist p)
    {
        int gen = _pageGen;
        SetListArt("ПЛЕЙЛИСТ", "\uE8D6", p.CoverUri);
        ShowList(p.Title, new List<Track>(), "Загружаю…");
        try
        {
            var list = await Ym.PlaylistTracks(p);
            if (gen == _pageGen) ShowList(p.Title, list);
        }
        catch (Exception e) { if (gen == _pageGen) ShowList(p.Title, new List<Track>(), "Не загрузилось: " + e.Message); }
    }

    public void SetSearchText(string q) { SearchText.Text = q; }

    void ShowList(string title, List<Track> list, string? empty = null, string? queueName = null)
    {
        var name = queueName ?? title;
        ShowPage(ListPage, _shownName != name);
        ListTitle.Text = title;
        ListTitle.FontSize = title.Length > 34 ? 26 : title.Length > 22 ? 32 : 38;
        ListSub.Visibility = Visibility.Collapsed;
        _shown = list;
        _shownName = name;
        Tracks.Tag = new TrackSource(name, list);
        Tracks.ItemsSource = list;
        long ms = list.Sum(t => (long)t.DurationMs);
        ListInfo.Text = list.Count == 0 ? "" : $"{Plural(list.Count, "трек", "трека", "треков")} · {Hours(ms)}";
        ListEmpty.Text = empty ?? "";
        ListEmpty.Visibility = list.Count == 0 && empty != null ? Visibility.Visible : Visibility.Collapsed;
        ListPlay.Visibility = ListShuffle.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (list.Count > 0) Tracks.ScrollIntoView(list[0]);
    }

    void SetListArt(string kind, string glyph, string coverUri)
    {
        ListHeader.Visibility = Visibility.Visible;
        ListKind.Text = kind;
        ListArtGlyph.Text = glyph;
        ListArtImage.Source = null;
        var url = CoverUrl(coverUri, 400);
        _listArtUrl = url;
        if (url.Length > 0) Covers.Load(url, 140, img => { if (_listArtUrl == url) ListArtImage.Source = img; });
    }
    string _listArtUrl = "";

    static string CoverUrl(string uri, int px) => Track.Img(uri, px);

    /// <summary>Появление страницы: прозрачность 0→1 и сдвиг на 10 px снизу, 180 мс.</summary>
    static void Enter(FrameworkElement el, double dy = 10)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(180);
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
        if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen) el.RenderTransform = tt = new TranslateTransform();
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, d) { EasingFunction = ease });
    }

    void Heart_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not Track t) return;
        _ = Like(t);
    }

    async Task Like(Track t)
    {
        try
        {
            await Ym.SetLike(t, !t.Liked);
            if (_likes != null)
            {
                if (t.Liked && !_likes.Contains(t)) _likes.Insert(0, t);
                else if (!t.Liked) _likes.Remove(t);
                if (_shownName == "Мне нравится") ShowList("Мне нравится", _likes);
            }
        }
        catch (Exception ex) { ShowToast("Лайк: " + ex.Message); }
    }

    // ---------------------------------------------------------------- волна: настройки
    static readonly (string id, string name)[] Scenes = { ("", "Обычная"), ("cs2", "CS2"), ("cs2phonk", "CS2 + фонк") };
    static readonly (string id, string name)[] Diversity = { ("", "Как обычно"), ("favorite", "Любимое"), ("discover", "Незнакомое"), ("popular", "Популярное") };
    static readonly (string id, string name)[] Moods = { ("", "Любое"), ("active", "Бодрое"), ("fun", "Весёлое"), ("calm", "Спокойное"), ("sad", "Грустное") };

    /// <summary>Обложка на волне подстраивается под высоту окна: в маленьком окне и с открытыми настройками — меньше.</summary>
    void FitHero()
    {
        Dispatcher.BeginInvoke(() =>
        {
            double avail = WavePage.ActualHeight - 76 - WaveContent.Margin.Top - WaveContent.Margin.Bottom;
            if (avail <= 0) return;
            double rest = 0;
            foreach (FrameworkElement c in WaveContent.Children)
                if (c != HeroArt && c.Visibility != Visibility.Collapsed) rest += c.DesiredSize.Height;
            rest += 28 - HeroLabel.Margin.Top; // считаем с обычным отступом подписи
            double s = Math.Min((avail - rest) / 220, 1.2);
            // места меньше чем на обложку ~130 px — убираем её совсем, а не показываем марку
            bool compact = s < 0.6;
            HeroArt.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            HeroLabel.Margin = new Thickness(0, compact ? 0 : 28, 0, 0);
            double cur = HeroArt.LayoutTransform is ScaleTransform st ? st.ScaleX : 1;
            if (!compact && Math.Abs(s - cur) > 0.01) HeroArt.LayoutTransform = new ScaleTransform(s, s);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void BuildWaveChips()
    {
        void Fill(WrapPanel panel, (string id, string name)[] items, Func<string> get, Action<string> set)
        {
            panel.Children.Clear();
            foreach (var (id, name) in items)
            {
                var b = new Button { Content = name, Margin = new Thickness(4), Padding = new Thickness(14, 6, 14, 6), FontSize = 12.5 };
                b.Style = (Style)FindResource(get() == id ? "PillOn" : "Pill");
                b.Click += async (_, _) =>
                {
                    if (get() == id) return;
                    set(id); _s.SaveSoon();
                    BuildWaveChips();
                    if (_pb.IsWave) await _pb.PlayWave(); // новая волна с другим характером
                };
                panel.Children.Add(b);
            }
        }
        Fill(WaveScene, Scenes, () => _s.WaveScene, v => { _s.WaveScene = v; _s.SceneBeforeCs2 = null; });
        Fill(WaveDiv, Diversity, () => _s.WaveDiversity, v => _s.WaveDiversity = v);
        Fill(WaveMood, Moods, () => _s.WaveMood, v => _s.WaveMood = v);
        // в режиме CS2 характер и настроение задаёт сам режим
        bool scene = _s.WaveScene.Length > 0;
        WaveDiv.Opacity = WaveMood.Opacity = scene ? 0.3 : 1;
        WaveDiv.IsEnabled = WaveMood.IsEnabled = !scene;
        AutoCs2.Content = (_s.AutoCs2 ? "\u2713 " : "") + "Включать режим CS2 при запуске игры";
        AutoCs2.Foreground = (Brush)FindResource(_s.AutoCs2 ? "Fg" : "Dim");
        HeroLabel.Text = _s.WaveScene switch { "cs2" => "МОЯ ВОЛНА \u00B7 CS2", "cs2phonk" => "МОЯ ВОЛНА \u00B7 CS2 + ФОНК", _ => "МОЯ ВОЛНА" };
    }

    // ---------------------------------------------------------------- сейчас играет
    void OnTrackChanged()
    {
        var t = _pb.Current;
        if (_nowTrack != null) _nowTrack.PropertyChanged -= NowTrackChanged;
        _nowTrack = t;
        if (t != null) t.PropertyChanged += NowTrackChanged;

        NowTitle.Text = t?.FullTitle ?? "Ничего не играет";
        NowArtist.Text = t?.Artists ?? (Ym.LoggedIn ? "Включите «Мою волну» или любой список" : "");
        Title = t != null ? $"{t.Title} — {t.Artists}" : "Noir";
        bool waveNow = _pb.IsWave && t != null;
        HeroTitle.Text = waveNow ? t!.FullTitle : "Бесконечный поток под ваш вкус";
        if (waveNow) HeroLabel.Text = _pb.QueueName.ToUpperInvariant(); else BuildWaveChips();
        HeroArtist.Text = waveNow ? t!.Artists : "Нажмите, чтобы начать";
        HeroDislike.Visibility = HeroNext.Visibility = waveNow ? Visibility.Visible : Visibility.Hidden;
        HeroCover.Source = null;
        HeroEmpty.Visibility = Visibility.Visible;
        if (waveNow) Covers.Load(t!.CoverUrl(400), 220, img =>
        {
            if (_pb.Current != t) return;
            HeroCover.Source = img;
            HeroEmpty.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
            if (img != null) HeroCover.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        });
        SetBackdrop(t);
        BtnDislike.Visibility = _pb.IsWave ? Visibility.Visible : Visibility.Collapsed;
        BtnDislike.ToolTip = HeroDislike.ToolTip = _pb.IsCs2Wave
            ? "Не для CS2 — больше не ставить этого артиста в CS2-волне"
            : "Не нравится — больше не ставить";
        PaintLike();
        NowCover.Source = null;
        if (t != null) Covers.Load(t.CoverUrl(200), 54, img => { if (_pb.Current == t) NowCover.Source = img; });
        DurText.Text = t != null ? t.Duration : "0:00";
        PosText.Text = "0:00";
        Seek.Value = 0; _seekTo = null;
    }

    bool _backA;
    string _backUrl = "";

    /// <summary>Фон окна — та же обложка 100px, растянутая и размытая; смена трека — плавный переход за 0,6 с.</summary>
    void SetBackdrop(Track? t)
    {
        var url = t?.CoverUrl(100) ?? "";
        if (url == _backUrl) return;
        _backUrl = url;
        var d = TimeSpan.FromMilliseconds(600);
        var (next, prev) = _backA ? (BackB, BackA) : (BackA, BackB);
        if (url.Length == 0) { prev.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d)); next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d)); return; }
        Covers.Load(url, 32, img =>
        {
            if (_backUrl != url || img == null) return;
            _backA = !_backA;
            next.Source = img;
            next.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
            prev.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        });
    }

    /// <summary>В CS2-волне 👎 значит «не для игры» (стоп-лист артистов), в остальных — обычный дизлайк Яндекса.</summary>
    async Task Dislike()
    {
        if (_pb.IsCs2Wave)
        {
            if (_pb.NotForCs2() is { } name) ShowToast($"{name} больше не будет в CS2-волне");
            return;
        }
        await _pb.DislikeCurrent();
    }

    void NowTrackChanged(object? s, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(Track.Liked)) PaintLike(); }

    void PaintLike()
    {
        bool liked = _pb.Current?.Liked == true;
        NowLike.Visibility = _pb.Current?.IsSc == true ? Visibility.Hidden : Visibility.Visible;
        NowLike.Content = liked ? "" : "";
        NowLike.Foreground = (Brush)FindResource(liked ? "Fg" : "Dim");
    }

    void OnStateChanged()
    {
        bool playing = _pb.IsPlaying;
        BtnPlay.Content = playing ? "" : "";
        WavePlay.Content = playing && _pb.IsWave ? "" : "";
        ListPlay.Content = playing && !_pb.IsWave && _pb.QueueName == _shownName ? "" : "";
        WaveArt.SetPlaying(playing && _pb.IsWave);
        EqBars.Playing = playing;
        UpdateTimer();
        UpdateCs2Watch();
        UpdatePosition();
    }

    void PaintModes()
    {
        BtnShuffle.Foreground = (Brush)FindResource(_s.Shuffle ? "Fg" : "Dim");
        BtnShuffle.Opacity = _s.Shuffle ? 1 : 0.7;
        BtnRepeat.Content = _s.Repeat == 2 ? "" : "";
        BtnRepeat.Foreground = (Brush)FindResource(_s.Repeat > 0 ? "Fg" : "Dim");
        BtnRepeat.Opacity = _s.Repeat > 0 ? 1 : 0.7;
        BtnRepeat.ToolTip = _s.Repeat switch { 1 => "Повтор списка", 2 => "Повтор трека", _ => "Повтор выключен" };
    }

    // ---------------------------------------------------------------- CS2
    readonly DispatcherTimer _cs2Timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    bool _cs2Seen;

    /// <summary>Смотрим на процесс cs2 раз в 5 с и только пока играет волна — иначе таймер стоит.</summary>
    void UpdateCs2Watch()
    {
        bool run = _s.AutoCs2 && _pb.IsWave && _pb.IsPlaying;
        if (run && !_cs2Timer.IsEnabled) { _cs2Timer.Start(); _ = CheckCs2(); }
        else if (!run && _cs2Timer.IsEnabled) _cs2Timer.Stop();
    }

    static bool Cs2Running()
    {
        var p = Process.GetProcessesByName("cs2");
        foreach (var x in p) x.Dispose();
        return p.Length > 0;
    }

    async Task CheckCs2()
    {
        bool on = Cs2Running();
        if (on == _cs2Seen) return;
        _cs2Seen = on;
        if (on && !_s.WaveScene.StartsWith("cs2"))
        {
            _s.SceneBeforeCs2 = _s.WaveScene;
            _s.WaveScene = "cs2";
            _s.SaveSoon();
            BuildWaveChips();
            ShowToast("CS2 запущена — волна в режиме CS2");
            await _pb.PlayWave();
        }
        else if (!on && _s.SceneBeforeCs2 != null)
        {
            _s.WaveScene = _s.SceneBeforeCs2;
            _s.SceneBeforeCs2 = null;
            _s.SaveSoon();
            BuildWaveChips();
            ShowToast("Вышли из CS2 — обычная волна");
            await _pb.PlayWave();
        }
    }

    /// <summary>Позицию обновляем 4 раза в секунду и только когда окно видно и играет — в трее таймер стоит.</summary>
    void UpdateTimer()
    {
        bool run = IsVisible && WindowState != WindowState.Minimized && _pb.IsPlaying;
        if (run && !_tick.IsEnabled) _tick.Start();
        else if (!run && _tick.IsEnabled) _tick.Stop();
    }

    void UpdatePosition()
    {
        if (_seeking) return;
        var d = _pb.Duration;
        var p = _pb.Position;
        if (_seekTo is { } to)
        {
            if (DateTime.UtcNow < _seekHold && Math.Abs((p - to).TotalSeconds) > 1.5) p = to;
            else _seekTo = null;
        }
        if (d > TimeSpan.Zero) Seek.Value = Math.Clamp(p.TotalMilliseconds / d.TotalMilliseconds, 0, 1);
        PosText.Text = Fmt(p);
        if (d > TimeSpan.Zero) DurText.Text = Fmt(d);
    }

    void SetVolume(double v)
    {
        v = Math.Clamp(v, 0, 1);
        _pb.Volume = v;
        _settingVol = true; Vol.Value = v; _settingVol = false;
        PaintVolume();
    }

    void PaintVolume()
    {
        double v = _pb.Volume;
        BtnMute.Content = v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
    }

    // ---------------------------------------------------------------- клавиши
    void OnKey(object sender, KeyEventArgs e)
    {
        bool typing = Keyboard.FocusedElement is TextBox;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        if (e.Key == Key.Space && !typing) { _pb.Toggle(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Right) { _pb.Next(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Left) { _pb.Prev(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { if (NavSearch.IsChecked == true) _ = Go(RootNav(NavSearch, ShowSearch), true); else NavSearch.IsChecked = true; e.Handled = true; }
        else if (ctrl && e.Key == Key.L) { _ = _pb.LikeCurrent(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Q) { ToggleQueue(); e.Handled = true; }
        else if (e.Key == Key.Escape && !typing && QueuePanel.Visibility == Visibility.Visible) { ToggleQueue(false); e.Handled = true; }
        else if (alt && e.Key == Key.Left || e.SystemKey == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) != 0) { _ = Back(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Up) { SetVolume(_pb.Volume + 0.05); e.Handled = true; }
        else if (ctrl && e.Key == Key.Down) { SetVolume(_pb.Volume - 0.05); e.Handled = true; }
        else if (e.Key == Key.Escape && typing) { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, this); e.Handled = true; }
    }

    // ---------------------------------------------------------------- окно и трей
    bool _quitting;
    public void Quit() { _quitting = true; SaveBounds(); Close(); }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void HideToTray()
    {
        SaveBounds();
        Hide();
        Covers.Clear();
        Native.TrimMemory();
    }

    void ApplyBounds()
    {
        Width = _s.Width; Height = _s.Height;
        if (!double.IsNaN(_s.Left) && !double.IsNaN(_s.Top)
            && _s.Left > SystemParameters.VirtualScreenLeft - 50 && _s.Top > SystemParameters.VirtualScreenTop - 50
            && _s.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && _s.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _s.Left; Top = _s.Top;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    void SaveBounds()
    {
        if (SnapMode || WindowState != WindowState.Normal) return;
        _s.Left = Left; _s.Top = Top; _s.Width = Width; _s.Height = Height;
        _s.Save();
    }

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        _toastTimer.Stop(); _toastTimer.Start();
    }

    // ---------------------------------------------------------------- мелочи
    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    static string Hours(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин" : $"{t.Minutes} мин";
    }

    public static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        string w = m10 == 1 && m100 != 11 ? one : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? few : many;
        return $"{n} {w}";
    }
}
