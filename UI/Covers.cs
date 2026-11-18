using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Player.Core;

namespace Player.UI;

/// <summary>
/// Обложки: качаем маленькими (100×100 для списков), декодируем сразу в нужный размер, обесцвечиваем,
/// держим в памяти последние 300 и на диске. Одновременно грузим не больше 6.
/// </summary>
public static class Covers
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly SemaphoreSlim Gate = new(6);
    static readonly Dictionary<string, LinkedListNode<(string key, ImageSource img)>> Map = new();
    static readonly LinkedList<(string key, ImageSource img)> Lru = new();
    const int Cap = 300;
    static Dispatcher? _ui;
    static string Disk => Path.Combine(PlayerSettings.CacheDir, "covers");

    public static bool Color = true;
    public static double Dpi = 1;

    public static void Init(Dispatcher ui) { _ui = ui; Directory.CreateDirectory(Disk); }

    public static void Clear() { lock (Map) { Map.Clear(); Lru.Clear(); } }

    public static void Load(string url, int dip, Action<ImageSource?> done)
    {
        if (url.Length == 0) return;
        int px = (int)Math.Ceiling(dip * Dpi);
        var key = url + "|" + px + (Color ? "c" : "g");
        lock (Map)
        {
            if (Map.TryGetValue(key, out var node))
            {
                Lru.Remove(node); Lru.AddFirst(node);
                done(node.Value.img);
                return;
            }
        }
        _ = Task.Run(async () =>
        {
            ImageSource? img = null;
            await Gate.WaitAsync();
            try { img = await Fetch(url, px); }
            catch { }
            finally { Gate.Release(); }
            if (img != null)
                lock (Map)
                {
                    if (!Map.ContainsKey(key))
                    {
                        Map[key] = Lru.AddFirst((key, img));
                        while (Lru.Count > Cap) { Map.Remove(Lru.Last!.Value.key); Lru.RemoveLast(); }
                    }
                }
            _ui?.BeginInvoke(() => done(img), DispatcherPriority.Background);
        });
    }

    public static Task<ImageSource?> LoadAsync(string url, int dip)
    {
        var tcs = new TaskCompletionSource<ImageSource?>();
        Load(url, dip, i => tcs.TrySetResult(i));
        if (url.Length == 0) tcs.TrySetResult(null);
        return tcs.Task;
    }

    static async Task<ImageSource?> Fetch(string url, int px)
    {
        var file = Path.Combine(Disk, Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(url))) + ".jpg");
        byte[] bytes;
        if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file);
        else
        {
            bytes = await Http.GetByteArrayAsync(url);
            try { await File.WriteAllBytesAsync(file, bytes); } catch { }
        }
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(bytes);
        bi.DecodePixelWidth = px;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.EndInit();
        bi.Freeze();
        if (Color) return bi;
        var gray = new FormatConvertedBitmap(bi, PixelFormats.Gray8, null, 0);
        gray.Freeze();
        return gray;
    }
}
