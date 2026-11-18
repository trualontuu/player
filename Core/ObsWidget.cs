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
    public const int Port = 47800;
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
    // Параметры адреса: ?hide=paused — прятать на паузе; ?scale=1.5 — крупнее; ?pos=right — прижать вправо; ?static=1 — без анимаций.
    const string Html = """
<!doctype html>
<html lang="ru"><head><meta charset="utf-8"><title>Noir · сейчас играет</title>
<style>
:root{--bg:#0A0A0A;--accent:#F2F2F2;--dim:#8C8C8C;--on:#0A0A0A}
html,body{margin:0;background:transparent;overflow:hidden;font-family:"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;-webkit-font-smoothing:antialiased}
.wrap{position:absolute;left:12px;top:12px;transform-origin:0 0}
.right .wrap{left:auto;right:12px;transform-origin:100% 0}
.card{position:relative;width:420px;height:88px;display:flex;align-items:center;gap:14px;padding:0 18px 0 12px;box-sizing:border-box;
  border-radius:20px;background:color-mix(in srgb,var(--bg) 86%,transparent);border:1px solid rgba(255,255,255,.09);
  box-shadow:0 12px 32px rgba(0,0,0,.45);overflow:hidden;transition:opacity .45s ease,transform .55s cubic-bezier(.2,.9,.3,1.15),background-color .6s}
.card.hidden{opacity:0;transform:translateY(12px) scale(.97)}
.blur{position:absolute;inset:-40px;background:center/cover no-repeat;filter:blur(30px) saturate(1.4);opacity:.32;transition:opacity .6s}
.shade{position:absolute;inset:0;background:linear-gradient(90deg,color-mix(in srgb,var(--bg) 35%,transparent),color-mix(in srgb,var(--bg) 80%,transparent))}
.cover{position:relative;width:64px;height:64px;border-radius:12px;background:#1a1a1a center/cover no-repeat;flex:none;box-shadow:0 6px 16px rgba(0,0,0,.5);border:1px solid rgba(255,255,255,.08)}
.text{position:relative;flex:1;min-width:0;transition:opacity .25s,transform .25s}
.text.swap{opacity:0;transform:translateX(-6px)}
.label{font-size:10px;letter-spacing:.14em;font-weight:600;color:var(--dim);display:flex;align-items:center;gap:7px;height:12px}
.title{font-family:"Segoe UI Variable Display","Segoe UI",system-ui;font-size:17px;font-weight:600;color:#F2F2F2;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:3px}
.artist{font-size:13px;color:var(--dim);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:1px;transition:color .6s}
.bar{height:3px;border-radius:2px;background:rgba(255,255,255,.13);margin-top:8px;overflow:hidden}
.fill{height:100%;width:100%;transform:scaleX(0);transform-origin:0 50%;will-change:transform;background:var(--accent);border-radius:2px;transition:background-color .6s}
.eq{display:inline-flex;gap:2px;align-items:flex-end;height:10px}
.eq i{display:block;width:2px;height:40%;background:var(--accent);border-radius:1px;animation:eq .9s ease-in-out infinite;transition:background-color .6s}
.eq i:nth-child(2){animation-delay:-.35s;animation-duration:.8s}.eq i:nth-child(3){animation-delay:-.6s;animation-duration:1.05s}
.paused .eq i{animation-play-state:paused;height:30%}
.static *{transition:none!important;animation:none!important}
@keyframes eq{0%,100%{height:25%}50%{height:100%}}
</style></head>
<body><div class="wrap" id="wrap"><div class="card hidden" id="card">
  <div class="blur" id="blur"></div><div class="shade"></div>
  <div class="cover" id="cover"></div>
  <div class="text" id="text">
    <div class="label"><span class="eq"><i></i><i></i><i></i></span><span id="label">NOIR</span></div>
    <div class="title" id="title"></div>
    <div class="artist" id="artist"></div>
    <div class="bar"><div class="fill" id="fill"></div></div>
  </div>
</div></div>
<script>
const q=new URLSearchParams(location.search);
const hidePaused=q.get('hide')==='paused';
const scale=parseFloat(q.get('scale')||'1');
if(q.get('pos')==='right')document.body.classList.add('right');
if(q.get('static')==='1')document.body.classList.add('static');
const $=id=>document.getElementById(id);
$('wrap').style.transform=`scale(${scale})`;
let key='',pos=0,dur=0,playing=false,at=performance.now();
function fmt(c){return c&&/^#[0-9a-fA-F]{6}$/.test(c)?c:null}
async function tick(){
  try{
    const r=await fetch('/now',{cache:'no-store'});const s=await r.json();
    const root=document.documentElement.style;
    if(fmt(s.bg))root.setProperty('--bg',s.bg);
    if(fmt(s.accent))root.setProperty('--accent',s.accent);
    if(fmt(s.dim))root.setProperty('--dim',s.dim);
    const has=!!s.title, show=has&&!(hidePaused&&!s.playing);
    $('card').classList.toggle('hidden',!show);
    $('card').classList.toggle('paused',!s.playing);
    $('label').textContent=s.playing?'NOIR · СЕЙЧАС ИГРАЕТ':'NOIR · ПАУЗА';
    const k=(s.title||'')+'|'+(s.artist||'');
    if(has&&k!==key){
      const first=key==='';key=k;
      const apply=()=>{$('title').textContent=s.title;$('artist').textContent=s.artist||'';
        const u=s.cover?`url("${s.cover.replace(/"/g,'%22')}")`:'none';$('cover').style.backgroundImage=u;$('blur').style.backgroundImage=u;
        $('text').classList.remove('swap');};
      if(first)apply();else{$('text').classList.add('swap');setTimeout(apply,230);}
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
