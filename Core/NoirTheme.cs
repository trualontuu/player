using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Player.Core;

/// <summary>
/// Цвета Noir. Три режима (PlayerSettings.ColorMode):
///   island — тема из настроек Island (меняется вместе с ним);
///   cover  — палитра из обложки играющего трека, перестраивается на каждом треке;
///   mono   — исходный чёрно-белый.
/// Все цвета — изменяемые кисти в ресурсах приложения (DynamicResource), смена идёт плавной анимацией.
/// </summary>
public static class NoirTheme
{
    public sealed record Palette(Color Bg, Color Accent, Color Dim, Color Track);

    public static readonly Palette Mono = new(C(0x0A0A0A), C(0xF2F2F2), C(0x8C8C8C), C(0x2A2A2A));

    public static readonly (string id, string name)[] Modes = { ("island", "Как в Island"), ("cover", "Под обложку"), ("mono", "Чёрно-белые") };

    // текущие и целевые цвета; кисти в ресурсах приложения заморожены, поэтому переход —
    // подмена кистей новыми с промежуточным цветом (~500 мс на CompositionTarget.Rendering)
    static readonly Dictionary<string, Color> _cur = new(), _from = new(), _to = new(), _pushed = new();
    static DateTime _t0;
    static bool _animating;
    static PlayerSettings? _s;
    static BitmapSource? _cover;
    static FileSystemWatcher? _w;
    static DispatcherTimer? _debounce;

    /// <summary>Тест: подменить тему Island.</summary>
    public static string? IslandOverride;

    static readonly string[] Keys = { "Bg", "Side", "Bar", "Line", "Hover", "Sel", "Dim", "Faint", "Track", "Accent", "OnAccent", "AccentSoft", "SideGlass", "BarGlass", "PanelBg", "MenuBg", "Tile" };

    /// <summary>До создания окон: кисти в ресурсы приложения и первая палитра без анимации.</summary>
    public static void Init(PlayerSettings s)
    {
        _s = s;
        Refresh(animate: false);
    }

    /// <summary>Текущий цвет палитры в виде #RRGGBB (для виджета OBS).</summary>
    public static string Hex(string key) => _cur.TryGetValue(key, out var c) ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "";

    public static string Mode => _s?.ColorMode ?? "island";

    public static void SetMode(string mode)
    {
        if (_s == null) return;
        _s.ColorMode = mode;
        _s.SaveSoon();
        Refresh(animate: true);
    }

    /// <summary>Сменилась обложка (маленькая, для фона) — в режиме «под обложку» перекрашиваемся.</summary>
    public static void OnCover(BitmapSource? img)
    {
        _cover = img;
        if (Mode == "cover") Refresh(animate: true);
    }

    static void Refresh(bool animate)
    {
        var p = Mode switch
        {
            "island" => FromIsland(),
            "cover" => (_cover != null ? FromCover(_cover) : null) ?? Mono,
            _ => Mono,
        };
        Apply(p, animate);
        if (Mode == "island") WatchIsland(); else StopWatch();
    }

    // ------------------------------------------------------------------ палитра → кисти
    static void Apply(Palette p, bool animate)
    {
        var bg = p.Bg;
        var t = new Dictionary<string, Color>
        {
            ["Bg"] = bg,
            ["Side"] = Mix(bg, White, 0.03),
            ["Bar"] = Mix(bg, White, 0.03),
            ["Line"] = Mix(bg, White, 0.09),
            ["Hover"] = Mix(bg, White, 0.06),
            ["Sel"] = Mix(bg, White, 0.10),
            ["Dim"] = p.Dim,
            ["Faint"] = Mix(p.Dim, bg, 0.5),
            ["Track"] = p.Track,
            ["Accent"] = p.Accent,
            ["OnAccent"] = Luma(p.Accent) > 0.62 ? C(0x0A0A0A) : C(0xFFFFFF),
            ["AccentSoft"] = A(p.Accent, 0x40),
            ["SideGlass"] = A(bg, 0xB8),
            ["BarGlass"] = A(bg, 0xE0),
            ["PanelBg"] = A(Mix(bg, White, 0.03), 0xF5),
            ["MenuBg"] = Mix(bg, White, 0.08),
            ["Tile"] = Mix(bg, White, 0.05),
            ["Shade0"] = A(bg, 0xB8),
            ["Shade1"] = A(bg, 0xDA),
            ["Shade2"] = A(bg, 0xF8),
        };
        UI.WaveVisual.Tint = p.Accent;
        if (!animate || _cur.Count == 0)
        {
            foreach (var (k, c) in t) _cur[k] = c;
            Push();
            return;
        }
        foreach (var (k, c) in t) { _from[k] = _cur.TryGetValue(k, out var f) ? f : c; _to[k] = c; }
        _t0 = DateTime.UtcNow;
        if (!_animating) { _animating = true; CompositionTarget.Rendering += Tick; }
    }

    static void Tick(object? sender, EventArgs e)
    {
        double k = Math.Min(1, (DateTime.UtcNow - _t0).TotalMilliseconds / 500);
        double ease = k < 0.5 ? 2 * k * k : 1 - Math.Pow(-2 * k + 2, 2) / 2;
        foreach (var (key, to) in _to) _cur[key] = Lerp(_from[key], to, ease);
        Push();
        if (k >= 1) { CompositionTarget.Rendering -= Tick; _animating = false; }
    }

    /// <summary>Текущие цвета → новые замороженные кисти в ресурсах (DynamicResource подхватит).</summary>
    static void Push()
    {
        var r = Application.Current.Resources;
        // меняем только изменившиеся кисти: каждая замена — обход всех DynamicResource в окне
        foreach (var key in Keys)
        {
            if (_pushed.TryGetValue(key, out var was) && was == _cur[key]) continue;
            _pushed[key] = _cur[key];
            var b = new SolidColorBrush(_cur[key]); b.Freeze();
            r[key] = b;
        }
        if (_pushed.TryGetValue("Shade0", out var s0) && s0 == _cur["Shade0"] && _pushed["Shade1"] == _cur["Shade1"] && _pushed["Shade2"] == _cur["Shade2"]) return;
        _pushed["Shade0"] = _cur["Shade0"]; _pushed["Shade1"] = _cur["Shade1"]; _pushed["Shade2"] = _cur["Shade2"];
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        g.GradientStops.Add(new GradientStop(_cur["Shade0"], 0));
        g.GradientStops.Add(new GradientStop(_cur["Shade1"], 0.55));
        g.GradientStops.Add(new GradientStop(_cur["Shade2"], 1));
        g.Freeze();
        r["Shade"] = g;
    }

    static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    // ------------------------------------------------------------------ Island
    static Palette FromIsland()
    {
        string? id = IslandOverride;
        if (id == null)
            try
            {
                var path = Path.Combine(Island.Core.Settings.Dir, "settings.json");
                if (File.Exists(path))
                    id = JsonSerializer.Deserialize<Island.Core.Settings>(File.ReadAllText(path))?.Theme;
            }
            catch { }
        var t = Island.UI.Theme.Get(id);
        // у «чёрной» темы острова фон чисто чёрный — чуть приподнимаем, чтобы были видны слои
        return new Palette(t.Bg == Colors.Black ? C(0x050505) : t.Bg, t.Accent, t.Dim, t.Track);
    }

    static void WatchIsland()
    {
        if (_w != null) return;
        try
        {
            var dir = Island.Core.Settings.Dir;
            if (!Directory.Exists(dir)) return;
            var d = Application.Current.Dispatcher;
            _debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _debounce!.Stop(); if (Mode == "island") Apply(FromIsland(), true); }, d);
            _debounce.Stop();
            _w = new FileSystemWatcher(dir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
            _w.Changed += (_, _) => d.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
            _w.EnableRaisingEvents = true;
        }
        catch { }
    }

    static void StopWatch() { _w?.Dispose(); _w = null; }

    // ------------------------------------------------------------------ обложка
    /// <summary>Самый «цветной» оттенок обложки (гистограмма по тону с весом насыщенность×яркость). Серая обложка — null.</summary>
    public static Palette? FromCover(BitmapSource src)
    {
        try
        {
            BitmapSource small = src;
            if (src.PixelWidth > 32) small = new TransformedBitmap(src, new ScaleTransform(32.0 / src.PixelWidth, 32.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            const int Bins = 24;
            var weight = new double[Bins];
            var sumS = new double[Bins];
            var sumV = new double[Bins];
            var sumX = new double[Bins];
            var sumY = new double[Bins];
            double total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                ToHsv(px[i + 2], px[i + 1], px[i], out double hh, out double ss, out double vv);
                double wgt = ss * ss * vv * (vv > 0.18 ? 1 : 0.1);
                total += 1;
                if (wgt <= 0.0001) continue;
                int b = (int)(hh / 360 * Bins) % Bins;
                weight[b] += wgt; sumS[b] += ss * wgt; sumV[b] += vv * wgt;
                // тон усредняем как вектор, чтобы красный около 0°/360° не разваливался
                sumX[b] += Math.Cos(hh * Math.PI / 180) * wgt; sumY[b] += Math.Sin(hh * Math.PI / 180) * wgt;
            }
            // соседние корзины тоже считаем — пятна оттенка на границе корзин
            int best = -1; double bestW = 0;
            for (int b = 0; b < Bins; b++)
            {
                double ww = weight[b] + 0.5 * (weight[(b + 1) % Bins] + weight[(b + Bins - 1) % Bins]);
                if (ww > bestW) { bestW = ww; best = b; }
            }
            if (best < 0 || bestW / total < 0.02) return null; // почти серая обложка

            double hue = Math.Atan2(sumY[best], sumX[best]) * 180 / Math.PI;
            if (hue < 0) hue += 360;
            double s = sumS[best] / weight[best], v = sumV[best] / weight[best];

            var bg = FromHsv(hue, Math.Clamp(s * 0.75, 0.30, 0.65), 0.12);
            var accent = FromHsv(hue, Math.Clamp(s * 1.15, 0.60, 0.90), Math.Clamp(v * 1.1, 0.88, 1.0));
            var dim = FromHsv(hue, 0.16, 0.68);
            var track = FromHsv(hue, 0.30, 0.28);
            return new Palette(bg, accent, dim, track);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ цвет
    static readonly Color White = Colors.White;
    static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    static void ToHsv(byte r8, byte g8, byte b8, out double h, out double s, out double v)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max; s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
