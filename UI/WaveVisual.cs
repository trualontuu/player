using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Player.UI;

/// <summary>
/// Фон «Моей волны»: несколько белых синусоид разной прозрачности. Анимация идёт только пока страница
/// видна и музыка играет (30 кадров/с, одна StreamGeometry на линию); на паузе волны плавно затихают и таймер встаёт.
/// </summary>
public sealed class WaveVisual : FrameworkElement
{
    readonly DispatcherTimer _t = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    double _phase, _amp = 0.25, _target = 0.25;
    bool _active;
    static readonly Pen[] Pens = MakePens();

    public WaveVisual()
    {
        IsHitTestVisible = false;
        _t.Tick += (_, _) =>
        {
            _phase += 0.035 + _amp * 0.03;
            _amp += (_target - _amp) * 0.06;
            InvalidateVisual();
            if (!_active && Math.Abs(_amp - _target) < 0.005) _t.Stop();
        };
        IsVisibleChanged += (_, _) => Update();
    }

    /// <summary>playing — громкие волны, иначе тихие; анимация работает только при видимости.</summary>
    public void SetPlaying(bool playing)
    {
        _active = playing;
        _target = playing ? 1.0 : 0.25;
        Update();
    }

    void Update()
    {
        bool run = IsVisible && (_active || Math.Abs(_amp - _target) > 0.005);
        if (run && !_t.IsEnabled) _t.Start();
        else if (!run && _t.IsEnabled) _t.Stop();
    }

    static Pen[] MakePens()
    {
        var p = new Pen[5];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = new Pen(new SolidColorBrush(Color.FromArgb((byte)(70 - i * 11), 255, 255, 255)), 1.6 - i * 0.18);
            p[i].Freeze();
        }
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        double mid = h * 0.5;
        int steps = Math.Max(40, (int)(w / 8));
        for (int k = 0; k < Pens.Length; k++)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                double freq = 1.6 + k * 0.45, sp = 1 + k * 0.35, amp = h * (0.13 - k * 0.018) * _amp;
                for (int i = 0; i <= steps; i++)
                {
                    double x = w * i / steps, u = (double)i / steps;
                    double env = Math.Sin(Math.PI * u); // к краям волна сходит на нет
                    double y = mid + env * amp * Math.Sin(u * Math.PI * 2 * freq + _phase * sp + k)
                                   + env * amp * 0.35 * Math.Sin(u * Math.PI * 2 * (freq * 2.3) - _phase * 1.7);
                    if (i == 0) c.BeginFigure(new Point(x, y), false, false); else c.LineTo(new Point(x, y), true, true);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, Pens[k], g);
        }
    }
}

/// <summary>Номер строки: AlternationIndex + 1.</summary>
public sealed class PlusOne : IValueConverter
{
    public static readonly PlusOne It = new();
    public object Convert(object v, Type t, object p, CultureInfo c) => v is int i ? (i + 1).ToString() : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>
/// Три прыгающих столбика у играющего трека. Анимация идёт, только пока музыка играет и строка видна
/// (в списке она одна, остальные строки виртуализированы) — на паузе столбики замирают.
/// </summary>
public sealed class EqBars : FrameworkElement
{
    static bool _playing;
    static event Action? PlayingChanged;
    public static bool Playing { get => _playing; set { if (_playing != value) { _playing = value; PlayingChanged?.Invoke(); } } }

    static readonly Brush Fill = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    readonly DispatcherTimer _t = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    readonly double _seed = Random.Shared.NextDouble() * 10;
    double _time;

    public EqBars()
    {
        Width = 14; Height = 14; IsHitTestVisible = false;
        _t.Tick += (_, _) => { _time += 0.04; InvalidateVisual(); };
        IsVisibleChanged += (_, _) => Update();
        Loaded += (_, _) => { PlayingChanged += Update; Update(); };
        Unloaded += (_, _) => { PlayingChanged -= Update; _t.Stop(); };
    }

    void Update()
    {
        bool run = IsVisible && _playing;
        if (run && !_t.IsEnabled) _t.Start();
        else if (!run && _t.IsEnabled) { _t.Stop(); InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, bw = 3, gap = (w - bw * 3) / 2;
        for (int i = 0; i < 3; i++)
        {
            double f = _playing
                ? 0.25 + 0.75 * Math.Abs(Math.Sin(_time * (5.1 + i * 1.7) + _seed + i * 1.9) * Math.Sin(_time * (2.3 + i) + i))
                : new[] { 0.45, 0.8, 0.3 }[i];
            double bh = Math.Max(2, h * f);
            dc.DrawRoundedRectangle(Fill, null, new Rect(i * (bw + gap), h - bh, bw, bh), 1, 1);
        }
    }
}
