using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Island.Core;
using Island.UI;
using Player.Audio;

namespace Player.UI;

/// <summary>Меню в трее: что играет, управление и пара пунктов — чёрно-белая карточка над значком.</summary>
public sealed class TrayMenu : Window
{
    static TrayMenu? _open;
    readonly Border _card;
    bool _closing;

    public static void Toggle(Playback pb, Action open, Action quit)
    {
        if (_open != null) { _open.CloseAnimated(); return; }
        _open = new TrayMenu(pb, open, quit);
        _open.Closed += (_, _) => _open = null;
        _open.Popup();
    }

    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static Brush R(string k) => (Brush)Application.Current.Resources[k];

    TrayMenu(Playback pb, Action open, Action quit)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true; Opacity = 0; Left = -20000; Top = -20000;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = R("Fg");

        var stack = new StackPanel { Width = 290 };

        // что играет
        var t = pb.Current;
        var cover = new Image { Stretch = Stretch.UniformToFill };
        var coverBox = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(6), Background = R("Hover"), ClipToBounds = true, Child = cover };
        if (t != null) Covers.Load(t.CoverUrl(200), 52, img => cover.Source = img);
        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = t?.FullTitle ?? "Ничего не играет", FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = t?.Artists ?? "Noir", FontSize = 12.5, Foreground = R("Dim"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        var head = new DockPanel { Margin = new Thickness(14, 14, 14, 8) };
        head.Children.Add(coverBox);
        head.Children.Add(texts);
        stack.Children.Add(head);

        // управление
        var play = Btn(pb.IsPlaying ? "" : "", 40, true);
        play.MouseLeftButtonUp += (_, _) => { pb.Toggle(); ((TextBlock)play.Child).Text = pb.IsPlaying ? "" : ""; };
        var prev = Btn("", 36, false); prev.MouseLeftButtonUp += (_, _) => { pb.Prev(); CloseAnimated(); };
        var next = Btn("", 36, false); next.MouseLeftButtonUp += (_, _) => { pb.Next(); CloseAnimated(); };
        var like = Btn(t?.Liked == true ? "" : "", 36, false);
        like.MouseLeftButtonUp += async (_, _) => { await pb.LikeCurrent(); ((TextBlock)like.Child).Text = pb.Current?.Liked == true ? "" : ""; };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 12) };
        foreach (var b in new[] { like, prev, play, next }) { b.Margin = new Thickness(6, 0, 6, 0); controls.Children.Add(b); }
        // для симметрии — пустое место справа той же ширины, что и лайк
        controls.Children.Add(new Border { Width = 36, Margin = new Thickness(6, 0, 6, 0) });
        stack.Children.Add(controls);

        // выравнивание громкости: галочка переключается прямо в меню
        var norm = Row(pb.Normalize ? "" : "", "Выравнивать громкость", () => { });
        norm.MouseLeftButtonUp += (_, _) =>
        {
            pb.Normalize = !pb.Normalize;
            ((TextBlock)((DockPanel)norm.Child).Children[0]).Text = pb.Normalize ? "" : "";
        };
        norm.ToolTip = "Громкие треки приглушаются, тихие подтягиваются — громкость не скачет";

        // цвета: по клику — следующий режим (как в Island → под обложку → чёрно-белые)
        static string ColorsText() => "Цвета: " + Array.Find(Player.Core.NoirTheme.Modes, m => m.id == Player.Core.NoirTheme.Mode).name.ToLowerInvariant().Replace("island", "Island");
        var colors = Row("", ColorsText(), () => { });
        colors.MouseLeftButtonUp += (_, _) =>
        {
            var modes = Player.Core.NoirTheme.Modes;
            int i = Array.FindIndex(modes, m => m.id == Player.Core.NoirTheme.Mode);
            Player.Core.NoirTheme.SetMode(modes[(i + 1) % modes.Length].id);
            ((TextBlock)((DockPanel)colors.Child).Children[1]).Text = ColorsText();
        };
        colors.ToolTip = "Как в Island — тема из настроек острова; под обложку — цвета каждого трека";
        stack.Children.Add(Section(norm, colors));

        stack.Children.Add(Section(
            Row("", "Открыть плеер", () => { CloseAnimated(); open(); }),
            Row("", "Выйти", () => { CloseAnimated(); quit(); })));

        _card = new Border
        {
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(18, 14, 18, 22),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2A, 255, 255, 255)), Background = R("Side"),
            Child = stack, RenderTransform = new TranslateTransform(0, 10),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.6, Color = Colors.Black },
        };
        Content = _card;
        Deactivated += (_, _) => CloseAnimated();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) CloseAnimated(); };
    }

    static Border Btn(string glyph, double size, bool primary)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Cursor = Cursors.Hand,
            Background = primary ? R("Fg") : Brushes.Transparent,
            Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = primary ? 15 : 14, Foreground = primary ? new SolidColorBrush(Color.FromRgb(10, 10, 10)) : R("Fg"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        if (!primary)
        {
            b.MouseEnter += (_, _) => b.Background = R("Sel");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        }
        return b;
    }

    static FrameworkElement Section(params FrameworkElement[] items)
    {
        var p = new StackPanel { Margin = new Thickness(6, 4, 6, 6) };
        foreach (var i in items) p.Children.Add(i);
        return new Border { Child = p, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = R("Line") };
    }

    static Border Row(string glyph, string text, Action click)
    {
        var dock = new DockPanel();
        dock.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Width = 30, Foreground = R("Dim"), VerticalAlignment = VerticalAlignment.Center });
        dock.Children.Add(new TextBlock { Text = text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        var b = new Border { Child = dock, Height = 36, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        b.MouseEnter += (_, _) => b.Background = R("Sel");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    void Popup()
    {
        Show();
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetCursorPos(out var pt);
        var (_, work, scale, _) = Native.MonitorOf(Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST));
        int w = (int)Math.Round(ActualWidth * scale), h = (int)Math.Round(ActualHeight * scale);
        int x = Math.Clamp(pt.X - w / 2, work.Left, work.Left + work.W - w);
        int y = pt.Y > work.Top + work.H / 2 ? work.Top + work.H - h + (int)(10 * scale) : Math.Max(work.Top, pt.Y - (int)(8 * scale));
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
        Activate();
        Native.SetForegroundWindow(hwnd);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Theme.Ms(120)));
        _card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, Theme.Ms(280)) { EasingFunction = Theme.Spring(0.8, 7.5) });
    }

    void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        var a = new DoubleAnimation(Opacity, 0, Theme.Ms(90));
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
    }
}
