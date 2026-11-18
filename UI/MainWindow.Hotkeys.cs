using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Player.UI;

/// <summary>
/// Глобальные сочетания (работают и в полноэкранной игре): следующий, предыдущий, пауза, «не для CS2».
/// Регистрирует их SystemHost из App; здесь — действия и запись нового сочетания в настройках.
/// </summary>
public partial class MainWindow
{
    public (Action clear, Func<string, Action, bool> register)? HotkeyHost;
    Action<KeyEventArgs>? _capture;
    readonly HashSet<string> _hotkeyFailed = new();
    internal IReadOnlyCollection<string> HotkeyFailed => _hotkeyFailed;

    (string title, string hint, Func<string> get, Action<string> set, Action run)[] HotkeyDefs => new (string, string, Func<string>, Action<string>, Action)[]
    {
        ("Следующий трек", "", () => _s.HotkeyNext, v => _s.HotkeyNext = v, () => _pb.Next()),
        ("Предыдущий трек", "", () => _s.HotkeyPrev, v => _s.HotkeyPrev = v, () => _pb.Prev()),
        ("Пауза / играть", "", () => _s.HotkeyPause, v => _s.HotkeyPause = v, () => { if (_pb.Current == null) _ = _pb.PlayWave(); else _pb.Toggle(); }),
        ("Убрать трек", "В CS2-волне — артист в стоп-лист и следующий; в обычной — «не нравится» и следующий", () => _s.HotkeyBan, v => _s.HotkeyBan = v, () => _ = Dislike()),
    };

    /// <summary>Состояние для виджета OBS (снимок в UI-потоке).</summary>
    public Task<object> WidgetState() => Dispatcher.InvokeAsync(() =>
    {
        var t = _pb.Current;
        return (object)new
        {
            playing = _pb.IsPlaying,
            title = t?.FullTitle ?? "",
            artist = t?.Artists ?? "",
            cover = t?.CoverUrl(200) ?? "",
            pos = _pb.Position.TotalSeconds,
            dur = _pb.Duration.TotalSeconds,
            accent = Player.Core.NoirTheme.Hex("Accent"),
            bg = Player.Core.NoirTheme.Hex("Bg"),
            dim = Player.Core.NoirTheme.Hex("Dim"),
        };
    }).Task;

    /// <summary>Перерегистрировать все сочетания. Занятые другими программами — в _hotkeyFailed.</summary>
    public void ApplyHotkeys()
    {
        if (HotkeyHost is not { } h) return;
        h.clear();
        _hotkeyFailed.Clear();
        foreach (var d in HotkeyDefs)
        {
            var combo = d.get();
            if (string.IsNullOrWhiteSpace(combo)) continue;
            var run = d.run;
            if (!h.register(combo, () => Dispatcher.BeginInvoke(run))) _hotkeyFailed.Add(combo);
        }
    }

    /// <summary>Ушли со страницы, пока ждали сочетание, — вернуть сочетания.</summary>
    void CancelCapture()
    {
        if (_capture == null) return;
        _capture = null;
        ApplyHotkeys();
    }

    /// <summary>Строки для страницы настроек: кнопка с сочетанием, по клику — «нажмите сочетание».</summary>
    FrameworkElement[] HotkeyRows()
    {
        var rows = new List<FrameworkElement>();
        foreach (var d in HotkeyDefs)
        {
            var btn = new Button { Style = (Style)FindResource("Pill"), Padding = new Thickness(14, 6, 14, 6), FontSize = 12.5, MinWidth = 150 };
            var status = new TextBlock { FontSize = 12.5, Foreground = (Brush)FindResource("Dim"), TextWrapping = TextWrapping.Wrap };
            void Paint()
            {
                var c = d.get();
                btn.Content = string.IsNullOrWhiteSpace(c) ? "не задано" : Pretty(c);
                status.Text = _hotkeyFailed.Contains(c) ? "Сочетание занято другой программой — выберите другое" : d.hint;
                status.Foreground = (Brush)FindResource(_hotkeyFailed.Contains(c) ? "Fg" : "Dim");
                status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            btn.Click += (_, _) =>
            {
                // пока ждём нажатие, свои сочетания снимаем — иначе Windows отдаст их нам, а не окну
                HotkeyHost?.clear();
                btn.Content = "нажмите сочетание…";
                status.Text = "С Ctrl, Alt или Win · Esc — отмена · Backspace — убрать";
                status.Visibility = Visibility.Visible;
                _capture = e =>
                {
                    var key = e.Key == Key.System ? e.SystemKey : e.Key;
                    if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
                    _capture = null;
                    if (key == Key.Back) d.set("");
                    else if (key != Key.Escape && Combo(key) is { } combo) d.set(combo);
                    _s.SaveSoon();
                    ApplyHotkeys();
                    Paint();
                };
            };
            Paint();
            rows.Add(SRow(d.title, null, btn, status));
        }
        return rows.ToArray();
    }

    /// <summary>Нажатая клавиша + модификаторы → «Ctrl+Alt+Right». Без Ctrl/Alt/Win — нельзя: перехватили бы клавишу у игры.</summary>
    static string? Combo(Key key)
    {
        var m = Keyboard.Modifiers;
        bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        if ((m & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 && !win) return null;
        string? k = key switch
        {
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
            Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.OemTilde => "`",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
            _ => null,
        };
        if (k == null) return null;
        var parts = new List<string>();
        if ((m & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((m & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((m & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(k);
        return string.Join("+", parts);
    }

    static string Pretty(string combo) => combo.Replace("Left", "←").Replace("Right", "→").Replace("Up", "↑").Replace("Down", "↓").Replace("+", " + ");
}
