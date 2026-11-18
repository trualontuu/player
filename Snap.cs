using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Player.Audio;
using Player.Core;
using Player.UI;
using Player.Yandex;

namespace Player;

/// <summary>
/// «Player.exe --snap &lt;папка&gt; [--play]» — проверка без кликов: окно за экраном, снимки страниц с живыми данными,
/// report.txt (время запросов, память). С --play волна запускается на громкости 0 и без отзывов в аккаунт.
/// </summary>
public static class Snap
{
    public static async void Run(Application app, string dir, bool play)
    {
        var log = new StringBuilder();
        var sw = Stopwatch.StartNew();
        void L(string s) { log.AppendLine($"[{sw.ElapsedMilliseconds,6} мс] {s}"); }
        Playback? pb = null;
        try
        {
            Directory.CreateDirectory(dir);
            Ym.NoFeedback = true;
            var s = new PlayerSettings { Volume = 0, Width = 1080, Height = 700, WaveScene = Environment.GetEnvironmentVariable("PLAYER_SCENE") ?? "" };
            Covers.Init(app.Dispatcher);
            Covers.Dpi = 1.5;
            pb = new Playback(app.Dispatcher, s);
            var win = new MainWindow(pb, s) { SnapMode = true, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            win.Show();
            await win.Start();
            bool pipe = Environment.GetEnvironmentVariable("PLAYER_PIPE") == "1";
            if (pipe) { ControlPipe.Start(req => win.HandleControl(req)); L("канал управления открыт"); }
            L($"вход: {(Ym.LoggedIn ? "есть" : "нет")}");
            await Task.Delay(400);
            Save(win, Path.Combine(dir, "1_wave.png"));

            var t0 = sw.ElapsedMilliseconds;
            win.NavLikes.IsChecked = true;
            await win.ShowLikes();
            L($"«Мне нравится» за {sw.ElapsedMilliseconds - t0} мс");
            await Task.Delay(1500); // обложки
            Save(win, Path.Combine(dir, "2_likes.png"));

            t0 = sw.ElapsedMilliseconds;
            win.NavSearch.IsChecked = true;
            win.SetSearchText("drake");
            await win.RunSearch("drake");
            L($"поиск за {sw.ElapsedMilliseconds - t0} мс");
            await Task.Delay(1500);
            Save(win, Path.Combine(dir, "3_search.png"));

            t0 = sw.ElapsedMilliseconds;
            await win.SnapArtist("39273"); // Drake
            L($"страница артиста за {sw.ElapsedMilliseconds - t0} мс");
            await Task.Delay(1500);
            Save(win, Path.Combine(dir, "3b_artist.png"));

            t0 = sw.ElapsedMilliseconds;
            await win.SnapAlbum("29651389"); // For All The Dogs
            L($"альбом за {sw.ElapsedMilliseconds - t0} мс");
            await Task.Delay(1200);
            Save(win, Path.Combine(dir, "3c_album.png"));

            // SoundCloud
            win.NavSearch.IsChecked = true;
            win.SetSearchText("lil uzi vert");
            t0 = sw.ElapsedMilliseconds;
            win.SnapSource("sc");
            await Task.Delay(100);
            await win.RunSearch("lil uzi vert");
            L($"SoundCloud: поиск за {sw.ElapsedMilliseconds - t0} мс");
            await Task.Delay(1800);
            Save(win, Path.Combine(dir, "3d_sc_search.png"));
            t0 = sw.ElapsedMilliseconds;
            await win.SnapArtist("sc:10494998");
            L($"SoundCloud: артист за {sw.ElapsedMilliseconds - t0} мс");
            await Task.Delay(1800);
            Save(win, Path.Combine(dir, "3e_sc_artist.png"));
            win.SnapSource("ym");

            if (play)
            {
                win.NavWave.IsChecked = true;
                if (Environment.GetEnvironmentVariable("PLAYER_SEEDS") is { Length: > 0 } sd) Playback.SeedsOverride = sd.Split(',');
                t0 = sw.ElapsedMilliseconds;
                await pb.PlayWave();
                for (int i = 0; i < 60 && !(pb.IsPlaying && !pb.IsBuffering && pb.Position > TimeSpan.Zero); i++) await Task.Delay(250);
                foreach (var q in pb.Queue) L($"   очередь: {q.FullTitle} — {q.Artists}");
                L($"выравнивание: громкость трека {pb.Current?.Lufs?.ToString("F1") ?? "?"} LUFS → коэффициент {pb.Gain:F2}");
                L($"волна: до звука {sw.ElapsedMilliseconds - t0} мс, играет={pb.IsPlaying}, трек «{pb.Current?.FullTitle}» — {pb.Current?.Artists}, в очереди {pb.Queue.Count}");
                await Task.Delay(3000);
                L($"через 3 с позиция {pb.Position.TotalSeconds:F1} с из {pb.Duration.TotalSeconds:F0}");
                Save(win, Path.Combine(dir, "4_wave_playing.png"));
                pb.Seek(TimeSpan.FromSeconds(60));
                await Task.Delay(800);
                L($"перемотка на 60 с с первого раза: позиция {pb.Position.TotalSeconds:F1} с");

                // очередь: «играть следующим» и «добавить в очередь»
                var extra = (await Ym.SearchAll("lil uzi vert x2")).Tracks.Take(2).ToList();
                if (extra.Count == 2)
                {
                    pb.AddToQueue(extra[1]);
                    pb.PlayNext(extra[0]);
                    L($"очередь после ручных: следующий «{pb.Queue[pb.Index + 1].FullTitle}», за ним «{pb.Queue[pb.Index + 2].FullTitle}»");
                }
                win.SnapQueue(true);
                await Task.Delay(1500);
                Save(win, Path.Combine(dir, "5_queue.png"));
                win.SnapQueue(false);

                t0 = sw.ElapsedMilliseconds;
                var before = pb.Current;
                pb.Next();
                for (int i = 0; i < 60 && (pb.Current == before || !(pb.IsPlaying && pb.Position > TimeSpan.Zero)); i++) await Task.Delay(100);
                L($"следующий трек за {sw.ElapsedMilliseconds - t0} мс: «{pb.Current?.FullTitle}»");
                // SoundCloud: реальный HLS-поток
                var scRes = await Sc.SearchAll("lil uzi vert");
                var scList = scRes.Tracks.Where(x => x.Available).Take(5).ToList();
                L($"SoundCloud: из {scRes.Tracks.Count} треков играбельных {scRes.Tracks.Count(x => x.Available)} (остальные с DRM)");
                if (scList.Count > 0)
                {
                    t0 = sw.ElapsedMilliseconds;
                    pb.PlayList(scList, 0, "SoundCloud тест");
                    for (int i = 0; i < 80 && !(pb.IsPlaying && !pb.IsBuffering && pb.Position > TimeSpan.FromSeconds(0.5)); i++) await Task.Delay(100);
                    L($"SoundCloud: до звука {sw.ElapsedMilliseconds - t0} мс, «{pb.Current?.FullTitle}» — {pb.Current?.Artists}, позиция {pb.Position.TotalSeconds:F1} с из {pb.Duration.TotalSeconds:F0}");
                    pb.Seek(TimeSpan.FromSeconds(40));
                    await Task.Delay(1500);
                    L($"SoundCloud: перемотка на 40 с → {pb.Position.TotalSeconds:F1} с");
                    Save(win, Path.Combine(dir, "6_sc_playing.png"));
                }
                pb.Pause();
            }

            if (pipe) { L("жду команды из лаунчера 25 с"); await Task.Delay(25000); }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            L($"память: private {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:F1} МБ, managed {GC.GetTotalMemory(false) / 1048576.0:F1} МБ");
        }
        catch (Exception e) { L("ОШИБКА: " + e); }
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        pb?.Dispose();
        app.Shutdown();
    }

    static void Save(Window w, string file)
    {
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        double scale = 1.25;
        var rtb = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
