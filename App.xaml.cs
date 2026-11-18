using System;
using System.Threading;
using System.Windows;
using Island.Core;
using Player.Audio;
using Player.Core;
using Player.UI;

namespace Player;

public partial class App : Application
{
    Mutex? _single;
    EventWaitHandle? _show, _quit;
    SystemHost? _host;
    Playback? _pb;
    MainWindow? _win;
    PlayerSettings _s = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int snapAt = Array.IndexOf(e.Args, "--snap");
        if (snapAt >= 0 && snapAt + 1 < e.Args.Length)
        {
            Snap.Run(this, e.Args[snapAt + 1], Array.IndexOf(e.Args, "--play") >= 0);
            return;
        }

        // повторный запуск показывает окно, «--quit» закрывает работающий плеер
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, "Player.Spsp.Show");
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, "Player.Spsp.Quit");
        _single = new Mutex(true, "Player.Spsp.Singleton", out bool first);
        bool quitArg = Array.IndexOf(e.Args, "--quit") >= 0;
        if (!first || quitArg)
        {
            if (!first) (quitArg ? _quit : _show).Set();
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => Dispatcher.BeginInvoke(() => _win?.ShowFromTray()), null, -1, false);
        ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => Dispatcher.BeginInvoke(Quit), null, -1, true);

        _s = PlayerSettings.Load();
        Covers.Init(Dispatcher);
        Covers.Color = !_s.GrayCovers;
        _pb = new Playback(Dispatcher, _s);
        _win = new MainWindow(_pb, _s);
        Covers.Dpi = 1.5;
        _win.Loaded += (_, _) => Covers.Dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_win).DpiScaleX;

        _host = new SystemHost();
        _host.TrayLeftClick += () => { if (_win.IsVisible && _win.IsActive) _win.HideToTray(); else _win.ShowFromTray(); };
        _host.TrayRightClick += () => TrayMenu.Toggle(_pb, () => _win.ShowFromTray(), Quit);
        _host.ShowTray("Noir");

        DispatcherUnhandledException += (_, ex) => { ex.Handled = true; try { _win?.ShowToast("Ошибка: " + ex.Exception.Message); } catch { } };

        if (Array.IndexOf(e.Args, "--background") < 0) _win.Show();
        _ = _win.Start();
        // команды из лаунчера (Alt+Q): поиск музыки, пауза, волна…
        Player.Core.ControlPipe.Start(req => _win.HandleControl(req));
    }

    void Quit()
    {
        _s.Save();
        _pb?.Dispose(); _pb = null;
        _host?.Dispose(); _host = null;
        _win?.Quit();
        _single?.ReleaseMutex();
        _single?.Dispose(); _single = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _pb?.Dispose(); _host?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
