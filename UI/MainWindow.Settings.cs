using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using Island.Core;
using Player.Core;
using Player.Yandex;

namespace Player.UI;

/// <summary>Страница «Настройки». Всё применяется сразу, без кнопки «Сохранить».</summary>
public partial class MainWindow
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    Task ShowSettings()
    {
        BuildSettings();
        ShowPage(SettingsPage);
        return Task.CompletedTask;
    }

    void BuildSettings()
    {
        var body = SettingsBody;
        body.Children.Clear();
        body.Children.Add(new TextBlock
        {
            Text = "Настройки", FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontSize = 38, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 18),
        });

        body.Children.Add(Group("Оформление",
            SRow("Цвета", "Как тема, выбранная в Island, под обложку каждого трека или чёрно-белые",
                Segmented(NoirTheme.Modes, NoirTheme.Mode, NoirTheme.SetMode)),
            SRow("Цветные обложки", "Выключите — обложки станут серыми, в тон интерфейсу",
                Switch(!_s.GrayCovers, on =>
                {
                    _s.GrayCovers = !on; _s.SaveSoon();
                    Covers.Color = on; Covers.Clear();
                    _backUrl = ""; OnTrackChanged();      // обложка сейчас и фон
                    if (_current != null) _ = _current(); // страница с обложками
                    ShowPage(SettingsPage);
                }))));

        body.Children.Add(Group("Звук",
            SRow("Выравнивать громкость", "Громкие треки приглушаются, тихие подтягиваются — громкость не скачет между треками",
                Switch(_pb.Normalize, on => _pb.Normalize = on)),
            SRow("Качество Яндекс Музыки", "Ниже — быстрее старт и меньше трафика; SoundCloud всегда 160 кбит/с",
                Segmented(new[] { ("128", "128"), ("192", "192"), ("320", "320 кбит/с") }, _s.MaxKbps.ToString(),
                    v => { _s.MaxKbps = int.Parse(v); _s.SaveSoon(); }))));

        // стоп-лист CS2: снять артиста — крестик
        var stop = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void FillStop()
        {
            stop.Children.Clear();
            if (_s.Cs2Exclude.Count == 0)
                stop.Children.Add(new TextBlock { Text = "пусто — 👎 в режиме CS2 добавляет сюда артиста", Foreground = (Brush)FindResource("Faint"), FontSize = 12.5 });
            foreach (var name in _s.Cs2Exclude.ToList())
            {
                var b = new Button { Style = (Style)FindResource("Pill"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 5, 10, 5), ToolTip = "Вернуть в CS2-волну" };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = name, FontSize = 12.5 });
                sp.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 9, Margin = new Thickness(8, 1, 0, 0), Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center });
                b.Content = sp;
                b.Click += (_, _) => { _s.Cs2Exclude.Remove(name); _s.SaveSoon(); FillStop(); };
                stop.Children.Add(b);
            }
        }
        FillStop();
        body.Children.Add(Group("Моя волна",
            SRow("Режим CS2 при запуске игры", "Когда запускается Counter-Strike 2, волна сама переключается на хайп-трэп, после игры — обратно",
                Switch(_s.AutoCs2, on => { _s.AutoCs2 = on; _s.SaveSoon(); BuildWaveChips(); UpdateCs2Watch(); })),
            SRow("Не ставить в режиме CS2", "Артисты, которые мешают играть", null, stop)));

        body.Children.Add(Group("Поиск",
            SRow("Где искать", "Можно переключить и прямо на странице поиска",
                Segmented(new[] { ("ym", "Яндекс Музыка"), ("sc", "SoundCloud") }, _s.SearchSource, SetSource))));

        body.Children.Add(Group("Система",
            SRow("Запускать вместе с Windows", "Тихо, сразу в трей — медиаклавиши и Island работают с первой минуты",
                Switch(IsAutostart(), SetAutostart)),
            SRow("Горячие клавиши", "Ctrl+F — поиск · Ctrl+Q — очередь · Alt+← — назад · Esc — закрыть очередь. Медиаклавиши и Alt+Q (лаунчер: «пауза», «дальше», «кс2») — всегда", null)));

        var memText = new TextBlock { Foreground = (Brush)FindResource("Dim"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        var cacheText = new TextBlock { Foreground = (Brush)FindResource("Dim"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        void Measure()
        {
            using var p = Process.GetCurrentProcess();
            memText.Text = $"Сейчас занято {p.PrivateMemorySize64 / 1048576.0:0} МБ. В трее плеер сам отпускает обложки и страницы";
            cacheText.Text = $"Обложки на диске: {DirMb(Path.Combine(PlayerSettings.CacheDir, "covers")):0.#} МБ — с ними списки открываются мгновенно";
        }
        Measure();
        body.Children.Add(Group("Память",
            SRow("Оперативная память", null, Button("Освободить", () => { FreeMemory(deep: false); Measure(); }), memText),
            SRow("Кэш обложек", null, Button("Очистить", () =>
            {
                try { foreach (var f in Directory.EnumerateFiles(Path.Combine(PlayerSettings.CacheDir, "covers"))) File.Delete(f); } catch { }
                Covers.Clear();
                Measure();
            }), cacheText)));

        body.Children.Add(Group("Аккаунт",
            SRow(Ym.LoggedIn ? (Ym.Login.Length > 0 ? Ym.Login : "Яндекс") : "Вход не выполнен",
                "Вход общий с Island: войти или выйти можно в его настройках, раздел «Яндекс Музыка»", null)));

        var ver = typeof(MainWindow).Assembly.GetName().Version;
        body.Children.Add(Group("О программе",
            SRow($"Noir {ver?.Major}.{ver?.Minor}.{ver?.Build}", "Яндекс Музыка и SoundCloud · звук через системный плеер Windows",
                Button("Папка настроек", () => { try { Process.Start("explorer.exe", PlayerSettings.Dir)?.Dispose(); } catch { } }))));
    }

    // ---------------------------------------------------------------- построители
    FrameworkElement Group(string title, params FrameworkElement[] rows)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        sp.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(4, 0, 0, 8) });
        var card = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) card.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x12, 255, 255, 255)), Margin = new Thickness(18, 0, 18, 0) });
            card.Children.Add(rows[i]);
        }
        sp.Children.Add(new Border
        {
            Child = card, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x0C, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)),
        });
        return sp;
    }

    FrameworkElement SRow(string title, string? hint, FrameworkElement? control, FrameworkElement? below = null)
    {
        var dock = new DockPanel { Margin = new Thickness(18, 14, 18, 14), LastChildFill = true };
        if (control != null)
        {
            DockPanel.SetDock(control, Dock.Right);
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(20, 0, 0, 0);
            dock.Children.Add(control);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        if (hint != null)
            text.Children.Add(new TextBlock { Text = hint, FontSize = 12.5, Foreground = (Brush)FindResource("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        if (below != null) { below.Margin = new Thickness(0, 6, 0, 0); text.Children.Add(below); }
        dock.Children.Add(text);
        return dock;
    }

    ToggleButton Switch(bool on, Action<bool> changed)
    {
        var t = new ToggleButton { Style = (Style)FindResource("Switch"), IsChecked = on };
        t.Checked += (_, _) => changed(true);
        t.Unchecked += (_, _) => changed(false);
        return t;
    }

    FrameworkElement Segmented((string id, string name)[] items, string current, Action<string> pick)
    {
        var wp = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 420 };
        void Paint()
        {
            foreach (Button b in wp.Children) b.Style = (Style)FindResource((string)b.Tag == current ? "PillOn" : "Pill");
        }
        foreach (var (id, name) in items)
        {
            var b = new Button { Content = name, Tag = id, Margin = new Thickness(6, 3, 0, 3), Padding = new Thickness(13, 6, 13, 6), FontSize = 12.5 };
            b.Click += (_, _) => { if (current == id) return; current = id; Paint(); pick(id); };
            wp.Children.Add(b);
        }
        Paint();
        return wp;
    }

    Button Button(string text, Action click)
    {
        var b = new Button { Style = (Style)FindResource("Pill"), Content = text, Padding = new Thickness(14, 6, 14, 6), FontSize = 12.5 };
        b.Click += (_, _) => click();
        return b;
    }

    // ---------------------------------------------------------------- автозапуск
    static bool IsAutostart()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Noir") is string; }
        catch { return false; }
    }

    static void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (on) k.SetValue("Noir", $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue("Noir", false);
        }
        catch { }
    }

    static double DirMb(string dir)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles().Sum(f => f.Length) / 1048576.0 : 0; }
        catch { return 0; }
    }
}
