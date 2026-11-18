using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Player.Core;

/// <summary>
/// Управление плеером из других программ (лаунчер): именованный канал, одна строка JSON туда — одна обратно.
/// Канал создаётся с правами по умолчанию: писать в него может только текущий пользователь (и система).
/// </summary>
public static class ControlPipe
{
    public static string Name => "Player.Control." + Environment.UserName;

    public static void Start(Func<JsonElement, Task<object>> handle)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync();
                    var p = pipe;
                    pipe = null;
                    _ = Serve(p, handle); // следующий клиент может подключиться, пока этот ждёт ответа
                }
                catch { pipe?.Dispose(); await Task.Delay(1000); }
            }
        });
    }

    static async Task Serve(NamedPipeServerStream pipe, Func<JsonElement, Task<object>> handle)
    {
        using (pipe)
        {
            try
            {
                using var r = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                var line = await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (line == null) return;
                object result;
                try { result = await handle(JsonDocument.Parse(line).RootElement.Clone()); }
                catch (Exception e) { result = new { error = e.Message }; }
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result) + "\n");
                await pipe.WriteAsync(bytes);
                await pipe.FlushAsync();
                pipe.WaitForPipeDrain();
            }
            catch { }
        }
    }
}
