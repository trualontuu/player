using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Player.Yandex;

namespace Player.Core;

/// <summary>
/// Пульт с телефона (как Spotify Connect). Плеер подключается к ретранслятору Noir Connect (Cloudflare)
/// как «host»; Noir на iPhone с тем же аккаунтом Яндекса — как «remote». Комнату ретранслятор определяет
/// по токену (спрашивает uid у Яндекса), токен не хранит. Сюда приходят команды того же вида, что из лаунчера,
/// а отсюда уходит состояние: что играет, позиция, громкость.
/// </summary>
public static class RemoteLink
{
    const string Url = "wss://noir-connect.raspisanie-mfszc.workers.dev/ws?role=host&name=";

    static ClientWebSocket? _ws;
    static readonly SemaphoreSlim _send = new(1, 1);
    static string _lastSig = "";
    static DateTime _lastSent;

    /// <param name="handle">команда → выполнить (в UI-потоке)</param>
    /// <param name="state">текущее состояние для телефона (в UI-потоке)</param>
    /// <param name="sig">короткая подпись состояния: изменилась — отправляем</param>
    public static void Start(Func<JsonElement, Task<object>> handle, Func<Task<object>> state, Func<Task<string>> sig)
    {
        _ = Task.Run(() => Loop(handle, state));
        // раз в 1.5 с сверяем подпись состояния; при изменении (или раз в 15 с, пока играет) — шлём
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1500);
                if (_ws?.State != WebSocketState.Open) continue;
                try
                {
                    var s = await sig();
                    if (s != _lastSig || (s.StartsWith("1") && DateTime.UtcNow - _lastSent > TimeSpan.FromSeconds(15)))
                        await SendState(state, s);
                }
                catch { }
            }
        });
    }

    /// <summary>Статус связи для отладки: LOCALAPPDATA/Player/remote.log (без токена).</summary>
    static void Log(string m)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Player");
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, "remote.log");
            if (File.Exists(f) && new FileInfo(f).Length > 64 * 1024) File.Delete(f);
            File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}{Environment.NewLine}");
        }
        catch { }
    }

    static string Name()
    {
        var n = Environment.MachineName;
        return n.StartsWith("DESKTOP-", StringComparison.OrdinalIgnoreCase) || n.StartsWith("WIN-", StringComparison.OrdinalIgnoreCase) ? "Компьютер" : n;
    }

    static async Task Loop(Func<JsonElement, Task<object>> handle, Func<Task<object>> state)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (true)
        {
            var tok = Ym.Token;
            if (tok == null) { await Task.Delay(5000); continue; }
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "OAuth " + tok);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(new Uri(Url + Uri.EscapeDataString(Name())), cts.Token);
                _ws = ws;
                Log("подключён");
                backoff = TimeSpan.FromSeconds(2);
                _lastSig = "";
                await SendState(state, null);
                var buf = new byte[16 * 1024];
                while (ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buf, CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    if (text == "pong") continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        if (!doc.RootElement.TryGetProperty("cmd", out var c)) continue;
                        Log("команда " + c.GetString());
                        await handle(doc.RootElement.Clone());
                    }
                    catch { }
                    await SendState(state, null);
                }
            }
            catch (Exception e) { Log("нет связи: " + e.Message); }
            finally { if (_ws == ws) _ws = null; }
            await Task.Delay(backoff);
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
    }

    static async Task SendState(Func<Task<object>> state, string? sig)
    {
        var ws = _ws;
        if (ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(new { type = "state", state = await state() });
        await _send.WaitAsync();
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            _lastSent = DateTime.UtcNow;
            if (sig != null) _lastSig = sig;
        }
        catch { }
        finally { _send.Release(); }
    }
}
