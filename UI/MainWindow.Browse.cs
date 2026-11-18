using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Player.Yandex;
using Track = Player.Yandex.Track;

namespace Player.UI;

/// <summary>Откуда играть строку списка: полный список (даже если показана только часть) и имя очереди.</summary>
public sealed record TrackSource(string Name, List<Track> List);

/// <summary>Строка панели «Очередь». Index — позиция в Playback.Queue.</summary>
public sealed class QueueItem
{
    public Track T { get; init; } = null!;
    public int Index { get; init; }
    public bool IsNow { get; init; }
    public bool Manual { get; init; }
}

/// <summary>Навигация с «назад», поиск по всему, страницы артиста и альбома, очередь, контекстное меню трека.</summary>
public partial class MainWindow
{
    // ================================================================ навигация
    readonly Stack<Func<Task>> _back = new();
    Func<Task>? _current;
    bool _quietNav;
    int _pageGen;
    string _browseMode = "";

    void InitBrowse()
    {
        NavWave.Checked += (_, _) => { if (!_quietNav) _ = Go(RootNav(NavWave, () => { ShowWave(); return Task.CompletedTask; }), true); };
        NavLikes.Checked += (_, _) => { if (!_quietNav) _ = Go(RootNav(NavLikes, ShowLikes), true); };
        NavSearch.Checked += (_, _) => { if (!_quietNav) _ = Go(RootNav(NavSearch, ShowSearch), true); };
        BtnBack.Click += async (_, _) => await Back();
        MouseUp += async (_, e) => { if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; await Back(); } };

        WireTrackList(Tracks);
        ListSub.Click += (_, _) => { if (ListSub.Tag is List<(string, string)> refs) OpenArtists(refs, ListSub); };
        NowTitleBtn.Click += (_, _) => { if (_pb.Current is { AlbumId.Length: > 0 } t) OpenAlbum(t.AlbumId); };
        NowArtistBtn.Click += (_, _) => { if (_pb.Current is { } t) OpenArtists(t.ArtistRefs, NowArtistBtn); };
        HeroArtistBtn.Click += (_, _) => { if (_pb.IsWave && _pb.Current is { } t) OpenArtists(t.ArtistRefs, HeroArtistBtn); };

        // источник поиска
        SrcYm.Click += (_, _) => SetSource("ym");
        SrcSc.Click += (_, _) => SetSource("sc");
        PaintSource();

        // очередь
        BtnQueue.Click += (_, _) => ToggleQueue();
        QueueClose.Click += (_, _) => ToggleQueue(false);
        _pb.QueueChanged += RefreshQueue;
        _pb.TrackChanged += RefreshQueue;
        WireQueueDrag();

        PreviewMouseRightButtonUp += OnRightClick;
    }

    /// <summary>Корневая страница (пункт меню слева): при возврате «назад» снова отмечаем её пункт, не вызывая обработчик.</summary>
    Func<Task> RootNav(RadioButton rb, Func<Task> show) => async () =>
    {
        if (rb.IsChecked != true) { _quietNav = true; rb.IsChecked = true; _quietNav = false; }
        await show();
    };

    async Task Go(Func<Task> show, bool root)
    {
        if (root) _back.Clear();
        else
        {
            if (_current != null) _back.Push(_current);
            UncheckNav();
        }
        _current = show;
        _pageGen++;
        BtnBack.Visibility = _back.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        await show();
    }

    async Task Back()
    {
        if (_back.Count == 0) return;
        _current = _back.Pop();
        _pageGen++;
        BtnBack.Visibility = _back.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        await _current();
    }

    void UncheckNav()
    {
        _quietNav = true;
        foreach (var rb in new[] { NavWave, NavLikes, NavSearch }.Concat(PlaylistNav.Children.OfType<RadioButton>())) rb.IsChecked = false;
        _quietNav = false;
    }

    void ShowPage(FrameworkElement page, bool force = false)
    {
        bool was = page.Visibility == Visibility.Visible;
        foreach (var p in new FrameworkElement[] { WavePage, ListPage, BrowsePage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        if (!was || force) Enter(page);
        _browseMode = page == BrowsePage ? _browseMode : "";
    }

    void OpenArtist(string id) { if (id.Length > 0) _ = Go(() => ShowArtist(id), false); }
    void OpenAlbum(string id) { if (id.Length > 0) _ = Go(() => ShowAlbum(id), false); }

    /// <summary>Один артист — сразу на страницу; несколько (фиты) — меню выбора.</summary>
    void OpenArtists(List<(string Id, string Name)> refs, FrameworkElement anchor)
    {
        var ok = refs.Where(r => r.Id.Length > 0).ToList();
        if (ok.Count == 0) return;
        if (ok.Count == 1) { OpenArtist(ok[0].Id); return; }
        var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (id, name) in ok) m.Items.Add(Item(name, "\uE77B", () => OpenArtist(id)));
        m.IsOpen = true;
    }

    // для --snap
    internal Task SnapArtist(string id) => Go(() => ShowArtist(id), false);
    internal Task SnapAlbum(string id) => Go(() => ShowAlbum(id), false);
    internal void SnapQueue(bool open) => ToggleQueue(open);
    internal void SnapSource(string src) => SetSource(src);

    // ================================================================ поиск
    void SetSource(string src)
    {
        if (_s.SearchSource == src) return;
        _s.SearchSource = src; _s.SaveSoon();
        PaintSource();
        _ = RunSearch(SearchText.Text.Trim());
        SearchText.Focus();
    }

    void PaintSource()
    {
        SrcYm.Style = (Style)FindResource(_s.SearchSource != "sc" ? "PillOn" : "Pill");
        SrcSc.Style = (Style)FindResource(_s.SearchSource == "sc" ? "PillOn" : "Pill");
        SearchHint.Text = _s.SearchSource == "sc" ? "Трек или артист на SoundCloud…" : "Трек, исполнитель, альбом…";
    }

    Ym.SearchResult? _lastSearch;
    string _lastSearchQ = "";

    async Task ShowSearch()
    {
        _s.Page = "search"; _s.SaveSoon();
        _browseMode = "search";
        SearchRow.Visibility = Visibility.Visible;
        ShowPage(BrowsePage);
        _browseMode = "search";
        _shownName = "";
        _ = Dispatcher.BeginInvoke(() => { SearchText.Focus(); SearchText.SelectAll(); }, DispatcherPriority.Input);
        var q = SearchText.Text.Trim();
        if (_lastSearch != null && _lastSearchQ == _s.SearchSource + q) RenderSearch(_lastSearch, q);
        else await RunSearch(q);
    }

    public async Task RunSearch(string q)
    {
        if (_browseMode != "search") return;
        _searchCts?.Cancel();
        if (q.Length == 0)
        {
            BrowseBody.Children.Clear();
            SetBrowseEmpty(_s.SearchSource == "sc" ? "Треки, артисты и плейлисты SoundCloud" : "Треки, артисты, альбомы и плейлисты Яндекс Музыки");
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            var src = _s.SearchSource;
            var res = src == "sc" ? await Sc.SearchAll(q, cts.Token) : await Ym.SearchAll(q, cts.Token);
            if (cts.IsCancellationRequested || _browseMode != "search" || src != _s.SearchSource) return;
            _lastSearch = res; _lastSearchQ = src + q;
            RenderSearch(res, q);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { BrowseBody.Children.Clear(); SetBrowseEmpty("Ошибка: " + e.Message); }
    }

    void RenderSearch(Ym.SearchResult r, string q)
    {
        BrowseBody.Children.Clear();
        if (r.Tracks.Count + r.Artists.Count + r.Albums.Count + r.Playlists.Count == 0) { SetBrowseEmpty("Ничего не нашлось"); return; }
        SetBrowseEmpty(null);
        var sections = new List<(string key, UIElement el)>();
        bool sc = _s.SearchSource == "sc";
        if (r.Tracks.Count > 0) sections.Add(("track", TrackSection("Треки", r.Tracks, (sc ? "SoundCloud: " : "Поиск: ") + q, 6)));
        if (r.Artists.Count > 0) sections.Add(("artist", Section("Артисты", Cards(r.Artists.Select(Card.Of), oneRow: true))));
        if (r.Albums.Count > 0) sections.Add(("album", Section(sc ? "Альбомы и плейлисты" : "Альбомы", Cards(r.Albums.Select(a => Card.Of(a)), oneRow: true))));
        if (r.Playlists.Count > 0) sections.Add(("playlist", Section("Плейлисты", Cards(r.Playlists.Select(Card.Of), oneRow: true))));
        // лучший результат — первым: ищешь «drake» — сначала артист
        foreach (var (_, el) in sections.OrderBy(s => s.key == r.BestType ? 0 : 1)) BrowseBody.Children.Add(el);
        if (BrowseBody.Children.Count > 0 && BrowseBody.Children[0] is FrameworkElement f) f.Margin = new Thickness(0, -8, 0, 0);
    }

    void SetBrowseEmpty(string? text)
    {
        BrowseEmpty.Text = text ?? "";
        BrowseEmpty.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ================================================================ артист
    async Task ShowArtist(string id)
    {
        int gen = _pageGen;
        SearchRow.Visibility = Visibility.Collapsed;
        ShowPage(BrowsePage, true);
        _browseMode = "artist";
        _shownName = "";
        BrowseBody.Children.Clear();
        SetBrowseEmpty("Загружаю…");
        BrowsePage.ScrollToTop();
        Ym.ArtistInfo a;
        bool sc = id.StartsWith("sc:");
        try { a = sc ? await Sc.ArtistPage(id) : await Ym.ArtistPage(id); }
        catch (Exception e) { if (gen == _pageGen) SetBrowseEmpty("Не загрузилось: " + e.Message); return; }
        if (gen != _pageGen) return;
        SetBrowseEmpty(null);

        BrowseBody.Children.Add(ArtistHeader(a));
        if (a.Popular.Count > 0) BrowseBody.Children.Add(TrackSection("Популярные треки", a.Popular, a.Artist.Name, 5));
        if (a.Albums.Count > 0) BrowseBody.Children.Add(Section("Альбомы", Cards(a.Albums.Select(x => Card.Of(x, false)))));
        if (a.Singles.Count > 0)
        {
            var cards = Cards(a.Singles.Take(10).Select(x => Card.Of(x, false)), oneRow: true);
            BrowseBody.Children.Add(Section(sc ? "Плейлисты" : "Синглы и EP", cards, a.Singles.Count > 4 ? "Показать все" : null, link =>
            {
                cards.ItemsSource = a.Singles.Select(x => Card.Of(x, false)).ToList();
                cards.MaxHeight = double.PositiveInfinity; cards.ClipToBounds = false;
                link.Visibility = Visibility.Collapsed;
            }));
        }
        if (a.Similar.Count > 0) BrowseBody.Children.Add(Section("Похожие артисты", Cards(a.Similar.Select(Card.Of), oneRow: true)));
    }

    FrameworkElement ArtistHeader(Ym.ArtistInfo a)
    {
        const double S = 150;
        var img = new Image { Stretch = Stretch.UniformToFill, Clip = new EllipseGeometry(new Point(S / 2, S / 2), S / 2, S / 2) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var url = CoverUrl(a.Artist.CoverUri, 400);
        if (url.Length > 0) Covers.Load(url, (int)S, i => img.Source = i);
        var art = new Grid { Width = S, Height = S, Margin = new Thickness(0, 0, 28, 0) };
        art.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(S / 2), Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18)),
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 12, Direction = 270, Opacity = 0.6, Color = Colors.Black },
        });
        art.Children.Add(img);

        var play = new Button { Style = (Style)FindResource("PlayBtn"), Width = 46, Height = 46, FontSize = 16, Content = "\uE768", Margin = new Thickness(0, 0, 12, 0), ToolTip = "Популярные треки" };
        bool sc = a.Artist.Id.StartsWith("sc:");
        play.Click += (_, _) => { if (a.Popular.Count > 0) _pb.PlayList(a.Popular, 0, a.Artist.Name); };
        var wave = new Button { Style = (Style)FindResource("Pill"), Margin = new Thickness(0, 0, 16, 0),
                                ToolTip = sc ? "Похожие треки на SoundCloud" : "Бесконечная волна в духе этого артиста" };
        wave.Content = IconText("\uE8D6", sc ? "Станция" : "Волна по артисту");
        wave.Click += async (_, _) =>
        {
            if (!sc) { await _pb.PlayWave(new[] { "artist:" + a.Artist.Id }, "Волна · " + a.Artist.Name); return; }
            if (a.Popular.FirstOrDefault(t => t.Available) is { } seed) await PlayStation(seed, a.Artist.Name);
        };
        var info = new List<string>();
        if (a.Artist.Genre.Length > 0) info.Add(Genre(a.Artist.Genre));
        if (a.Artist.TrackCount > 0) info.Add(Plural(a.Artist.TrackCount, "трек", "трека", "треков"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 2) };
        buttons.Children.Add(play);
        buttons.Children.Add(wave);
        buttons.Children.Add(new TextBlock { Text = string.Join(" · ", info), Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        text.Children.Add(new TextBlock { Text = sc ? "АРТИСТ · SOUNDCLOUD" : "АРТИСТ", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Dim") });
        text.Children.Add(new TextBlock
        {
            Text = a.Artist.Name, FontSize = 44, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        });
        text.Children.Add(buttons);

        var dp = new DockPanel();
        DockPanel.SetDock(art, Dock.Left);
        dp.Children.Add(art);
        dp.Children.Add(text);
        return dp;
    }

    static string Genre(string g) => g switch
    {
        "foreignrap" => "Зарубежный рэп", "rusrap" => "Русский рэп", "rap" => "Рэп", "pop" => "Поп", "ruspop" => "Русская поп-музыка",
        "rock" => "Рок", "electronics" => "Электроника", "dance" => "Танцевальная", "rnb" => "R&B", "indie" => "Инди",
        "alternative" => "Альтернатива", "phonkgenre" => "Фонк", "metal" => "Метал", "jazz" => "Джаз", "soundtrack" => "Саундтреки",
        _ => g,
    };

    // ================================================================ альбом
    async Task ShowAlbum(string id)
    {
        int gen = _pageGen;
        SetListArt("АЛЬБОМ", "\uE93C", "");
        ShowList("…", new List<Track>(), "Загружаю…", "album:" + id);
        try
        {
            var (al, tracks) = id.StartsWith("sc:") ? await Sc.PlaylistPage(id) : await Ym.AlbumPage(id);
            if (gen != _pageGen) return;
            SetListArt(al.Kind.ToUpperInvariant() + (al.Year.Length > 0 ? " · " + al.Year : ""), "\uE93C", al.CoverUri);
            ShowList(al.Title, tracks, tracks.Count == 0 ? "Пусто" : null, al.Title);
            ListSub.Content = new TextBlock { Text = al.Artists, TextTrimming = TextTrimming.CharacterEllipsis };
            ListSub.Tag = al.ArtistRefs;
            ListSub.Visibility = al.Artists.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception e) { if (gen == _pageGen) ShowList("Альбом", new List<Track>(), "Не загрузилось: " + e.Message); }
    }

    // ================================================================ блоки страниц
    StackPanel Section(string title, UIElement content, string? linkText = null, Action<Button>? link = null)
    {
        var head = new DockPanel { Margin = new Thickness(0, 34, 16, 10) };
        if (linkText != null && link != null)
        {
            var b = new Button { Style = (Style)FindResource("Link"), Content = linkText, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Bottom };
            b.Click += (_, _) => link(b);
            DockPanel.SetDock(b, Dock.Right);
            head.Children.Add(b);
        }
        head.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") });
        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(content);
        return sp;
    }

    /// <summary>Треки секцией: показываем первые <paramref name="shown"/>, «Показать все» раскрывает; играть — всегда весь список.</summary>
    StackPanel TrackSection(string title, List<Track> list, string queueName, int shown)
    {
        var lb = TrackBox(list.Count > shown ? list.Take(shown).ToList() : list, new TrackSource(queueName, list));
        return Section(title, lb, list.Count > shown ? $"Показать все ({list.Count})" : null, b =>
        {
            bool all = lb.Items.Count < list.Count;
            lb.ItemsSource = all ? list : list.Take(shown).ToList();
            b.Content = all ? "Свернуть" : $"Показать все ({list.Count})";
        });
    }

    ListBox TrackBox(List<Track> items, TrackSource src)
    {
        var lb = new ListBox
        {
            ItemsSource = items, Tag = src, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
            ItemTemplate = (DataTemplate)FindResource("TrackRow"), ItemContainerStyle = (Style)FindResource("Row"),
            AlternationCount = 100000, Margin = new Thickness(-10, 0, 16, 0),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(lb, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(lb, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(lb, false);
        // колесо крутит всю страницу, а не застревает в списке
        lb.PreviewMouseWheel += (_, e) => { e.Handled = true; BrowsePage.ScrollToVerticalOffset(BrowsePage.VerticalOffset - e.Delta); };
        WireTrackList(lb);
        return lb;
    }

    ItemsControl Cards(IEnumerable<Card> cards, bool oneRow = false)
    {
        var ic = new ItemsControl
        {
            ItemsSource = cards.ToList(), ItemTemplate = (DataTemplate)FindResource("CardTpl"),
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel))),
            Margin = new Thickness(-10, 0, 0, 0), Focusable = false,
        };
        if (oneRow) { ic.MaxHeight = 224; ic.ClipToBounds = true; }
        return ic;
    }

    StackPanel IconText(string glyph, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), Margin = new Thickness(0, 1, 8, 0) });
        sp.Children.Add(new TextBlock { Text = text });
        return sp;
    }

    void Card_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Card c) return;
        switch (c.Item)
        {
            case Artist a: OpenArtist(a.Id); break;
            case Album al: OpenAlbum(al.Id); break;
            case Playlist p: _ = Go(() => ShowPlaylist(p), false); break;
        }
    }

    // ================================================================ строки треков
    void WireTrackList(ListBox lb)
    {
        lb.MouseDoubleClick += (_, e) =>
        {
            if (lb.SelectedItem is Track t && Ancestor<ButtonBase>(e.OriginalSource as DependencyObject) == null) PlayFrom(lb, t);
        };
        lb.KeyDown += (_, e) => { if (e.Key == Key.Enter && lb.SelectedItem is Track t) { PlayFrom(lb, t); e.Handled = true; } };
    }

    void PlayFrom(ListBox lb, Track t)
    {
        if (lb.Tag is not TrackSource src) return;
        int i = src.List.IndexOf(t);
        if (i < 0) return;
        if (_pb.Current == t && _pb.QueueName == src.Name) { _pb.Toggle(); return; }
        _pb.PlayList(src.List, i, src.Name);
    }

    void RowPlay_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not Track t) return;
        if (_pb.Current == t) { _pb.Toggle(); return; }
        if (Ancestor<ListBox>(sender as DependencyObject) is { } lb) PlayFrom(lb, t);
    }

    void RowArtist_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is Track t) OpenArtists(t.ArtistRefs, (FrameworkElement)sender);
    }

    void RowAlbum_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is Track { AlbumId.Length: > 0 } t) OpenAlbum(t.AlbumId);
    }

    static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ================================================================ контекстное меню трека
    void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        var fe = e.OriginalSource as FrameworkElement;
        var (t, qi) = fe?.DataContext switch
        {
            Track x => (x, (QueueItem?)null),
            QueueItem q => (q.T, q),
            _ => ((Track?)null, (QueueItem?)null),
        };
        if (t == null) return;
        e.Handled = true;
        var m = new ContextMenu { PlacementTarget = fe, Placement = PlacementMode.MousePoint };
        if (qi == null || !qi.IsNow)
        {
            m.Items.Add(Item("Играть следующим", "\uE893", () => { _pb.PlayNext(t); ShowToast("Будет следующим"); }, t.Available));
            m.Items.Add(Item("Добавить в очередь", "\uE710", () => { _pb.AddToQueue(t); ShowToast("Добавлено в очередь"); }, t.Available));
        }
        if (qi is { IsNow: false }) m.Items.Add(Item("Убрать из очереди", "\uE711", () => _pb.RemoveAt(qi.Index)));
        m.Items.Add(Item(t.IsSc ? "Станция по треку" : "Волна по треку", "\uE8D6", () =>
        {
            if (t.IsSc) _ = PlayStation(t, t.FullTitle);
            else _ = _pb.PlayWave(new[] { "track:" + t.Id }, "Волна · " + t.FullTitle);
        }, t.Available));
        if (!t.IsSc) m.Items.Add(new Separator());
        if (!t.IsSc) m.Items.Add(Item(t.Liked ? "Убрать из «Мне нравится»" : "Мне нравится", t.Liked ? "\uEB52" : "\uEB51", () => _ = Like(t)));
        var artists = t.ArtistRefs.Where(r => r.Id.Length > 0).ToList();
        if (artists.Count > 0 || t.AlbumId.Length > 0) m.Items.Add(new Separator());
        foreach (var (id, name) in artists.Take(4)) m.Items.Add(Item(name, "\uE77B", () => OpenArtist(id)));
        if (t.AlbumId.Length > 0 && t.Album.Length > 0) m.Items.Add(Item(t.Album, "\uE93C", () => OpenAlbum(t.AlbumId)));
        m.IsOpen = true;
    }

    /// <summary>Станция SoundCloud: трек и похожие на него (у SoundCloud нет бесконечной волны, 50 похожих — хватает на пару часов).</summary>
    async Task PlayStation(Track seed, string name)
    {
        try
        {
            var rel = await Sc.Related(seed);
            var list = new List<Track> { seed };
            list.AddRange(rel.Where(x => x != seed));
            _pb.PlayList(list, 0, "Станция · " + name);
        }
        catch (Exception e) { ShowToast("Станция: " + e.Message); }
    }

    static MenuItem Item(string text, string glyph, Action click, bool enabled = true)
    {
        var mi = new MenuItem { Header = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 }, Tag = glyph, IsEnabled = enabled };
        mi.Click += (_, _) => click();
        return mi;
    }

    // ================================================================ очередь
    void ToggleQueue(bool? show = null)
    {
        bool open = show ?? QueuePanel.Visibility != Visibility.Visible;
        if (open == (QueuePanel.Visibility == Visibility.Visible)) return;
        BtnQueue.Foreground = (Brush)FindResource(open ? "Fg" : "Dim");
        if (!open) { QueuePanel.Visibility = Visibility.Collapsed; QueueList.ItemsSource = null; QueueNow.ItemsSource = null; return; }
        QueuePanel.Visibility = Visibility.Visible;
        RefreshQueue();
        var tt = QueuePanel.RenderTransform as TranslateTransform ?? new TranslateTransform();
        if (tt.IsFrozen) { tt = new TranslateTransform(); }
        QueuePanel.RenderTransform = tt;
        var d = TimeSpan.FromMilliseconds(200);
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        QueuePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
    }

    /// <summary>Перерисовка только при открытой панели — закрытая очередь ничего не стоит.</summary>
    void RefreshQueue()
    {
        if (QueuePanel.Visibility != Visibility.Visible) return;
        var q = _pb.Queue;
        int i = _pb.Index;
        QueueNow.ItemsSource = _pb.Current is { } cur ? new[] { new QueueItem { T = cur, Index = i, IsNow = true } } : null;
        var next = new List<QueueItem>();
        for (int k = i + 1; k < q.Count && next.Count < 500; k++) next.Add(new QueueItem { T = q[k], Index = k, Manual = _pb.IsManual(q[k]) });
        QueueList.ItemsSource = next;
        QueueFrom.Text = _pb.QueueName.Length > 0 ? $"ДАЛЕЕ · {_pb.QueueName.ToUpperInvariant()}" : "ДАЛЕЕ";
        QueueEmpty.Visibility = next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is QueueItem qi) _pb.RemoveAt(qi.Index);
    }

    /// <summary>Двойной клик — играть с этого места; перетаскивание — поменять порядок.</summary>
    void WireQueueDrag()
    {
        Point start = default;
        QueueItem? pressed = null;
        QueueList.MouseDoubleClick += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is QueueItem qi && Ancestor<ButtonBase>(e.OriginalSource as DependencyObject) == null)
                _pb.PlayAt(qi.Index);
        };
        QueueNow.MouseDoubleClick += (_, _) => _pb.Toggle();
        QueueList.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = Ancestor<ButtonBase>(e.OriginalSource as DependencyObject) == null ? (e.OriginalSource as FrameworkElement)?.DataContext as QueueItem : null;
            start = e.GetPosition(QueueList);
        };
        QueueList.PreviewMouseMove += (_, e) =>
        {
            if (pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(QueueList);
            if (Math.Abs(p.Y - start.Y) < 6 && Math.Abs(p.X - start.X) < 6) return;
            var item = pressed;
            pressed = null;
            DragDrop.DoDragDrop(QueueList, item, DragDropEffects.Move);
        };
        QueueList.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(QueueItem)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        QueueList.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(QueueItem)) is not QueueItem from) return;
            int to = (e.OriginalSource as FrameworkElement)?.DataContext is QueueItem target ? target.Index : _pb.Queue.Count - 1;
            _pb.Move(from.Index, to);
        };
    }
}
