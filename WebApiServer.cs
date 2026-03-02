// WebApiServer.cs — Embedded HTTP + WebSocket server
// Shares the live SipClientEngine instance with the WPF window.
// No extra NuGet packages — HttpListener + WebSockets ship with .NET 8.
//
// Endpoints:
//   GET  /              → full HTML page (the web SIP client UI)
//   GET  /api/status    → JSON snapshot of current state + last 150 log lines
//   GET  /api/contacts  → JSON array of contacts from SipClientConfig
//   POST /api/call      → { "target": "500@192.168.1.1" }
//   POST /api/hangup    → (no body)
//   POST /api/dtmf      → { "digit": "5" }
//   POST /api/testreg   → (no body)
//   WS   /              → push-only WebSocket; browser receives all events in real-time

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AudisService
{
    public sealed class WebApiServer : IDisposable
    {
        private readonly HttpListener           _listener  = new();
        private readonly SipClientEngine        _engine;
        private readonly Func<SipClientConfig>  _cfgLoader;
        private readonly Action<SipAudioMode, string> _modeSetter;

        // All currently-connected WebSocket clients
        private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();

        // Scrolling log ring-buffer — sent in full to each new WebSocket connection
        private readonly Queue<string>  _logBuffer  = new();
        private const    int            LogCap      = 150;
        private readonly object         _logLock    = new();

        // Cached live state
        private string  _callState   = "IDLE";
        private string? _callStarted = null;          // ISO-8601 or null
        private string  _regStatus   = "Disabled";
        private string  _regColor    = "#9e9e9e";
        private string  _regCode     = "";

        private CancellationTokenSource _cts = new();

        public int  Port      { get; }
        public bool IsRunning { get; private set; }

        // ── Constructor ───────────────────────────────────────────────────────────

        public WebApiServer(int port, SipClientEngine engine, Func<SipClientConfig> cfgLoader,
                            Action<SipAudioMode, string> modeSetter)
        {
            Port        = port;
            _engine     = engine;
            _cfgLoader  = cfgLoader;
            _modeSetter = modeSetter;
        }

        // ── Start / Stop ─────────────────────────────────────────────────────────

        public void Start()
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();

            // Try binding to all interfaces so LAN devices (phones, tablets) can connect.
            // "http://+:PORT/" requires Administrator rights or a one-time netsh ACL:
            //   netsh http add urlacl url=http://+:8765/ user=Everyone
            // If that throws we fall back silently to localhost-only.
            bool lanBound = TryStartListener($"http://+:{Port}/");
            if (!lanBound)
            {
                TryStartListener($"http://localhost:{Port}/");
                HandleLog(LogLevel.Warning,
                    $"[WEB] LAN access unavailable — running localhost-only on port {Port}. " +
                    $"For LAN access, run once as Administrator OR execute: " +
                    $"netsh http add urlacl url=http://+:{Port}/ user=Everyone");
            }
            IsRunning = true;

            // Seed state cache from engine's current state
            _callState = _engine.State.ToString().ToUpper();
            (_regStatus, _regColor, _regCode) = Reg2Display(_engine.RegState, _engine.LastRegStatusCode);

            // Wire engine events
            _engine.OnCallStateChanged         += HandleCallState;
            _engine.OnRegistrationStateChanged += HandleRegState;
            _engine.OnLog                      += HandleLog;

            _ = AcceptLoop(_cts.Token);
        }

        private bool TryStartListener(string prefix)
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(prefix);
                _listener.Start();
                return true;
            }
            catch (System.Net.HttpListenerException)
            {
                return false;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts.Cancel();
            _engine.OnCallStateChanged         -= HandleCallState;
            _engine.OnRegistrationStateChanged -= HandleRegState;
            _engine.OnLog                      -= HandleLog;
            try { _listener.Stop(); } catch { }
            IsRunning = false;

            // Close all open WebSockets
            foreach (var (id, ws) in _clients)
            {
                try { _ = ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopped", CancellationToken.None); }
                catch { }
            }
            _clients.Clear();
        }

        // ── Engine event handlers ─────────────────────────────────────────────────

        private void HandleCallState(ClientCallState state)
        {
            _callState = state.ToString().ToUpper();

            if (state == ClientCallState.Connected)
                _callStarted = DateTime.UtcNow.ToString("O");
            else if (state is ClientCallState.Idle or ClientCallState.Ended)
                _callStarted = null;

            Push(Json(new { type = "callState", state = _callState, callStarted = _callStarted }));
        }

        private void HandleRegState(RegistrationState state, int code)
        {
            (_regStatus, _regColor, _regCode) = Reg2Display(state, code);
            Push(Json(new { type = "regState", status = _regStatus, color = _regColor, code = _regCode }));
        }

        private void HandleLog(LogLevel lvl, string msg)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] [{lvl.ToString()[..3].ToUpper()}] {msg}";
            lock (_logLock)
            {
                _logBuffer.Enqueue(entry);
                if (_logBuffer.Count > LogCap) _logBuffer.Dequeue();
            }
            Push(Json(new { type = "log", message = entry }));
        }

        // ── HTTP accept loop ──────────────────────────────────────────────────────

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                    _ = Dispatch(ctx, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(100); }
            }
        }

        private async Task Dispatch(HttpListenerContext ctx, CancellationToken ct)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            res.Headers.Add("Access-Control-Allow-Origin",  "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

            try
            {
                if (req.IsWebSocketRequest) { await HandleWebSocket(ctx, ct); return; }

                string path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
                if (path == "") path = "/";

                // ── Static page ───────────────────────────────────────────────────
                if (path is "/" or "/index.html" && req.HttpMethod == "GET")
                { await SendHtml(res, BuildPage()); return; }

                // ── REST: status ──────────────────────────────────────────────────
                if (path == "/api/status" && req.HttpMethod == "GET")
                {
                    string[] logs;
                    lock (_logLock) logs = _logBuffer.ToArray();
                    await SendJson(res, new
                    {
                        callState   = _callState,
                        callStarted = _callStarted,
                        regStatus   = _regStatus,
                        regColor    = _regColor,
                        regCode     = _regCode,
                        logs
                    });
                    return;
                }

                // ── REST: contacts ────────────────────────────────────────────────
                if (path == "/api/contacts" && req.HttpMethod == "GET")
                {
                    var cfg = _cfgLoader();
                    var list = new List<object>();
                    foreach (var c in cfg.Contacts)
                        list.Add(new { c.Name, c.Extension });
                    await SendJson(res, list);
                    return;
                }

                // ── REST: call ────────────────────────────────────────────────────
                if (path == "/api/call" && req.HttpMethod == "POST")
                {
                    string body   = await ReadBody(req);
                    string target = "";
                    try
                    {
                        using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}");
                        if (doc.RootElement.TryGetProperty("target", out var t))
                            target = t.GetString() ?? "";
                    }
                    catch { }
                    if (!string.IsNullOrWhiteSpace(target)) _ = _engine.CallAsync(target);
                    await SendJson(res, new { ok = true, target });
                    return;
                }

                // ── REST: hangup ──────────────────────────────────────────────────
                if (path == "/api/hangup" && req.HttpMethod == "POST")
                { _engine.HangUp(); await SendJson(res, new { ok = true }); return; }

                // ── REST: dtmf ────────────────────────────────────────────────────
                if (path == "/api/dtmf" && req.HttpMethod == "POST")
                {
                    string body  = await ReadBody(req);
                    string digit = "";
                    try
                    {
                        using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}");
                        if (doc.RootElement.TryGetProperty("digit", out var d))
                            digit = d.GetString() ?? "";
                    }
                    catch { }
                    if (digit.Length == 1) _engine.SendDtmf(digit[0]);
                    await SendJson(res, new { ok = true });
                    return;
                }

                // ── REST: test registration ───────────────────────────────────────
                if (path == "/api/testreg" && req.HttpMethod == "POST")
                { _ = _engine.TestRegistrationAsync(); await SendJson(res, new { ok = true }); return; }

                // ── REST: audio mode GET ──────────────────────────────────────────
                if (path == "/api/audiomode" && req.HttpMethod == "GET")
                {
                    var cfg = _cfgLoader();
                    await SendJson(res, new
                    {
                        mode          = cfg.AudioMode.ToString(),
                        customWav     = cfg.CustomWav,
                        availableWavs = _engine.GetAudioFiles()
                    });
                    return;
                }

                // ── REST: audio mode POST ─────────────────────────────────────────
                if (path == "/api/audiomode" && req.HttpMethod == "POST")
                {
                    string body = await ReadBody(req);
                    string modeStr  = "Standard";
                    string wavFile  = "";
                    try
                    {
                        using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}");
                        if (doc.RootElement.TryGetProperty("mode", out var m))
                            modeStr = m.GetString() ?? "Standard";
                        if (doc.RootElement.TryGetProperty("customWav", out var w))
                            wavFile = w.GetString() ?? "";
                    }
                    catch { }

                    var mode = modeStr == "CustomWav" ? SipAudioMode.CustomWav : SipAudioMode.Standard;
                    _modeSetter(mode, wavFile);
                    // Push the new mode to all WebSocket clients
                    Push(Json(new { type = "audioMode", mode = mode.ToString(), customWav = wavFile }));
                    await SendJson(res, new { ok = true, mode = mode.ToString(), customWav = wavFile });
                    return;
                }

                res.StatusCode = 404; res.Close();
            }
            catch { try { res.StatusCode = 500; res.Close(); } catch { } }
        }

        // ── WebSocket ─────────────────────────────────────────────────────────────

        private async Task HandleWebSocket(HttpListenerContext ctx, CancellationToken ct)
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            var ws    = wsCtx.WebSocket;
            var id    = Guid.NewGuid();
            _clients.TryAdd(id, ws);

            try
            {
                // Send full state snapshot immediately so the page is current on load
                string[] logs;
                lock (_logLock) logs = _logBuffer.ToArray();
                var initCfg = _cfgLoader();
                await WsSend(ws, Json(new
                {
                    type          = "init",
                    callState     = _callState,
                    callStarted   = _callStarted,
                    regStatus     = _regStatus,
                    regColor      = _regColor,
                    regCode       = _regCode,
                    audioMode     = initCfg.AudioMode.ToString(),
                    customWav     = initCfg.CustomWav,
                    availableWavs = _engine.GetAudioFiles(),
                    logs
                }));

                // Drain receive buffer to keep the socket alive (we're push-only from server)
                var buf = new byte[256];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }
            finally
            {
                _clients.TryRemove(id, out _);
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                ws.Dispose();
            }
        }

        private void Push(string json)
        {
            foreach (var (id, ws) in _clients)
            {
                if (ws.State != WebSocketState.Open) { _clients.TryRemove(id, out _); continue; }
                _ = WsSend(ws, json);
            }
        }

        private static async Task WsSend(WebSocket ws, string json)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
        }

        // ── HTTP response helpers ─────────────────────────────────────────────────

        private static async Task SendJson(HttpListenerResponse res, object obj)
            => await SendBytes(res, Encoding.UTF8.GetBytes(Json(obj)), "application/json");

        private static async Task SendHtml(HttpListenerResponse res, string html)
            => await SendBytes(res, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");

        private static async Task SendBytes(HttpListenerResponse res, byte[] bytes, string ct)
        {
            res.ContentType     = ct;
            res.ContentLength64 = bytes.Length;
            try { await res.OutputStream.WriteAsync(bytes); res.Close(); } catch { }
        }

        private static async Task<string> ReadBody(HttpListenerRequest req)
        {
            using var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            return await sr.ReadToEndAsync();
        }

        private static string Json(object obj) => JsonSerializer.Serialize(obj);

        // ── State helpers ─────────────────────────────────────────────────────────

        private static (string status, string color, string code) Reg2Display(RegistrationState s, int code)
            => s switch
            {
                RegistrationState.Registered   => ("Online",       "#4caf50", "(200 OK)"),
                RegistrationState.Registering  => ("Registering…", "#ff9800", ""),
                RegistrationState.Failed       => ("Failed",       "#f44336", code > 0 ? $"({code})" : "(no response)"),
                RegistrationState.Unregistered => ("Unregistered", "#ff9800", ""),
                _                              => ("Disabled",     "#9e9e9e", "")
            };

        // ── Embedded HTML page ────────────────────────────────────────────────────

        private string BuildPage() => _htmlTemplate.Replace("__PORT__", Port.ToString());

        private const string _htmlTemplate = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Audis Web — SIP Client</title>
<style>
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',Tahoma,sans-serif;background:#1c1c1c;color:#ddd;
     display:flex;flex-direction:column;height:100vh;overflow:hidden}

/* ─── Top bar ─────────────────────────────────────────── */
.topbar{background:#222;border-bottom:2px solid #0078d7;
        padding:8px 16px;display:flex;align-items:center;gap:16px;flex-shrink:0}
.brand{font-size:17px;font-weight:700;color:#fff;letter-spacing:1px}
.brand em{color:#0078d7;font-style:normal}
.tagline{font-size:11px;color:#555;margin-top:1px}
.url{margin-left:auto;font-size:11px;color:#444;font-family:Consolas,monospace}

/* ─── Reg bar ─────────────────────────────────────────── */
.regbar{background:#1e1e1e;border-bottom:1px solid #2a2a2a;
        padding:5px 16px;display:flex;align-items:center;gap:8px;
        font-size:12px;flex-shrink:0}
.rdot{width:10px;height:10px;border-radius:50%;background:#555;flex-shrink:0;transition:background .3s}
.rstatus{font-weight:600}
.rcode{color:#555;font-size:11px}
.wsbadge{margin-left:auto;display:flex;align-items:center;gap:5px;font-size:11px;color:#555}
.wsled{width:8px;height:8px;border-radius:50%;background:#333;transition:background .3s}
.wsled.on{background:#4caf50}

/* ─── Body split ──────────────────────────────────────── */
.body{flex:1;display:flex;overflow:hidden}

/* ─── Phone panel ─────────────────────────────────────── */
.phone{width:296px;flex-shrink:0;background:#212121;
       border-right:1px solid #2a2a2a;display:flex;
       flex-direction:column;padding:12px;gap:10px;overflow-y:auto}

.status-card{background:#181818;border:1px solid #2a2a2a;border-radius:6px;
             padding:14px;text-align:center}
.stxt{font-size:26px;font-weight:700;letter-spacing:4px;
      color:#69f0ae;transition:color .3s;font-family:Consolas,monospace}
.stimer{font-size:13px;font-family:Consolas,monospace;
        color:#555;margin-top:4px;min-height:18px}

.numrow{display:flex;gap:6px}
.numinput{flex:1;background:#181818;border:1px solid #333;border-radius:5px;
          color:#fff;padding:9px 10px;font-size:15px;font-family:Consolas,monospace}
.numinput:focus{outline:none;border-color:#0078d7}
.delbtn{background:#252525;border:1px solid #333;border-radius:5px;
        color:#888;padding:0 10px;font-size:18px;cursor:pointer}
.delbtn:hover{background:#2e2e2e}

select.contacts{width:100%;background:#181818;border:1px solid #333;
                border-radius:5px;color:#aaa;padding:7px 8px;font-size:12px}
select.contacts:focus{outline:none;border-color:#0078d7}

.callrow{display:flex;gap:8px}
.bcall,.bhang{flex:1;padding:11px;border:none;border-radius:5px;
              color:#fff;font-size:15px;font-weight:700;cursor:pointer;transition:background .15s}
.bcall{background:#1b5e20}.bcall:hover:not(:disabled){background:#2e7d32}
.bhang{background:#7f0000}.bhang:hover:not(:disabled){background:#b71c1c}
.bcall:disabled,.bhang:disabled{opacity:.3;cursor:default}

/* ─── Dialpad ─────────────────────────────────────────── */
.dialpad{display:grid;grid-template-columns:repeat(3,1fr);gap:6px}
.dp{background:#252525;border:1px solid #333;border-radius:5px;
    color:#eee;font-size:20px;font-weight:600;padding:12px 0;
    cursor:pointer;text-align:center;user-select:none;
    transition:background .1s;line-height:1}
.dp:hover{background:#303030}.dp:active{background:#404040}
.dpsub{font-size:9px;color:#555;display:block;font-weight:400;
       letter-spacing:1px;margin-top:3px}

.bsec{width:100%;background:#0b2540;border:none;border-radius:5px;
      color:#5dade2;font-size:12px;padding:7px;cursor:pointer;transition:background .15s}
.bsec:hover:not(:disabled){background:#0d47a1;color:#fff}
.bsec:disabled{opacity:.25;cursor:default}

/* ─── Audio mode strip ────────────────────────────────── */
.modestrip{background:#1a1a2e;border:1px solid #2a2a4a;border-radius:6px;
           padding:9px 10px;display:flex;flex-direction:column;gap:7px}
.modelbl{font-size:10px;color:#5577aa;text-transform:uppercase;letter-spacing:.8px;font-weight:600}
.moderow{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.modeopt{display:flex;align-items:center;gap:4px;font-size:12px;color:#aaa;cursor:pointer;user-select:none}
.modeopt input[type=radio]{accent-color:#0078d7;cursor:pointer}
select.wavsel{background:#111;border:1px solid #333;border-radius:4px;
              color:#ccc;padding:3px 6px;font-size:12px;flex:1;min-width:0}
select.wavsel:disabled{opacity:.3}
select.wavsel:focus{outline:none;border-color:#0078d7}

/* ─── Log panel ───────────────────────────────────────── */
.logpanel{flex:1;display:flex;flex-direction:column;overflow:hidden}
.loghead{background:#1e1e1e;border-bottom:1px solid #2a2a2a;
         padding:6px 12px;display:flex;align-items:center;gap:8px;flex-shrink:0}
.logtitle{font-size:12px;color:#666;font-weight:600}
.btiny{background:#252525;border:1px solid #333;border-radius:3px;
       color:#aaa;font-size:11px;padding:2px 8px;cursor:pointer}
.btiny:hover{background:#303030}
.loglist{flex:1;overflow-y:auto;padding:4px 6px;
         font-family:Consolas,monospace;font-size:11px;
         line-height:1.65;background:#181818}
.ll{color:#666;border-bottom:1px solid #1e1e1e;padding:1px 0;word-break:break-all}
.ll:last-child{border-bottom:none}
</style>
</head>
<body>

<!-- top bar -->
<div class="topbar">
  <div>
    <div class="brand">AUDIS <em>Web</em></div>
    <div class="tagline">Kybl Enterprise — SIP Client Remote Interface</div>
  </div>
  <div class="url" id="pageUrl">http://localhost:__PORT__/</div>
</div>

<!-- registration status bar -->
<div class="regbar">
  <div class="rdot" id="rdot"></div>
  <span class="rstatus" id="rstatus">Disabled</span>
  <span class="rcode"   id="rcode"></span>
  <div class="wsbadge">
    <div class="wsled" id="wsled"></div>
    <span id="wslbl">Connecting…</span>
  </div>
</div>

<div class="body">

  <!-- ── Phone ── -->
  <div class="phone">

    <div class="status-card">
      <div class="stxt"   id="stxt">IDLE</div>
      <div class="stimer" id="stimer"></div>
    </div>

    <!-- number input -->
    <div class="numrow">
      <input class="numinput" id="num" type="text"
             placeholder="Number / SIP URI…" autocomplete="off"/>
      <button class="delbtn" id="bdel" title="Clear">⌫</button>
    </div>

    <!-- contacts dropdown -->
    <select class="contacts" id="contacts">
      <option value="">— Contacts —</option>
    </select>

    <!-- call / hangup -->
    <div class="callrow">
      <button class="bcall" id="bcall">📞 Call</button>
      <button class="bhang" id="bhang" disabled>📵 Hang Up</button>
    </div>

    <!-- dialpad -->
    <div class="dialpad">
      <button class="dp" data-d="1">1<span class="dpsub">&nbsp;</span></button>
      <button class="dp" data-d="2">2<span class="dpsub">ABC</span></button>
      <button class="dp" data-d="3">3<span class="dpsub">DEF</span></button>
      <button class="dp" data-d="4">4<span class="dpsub">GHI</span></button>
      <button class="dp" data-d="5">5<span class="dpsub">JKL</span></button>
      <button class="dp" data-d="6">6<span class="dpsub">MNO</span></button>
      <button class="dp" data-d="7">7<span class="dpsub">PQRS</span></button>
      <button class="dp" data-d="8">8<span class="dpsub">TUV</span></button>
      <button class="dp" data-d="9">9<span class="dpsub">WXYZ</span></button>
      <button class="dp" data-d="*">✱</button>
      <button class="dp" data-d="0">0</button>
      <button class="dp" data-d="#">#</button>
    </div>

    <button class="bsec" id="btestreg">Test Registration</button>

    <!-- Audio mode selector -->
    <div class="modestrip">
      <div class="modelbl">Greeting Audio</div>
      <div class="moderow">
        <label class="modeopt">
          <input type="radio" name="audioMode" value="Standard" checked> Standard (AUDIS)
        </label>
        <label class="modeopt">
          <input type="radio" name="audioMode" value="CustomWav"> Custom&nbsp;.wav:
        </label>
      </div>
      <select class="wavsel" id="wavSel" disabled>
        <option value="">Loading…</option>
      </select>
    </div>

  </div>

  <!-- ── Log ── -->
  <div class="logpanel">
    <div class="loghead">
      <span class="logtitle">SIP Client Log</span>
      <button class="btiny" id="bclr">Clear</button>
    </div>
    <div class="loglist" id="loglist"></div>
  </div>

</div>

<script>
'use strict';

// ─── state ───────────────────────────────────────────────────────────────────
const stateColor={
  CONNECTED:'#69f0ae', CALLING:'#ffe57f',
  ENDING:'#ffa726',    ENDED:'#ef5350',
  IDLE:'#69f0ae'
};
let inCall=false, callStart=null, timerId=null;

// ─── helpers ─────────────────────────────────────────────────────────────────
const $=id=>document.getElementById(id);
const post=(url,body)=>fetch(url,{method:'POST',
  headers:{'Content-Type':'application/json'},body:JSON.stringify(body||{})});

function setCallState(state, startedIso){
  $('stxt').textContent=state;
  $('stxt').style.color=stateColor[state]||'#69f0ae';
  inCall=state==='CONNECTED'||state==='CALLING';
  $('bcall').disabled=inCall;
  $('bhang').disabled=!inCall;
  clearInterval(timerId); $('stimer').textContent='';
  if(state==='CONNECTED'&&startedIso){
    callStart=new Date(startedIso);
    timerId=setInterval(()=>{
      const s=Math.floor((Date.now()-callStart)/1000);
      $('stimer').textContent=
        String(Math.floor(s/60)).padStart(2,'0')+':'+String(s%60).padStart(2,'0');
    },1000);
  }
}

function setReg(status, color, code){
  $('rdot').style.background=color;
  $('rstatus').textContent=status;
  $('rcode').textContent=code||'';
}

function addLog(msg){
  const box=$('loglist');
  const d=document.createElement('div');
  d.className='ll'; d.textContent=msg;
  box.appendChild(d);
  while(box.children.length>400)box.removeChild(box.firstChild);
  box.scrollTop=box.scrollHeight;
}

function loadContacts(){
  fetch('/api/contacts').then(r=>r.json()).then(list=>{
    const sel=$('contacts');
    sel.innerHTML='<option value="">— Contacts —</option>';
    list.forEach(c=>{
      const o=document.createElement('option');
      o.value=c.Extension;
      o.textContent=c.Name?c.Name+' — '+c.Extension:c.Extension;
      sel.appendChild(o);
    });
  }).catch(()=>{});
}

function target(){
  return $('contacts').value||$('num').value.trim();
}

// ─── WebSocket ────────────────────────────────────────────────────────────────
function connect(){
  const ws=new WebSocket((location.protocol==='https:'?'wss':'ws')+'://'+location.host+'/');
  ws.onopen=()=>{
    $('wsled').classList.add('on');
    $('wslbl').textContent='Live';
    loadContacts();
  };
  ws.onclose=()=>{
    $('wsled').classList.remove('on');
    $('wslbl').textContent='Reconnecting…';
    setTimeout(connect,3000);
  };
  ws.onerror=()=>ws.close();
  ws.onmessage=ev=>{
    const m=JSON.parse(ev.data);
    if(m.type==='init'){
      setCallState(m.callState||'IDLE', m.callStarted);
      setReg(m.regStatus, m.regColor, m.regCode);
      applyAudioModeUi(m.audioMode||'Standard', m.customWav||'', m.availableWavs||[]);
      (m.logs||[]).forEach(addLog);
    } else if(m.type==='callState'){
      setCallState(m.state, m.callStarted);
    } else if(m.type==='regState'){
      setReg(m.status, m.color, m.code);
    } else if(m.type==='log'){
      addLog(m.message);
    } else if(m.type==='audioMode'){
      applyAudioModeUi(m.mode, m.customWav, null);
    }
  };
}

// ─── event wiring ─────────────────────────────────────────────────────────────
$('bcall').addEventListener('click',()=>{
  const t=target();
  if(!t){alert('Enter a number or SIP URI.');return;}
  post('/api/call',{target:t});
});
$('bhang').addEventListener('click',()=>post('/api/hangup'));
$('bdel').addEventListener('click',()=>{
  $('num').value='';
  $('contacts').value='';
});
$('contacts').addEventListener('change',ev=>{
  if(ev.target.value) $('num').value=ev.target.value;
});
$('num').addEventListener('keydown',ev=>{
  if(ev.key==='Enter') $('bcall').click();
});
document.querySelectorAll('.dp').forEach(b=>{
  b.addEventListener('click',()=>{
    const d=b.dataset.d;
    if(inCall) post('/api/dtmf',{digit:d});
    else $('num').value+=(d==='*'?'*':d);
  });
});
$('btestreg').addEventListener('click',()=>post('/api/testreg'));
$('bclr').addEventListener('click',()=>{ $('loglist').innerHTML=''; });

// ─── audio mode ──────────────────────────────────────────────────────────────
function applyAudioModeUi(mode, customWav, availableWavs) {
  // Rebuild wav dropdown
  const sel = document.getElementById('wavSel');
  if (availableWavs) {
    sel.innerHTML = '';
    availableWavs.forEach(f => {
      const o = document.createElement('option');
      o.value = o.textContent = f;
      sel.appendChild(o);
    });
  }
  // Set radio
  document.querySelectorAll('input[name=audioMode]').forEach(r => {
    r.checked = r.value === mode;
  });
  sel.disabled = mode !== 'CustomWav';
  if (customWav) sel.value = customWav;
}

function saveAudioMode() {
  const mode = document.querySelector('input[name=audioMode]:checked')?.value || 'Standard';
  const wav  = document.getElementById('wavSel').value;
  fetch('/api/audiomode', {
    method: 'POST',
    headers: {'Content-Type':'application/json'},
    body: JSON.stringify({ mode, customWav: wav })
  });
}

document.querySelectorAll('input[name=audioMode]').forEach(r => {
  r.addEventListener('change', () => {
    document.getElementById('wavSel').disabled = r.value !== 'CustomWav';
    saveAudioMode();
  });
});
document.getElementById('wavSel').addEventListener('change', saveAudioMode);

// ─── boot ─────────────────────────────────────────────────────────────────────
document.getElementById('pageUrl').textContent = location.href;
// Load initial audio mode
fetch('/api/audiomode').then(r=>r.json()).then(d=>{
  applyAudioModeUi(d.mode, d.customWav, d.availableWavs);
}).catch(()=>{});
connect();
</script>
</body>
</html>
""";

        public void Dispose() { Stop(); _listener.Close(); }
    }
}
