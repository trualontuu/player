using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Player.Core;

public sealed class PlayerSettings
{
    public double Volume { get; set; } = 0.6;
    /// <summary>Выравнивать громкость треков (громкие приглушаются до −14 LUFS).</summary>
    public bool Normalize { get; set; } = true;
    /// <summary>Цвета: island — как тема Island, cover — под обложку, mono — чёрно-белые.</summary>
    public string ColorMode { get; set; } = "island";
    /// <summary>Максимальный битрейт: 320 или 192 (экономия трафика).</summary>
    public int MaxKbps { get; set; } = 320;
    /// <summary>Обесцвечивать обложки (по умолчанию цветные — интерфейс при этом остаётся ч/б).</summary>
    public bool GrayCovers { get; set; } = false;
    /// <summary>Характер волны: "" (как обычно) | favorite | discover | popular.</summary>
    public string WaveDiversity { get; set; } = "";
    /// <summary>Настроение волны: "" | active | fun | calm | sad.</summary>
    public string WaveMood { get; set; } = "";
    /// <summary>Режим волны: "" — обычная, "cs2" — твой вкус злее и энергичнее, "cs2phonk" — то же с фонком.</summary>
    public string WaveScene { get; set; } = "";
    /// <summary>Зёрна режима CS2: на что похожа музыка (треки и артисты Яндекса). По умолчанию — Drake «Away From Home»,
    /// Lil Uzi Vert «x2» и сами эти артисты: американский хайп-трэп (Yeat, Young Thug, Migos…).</summary>
    public string[] Cs2Seeds { get; set; } = { "track:118828139", "track:115180535", "artist:39273", "artist:3308067" };
    /// <summary>Артисты, которых не ставить в CS2-волне (мешают играть). Пополняется кнопкой «не для CS2».</summary>
    public List<string> Cs2Exclude { get; set; } = new() { "OsamaSon" };
    /// <summary>Ключ api-v2 SoundCloud; когда протухнет — плеер сам достанет новый с soundcloud.com.</summary>
    public string ScClientId { get; set; } = "";
    /// <summary>Где искать: "ym" — Яндекс Музыка, "sc" — SoundCloud.</summary>
    public string SearchSource { get; set; } = "ym";
    /// <summary>Сама переключать волну в режим CS2, пока запущена игра.</summary>
    public bool AutoCs2 { get; set; } = true;
    /// <summary>Какой режим был до автопереключения — вернуть после выхода из игры.</summary>
    public string? SceneBeforeCs2 { get; set; }
    public bool Shuffle { get; set; }
    /// <summary>0 — нет, 1 — список по кругу, 2 — один трек.</summary>
    public int Repeat { get; set; }
    public string Page { get; set; } = "wave";
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1080;
    public double Height { get; set; } = 700;

    [JsonIgnore] public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Player");
    [JsonIgnore] public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Player");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    Timer? _save;

    public static PlayerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(FilePath), Opts) ?? new();
        }
        catch { }
        return new();
    }

    /// <summary>Сохранение с задержкой — громкость при перетаскивании меняется десятки раз в секунду.</summary>
    public void SaveSoon()
    {
        _save ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        _save.Change(800, Timeout.Infinite);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
    }
}
