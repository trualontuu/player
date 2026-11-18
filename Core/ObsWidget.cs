using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Core;

/// <summary>
/// Виджет «сейчас играет» для OBS: крошечный HTTP-сервер только на 127.0.0.1.
///   GET /     — страница виджета (в OBS: Источник «Браузер»);
///   GET /now  — состояние JSON (трек, обложка, позиция, цвета палитры).
/// Заголовков CORS нет — чужие сайты в браузере прочитать /now не смогут.
/// </summary>
public static class ObsWidget
{
    public static int Port = 47800; // тест (--snap) ставит другой, чтобы не мешать запущенному Noir
    public static string Url => $"http://127.0.0.1:{Port}/";

    static TcpListener? _l;
    static CancellationTokenSource? _cts;
    static Func<Task<object>>? _state;

    public static bool Running => _l != null;
    /// <summary>Почему не запустился (порт занят и т.п.).</summary>
    public static string? Error { get; private set; }

    public static void Start(Func<Task<object>> state)
    {
        _state = state;
        if (_l != null) return;
        try
        {
            var l = new TcpListener(IPAddress.Loopback, Port);
            l.Start();
            _l = l;
            Error = null;
        }
        catch (Exception e) { Error = e is SocketException ? $"порт {Port} занят другой программой" : e.Message; return; }
        _cts = new CancellationTokenSource();
        _ = Loop(_l, _cts.Token);
    }

    public static void Stop()
    {
        _cts?.Cancel();
        try { _l?.Stop(); } catch { }
        _l = null; _cts = null;
    }

    static async Task Loop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Serve(c);
        }
    }

    static async Task Serve(TcpClient c)
    {
        using (c)
        {
            try
            {
                c.NoDelay = true;
                var s = c.GetStream();
                using var cts = new CancellationTokenSource(3000);
                // заголовки запроса до пустой строки (не больше 8 КБ)
                var buf = new byte[8192];
                int n = 0;
                while (n < buf.Length)
                {
                    int r = await s.ReadAsync(buf.AsMemory(n), cts.Token);
                    if (r == 0) break;
                    n += r;
                    if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
                }
                var head = Encoding.ASCII.GetString(buf, 0, n);
                var first = head.Split("\r\n")[0].Split(' ');
                if (first.Length < 2 || first[0] != "GET") { await Send(s, "405 Method Not Allowed", "text/plain", "GET only"u8.ToArray()); return; }
                var path = first[1].Split('?')[0];
                switch (path)
                {
                    case "/":
                    case "/widget":
                        await Send(s, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Html));
                        break;
                    case "/now":
                        object st = _state != null ? await _state() : new { };
                        await Send(s, "200 OK", "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(st));
                        break;
                    default:
                        await Send(s, "404 Not Found", "text/plain", "not found"u8.ToArray());
                        break;
                }
            }
            catch { }
        }
    }

    static async Task Send(Stream s, string status, string type, byte[] body)
    {
        var h = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(h));
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    // ------------------------------------------------------------------ страница
    // Параметры адреса: ?hide=paused — прятать на паузе; ?scale=1.5 — крупнее; ?pos=left / ?pos=top / ?pos=top-left — другой угол (по умолчанию правый нижний); ?color=1 — цвета палитры; ?static=1 — без анимаций.
    const string Html = """
<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><title>Noir · сейчас играет</title>
<style>
/* 1rem = 1 «базовый» пиксель при высоте источника 72; скрипт подгоняет под реальную высоту —
   поэтому чётко при любом размере: делайте источник в OBS крупным и уменьшайте на сцене */
:root{--accent:#F2F2F2;--dim:#8C8C8C;font-size:1px}
html,body{margin:0;background:transparent;overflow:hidden;font-family:"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;-webkit-font-smoothing:antialiased}
/* по умолчанию — в правом нижнем углу холста: при смене трека меняется только левый край */
.wrap{position:absolute;right:8rem;bottom:8rem}
.left .wrap{right:auto;left:8rem}
.top .wrap{bottom:auto;top:8rem}
.card{position:relative;height:52rem;width:fit-content;max-width:250rem;min-width:150rem;display:flex;align-items:center;gap:9rem;padding:0 13rem 0 8rem;box-sizing:border-box;
  border-radius:13rem;background:#0A0A0A;border:1rem solid #1E1E1E;
  box-shadow:0 6rem 18rem rgba(0,0,0,.4);overflow:hidden;transition:opacity .4s ease,transform .5s cubic-bezier(.2,.9,.3,1.15)}
.card.hidden{opacity:0;transform:translateY(8rem) scale(.97)}
.cover{width:36rem;height:36rem;border-radius:8rem;background:#1a1a1a center/cover no-repeat;flex:none;border:1rem solid rgba(255,255,255,.08)}
.text{min-width:0;transition:opacity .22s,transform .22s}
.text.swap{opacity:0;transform:translateX(-5rem)}
.row{display:flex;align-items:center;gap:6rem;min-width:0}
.title{font-family:"Segoe UI Variable Display","Segoe UI",system-ui;font-size:13.5rem;font-weight:600;color:#F2F2F2;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.artist{font-size:11.5rem;color:var(--dim);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:1rem}
.bar{position:absolute;left:0;right:0;bottom:0;height:2rem;background:rgba(255,255,255,.08)}
.fill{height:100%;width:100%;transform:scaleX(0);transform-origin:0 50%;will-change:transform;background:var(--accent)}
.eq{display:inline-flex;gap:2rem;align-items:flex-end;height:9rem;flex:none}
.eq i{display:block;width:2rem;height:40%;background:var(--accent);border-radius:1rem;animation:eq .9s ease-in-out infinite}
.eq i:nth-child(2){animation-delay:-.35s;animation-duration:.8s}.eq i:nth-child(3){animation-delay:-.6s;animation-duration:1.05s}
.paused .eq i{animation-play-state:paused;height:30%}
.static *{transition:none!important;animation:none!important}
@keyframes eq{0%,100%{height:25%}50%{height:100%}}
</style></head>
<body><div class="wrap" id="wrap"><div class="card hidden" id="card">
  <div class="cover" id="cover"></div>
  <div class="text" id="text">
    <div class="row"><span class="eq"><i></i><i></i><i></i></span><div class="title" id="title"></div></div>
    <div class="artist" id="artist"></div>
  </div>
  <div class="bar"><div class="fill" id="fill"></div></div>
</div></div>
<script>
const q=new URLSearchParams(location.search);
const hidePaused=q.get('hide')==='paused';
const color=q.get('color')==='1';   // по умолчанию чёрно-белый; ?color=1 — цвета палитры Noir
const scale=parseFloat(q.get('scale')||'1');
const posq=q.get('pos')||'';if(posq.includes('left'))document.body.classList.add('left');if(posq.includes('top'))document.body.classList.add('top');
if(q.get('static')==='1')document.body.classList.add('static');
const $=id=>document.getElementById(id);
// масштаб от высоты источника (база 72) — картинка всегда в родном разрешении, без растягивания
const fit=()=>document.documentElement.style.fontSize=(innerHeight/72*scale)+'px';
fit();addEventListener('resize',fit);
let key='',pos=0,dur=0,playing=false,at=performance.now();
const ok=c=>c&&/^#[0-9a-fA-F]{6}$/.test(c);
async function tick(){
  try{
    const s=await (await fetch('/now',{cache:'no-store'})).json();
    if(color){const r=document.documentElement.style;
      if(ok(s.accent))r.setProperty('--accent',s.accent);
      if(ok(s.dim))r.setProperty('--dim',s.dim);
      if(ok(s.bg))$('card').style.background=s.bg;}
    const has=!!s.title, show=has&&!(hidePaused&&!s.playing);
    $('card').classList.toggle('hidden',!show);
    $('card').classList.toggle('paused',!s.playing);
    const k=(s.title||'')+'|'+(s.artist||'');
    if(has&&k!==key){
      const first=key==='';key=k;
      const apply=()=>{$('title').textContent=s.title;$('artist').textContent=s.artist||'';
        $('cover').style.backgroundImage=s.cover?`url("${s.cover.replace(/"/g,'%22')}")`:'none';
        $('text').classList.remove('swap');};
      if(first)apply();else{$('text').classList.add('swap');setTimeout(apply,220);}
    }
    pos=s.pos||0;dur=s.dur||0;playing=!!s.playing;at=performance.now();
  }catch(e){$('card').classList.add('hidden');}
}
function frame(){
  const p=dur>0?Math.min(1,(pos+(playing?(performance.now()-at)/1000:0))/dur):0;
  $('fill').style.transform=`scaleX(${p.toFixed(4)})`;
  requestAnimationFrame(frame);
}
tick();setInterval(tick,1000);requestAnimationFrame(frame);
</script></body></html>
""";
}
