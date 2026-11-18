using System;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Player.UI;

/// <summary>
/// Перетаскивание тонкого ползунка с любой точки полосы: нажал — значение прыгнуло под курсор,
/// ведёшь — едет за мышью, отпустил — onUp. Штатный Slider тянется только за бегунок, а он у нас скрыт.
/// </summary>
public static class SliderDrag
{
    public static void Attach(Slider s, Action? onDown = null, Action? onUp = null)
    {
        void SetFrom(MouseEventArgs e)
        {
            if (s.Template?.FindName("PART_Track", s) is not Track track || track.ActualWidth <= 0) return;
            s.Value = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), s.Minimum, s.Maximum);
        }

        s.AddHandler(UIElement_PreviewDown, new MouseButtonEventHandler((_, e) =>
        {
            if (!s.IsEnabled) return;
            onDown?.Invoke();
            s.CaptureMouse();
            SetFrom(e);
            e.Handled = true;
        }), true);
        s.PreviewMouseMove += (_, e) => { if (s.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) SetFrom(e); };
        s.AddHandler(UIElement_PreviewUp, new MouseButtonEventHandler((_, e) =>
        {
            if (!s.IsMouseCaptured) return;
            SetFrom(e);
            s.ReleaseMouseCapture();
            e.Handled = true;
        }), true);
        // захват могли отнять (Alt+Tab и т.п.) — всё равно завершаем перетаскивание
        s.LostMouseCapture += (_, _) => onUp?.Invoke();
    }

    static readonly System.Windows.RoutedEvent UIElement_PreviewDown = System.Windows.UIElement.PreviewMouseLeftButtonDownEvent;
    static readonly System.Windows.RoutedEvent UIElement_PreviewUp = System.Windows.UIElement.PreviewMouseLeftButtonUpEvent;
}
