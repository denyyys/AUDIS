// SipClientEngine.cs  –  Outbound SIP UA running on port 5061
// The AUDIS server keeps 5060; this client is fully independent.

using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AudisService
{
    public enum ClientCallState { Idle, Calling, Connected, Ending, Ended }

    public enum RegistrationState { Disabled, Registering, Registered, Failed, Unregistered }

    public class SipClientEngine
    {
        // ── Events ───────────────────────────────────────────────────────────────
        public event Action<LogLevel, string>?       OnLog;
        public event Action<ClientCallState>?        OnCallStateChanged;
        public event Action<string>?                 OnDtmfReceived;
        public event Action<RegistrationState, int>? OnRegistrationStateChanged;

        // ── State ────────────────────────────────────────────────────────────────
        public ClientCallState   State             { get; private set; } = ClientCallState.Idle;
        public RegistrationState RegState          { get; private set; } = RegistrationState.Disabled;
        public int               LastRegStatusCode { get; private set; } = 0;
        public SipClientConfig   Config            { get; private set; } = new();

        private SIPTransport?            _transport;
        private SIPUserAgent?            _ua;
        private VoIPMediaSession?        _rtpSession;
        private CancellationTokenSource? _callCts;
        private CancellationTokenSource? _regCts;
        private CallState?               _callState;

        public AudisConfig? ServerConfig { get; set; }

        /// <summary>
        /// When true every outbound call is mixed-down and saved as a WAV in the
        /// recordings/ folder — identical behaviour to the main AUDIS SipEngine.
        /// </summary>
        public bool IsGlobalRecordingEnabled { get; set; } = false;

        private string AudioDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio");
        private string RecDir   => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings");

        /// <summary>Returns all .wav filenames (no path) found in the audio folder.</summary>
        public string[] GetAudioFiles()
        {
            if (!Directory.Exists(AudioDir)) return Array.Empty<string>();
            return Directory.GetFiles(AudioDir, "*.wav")
                            .Select(Path.GetFileName)
                            .Where(f => f != null)
                            .Select(f => f!)
                            .OrderBy(f => f)
                            .ToArray();
        }

        /// <summary>
        /// Updates the audio mode on the live Config object and persists it.
        /// Safe to call from any thread (does not restart the SIP stack).
        /// </summary>
        public void SetAudioMode(SipAudioMode mode, string customWav)
        {
            Config.AudioMode = mode;
            Config.CustomWav = customWav;
            Config.Save();
            Log(LogLevel.Information,
                $"[AUDIO-MODE] {mode}" + (mode == SipAudioMode.CustomWav ? $" → {customWav}" : ""));
        }

        // ── Lifecycle ────────────────────────────────────────────────────────────
        public void Start(SipClientConfig config)
        {
            Config = config;

            _transport = new SIPTransport();
            var ep = new IPEndPoint(IPAddress.Any, config.LocalSipPort);

            if (config.Transport == SipTransportProtocol.TCP)
                _transport.AddSIPChannel(new SIPTCPChannel(ep));
            else
                _transport.AddSIPChannel(new SIPUDPChannel(ep));

            _transport.ContactHost = config.PublicIp;

            // Respond 200 OK to OPTIONS keepalive pings from Asterisk/FreePBX.
            // Without this the contact is marked "Unavail" and calls are never routed to us.
            _transport.SIPTransportRequestReceived += async (localEP, remoteEP, req) =>
            {
                if (req.Method == SIPMethodsEnum.OPTIONS)
                {
                    try
                    {
                        var resp = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, "OK");
                        resp.Header.Allow = "INVITE, ACK, CANCEL, OPTIONS, BYE";
                        await _transport.SendResponseAsync(resp);
                        Log(LogLevel.Debug, $"[OPTIONS] 200 OK → {remoteEP}");
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Warning, $"[OPTIONS] Reply failed: {ex.Message}");
                    }
                }
            };

            Log(LogLevel.Information,
                $"SIP transport started on port {config.LocalSipPort} ({config.Transport}) PublicIP={config.PublicIp}");
            SetState(ClientCallState.Idle);

            if (config.UseRegistration && !string.IsNullOrWhiteSpace(config.SipServer))
                StartRegistration();
            else
                SetRegState(RegistrationState.Disabled, 0);
        }

        public void Stop()
        {
            Log(LogLevel.Information, "SIP engine stopping...");
            HangUp();
            StopRegistration();
            _transport?.Shutdown();
            _transport = null;
            SetState(ClientCallState.Idle);
            SetRegState(RegistrationState.Disabled, 0);
            Log(LogLevel.Information, "SIP engine stopped");
        }

        // ── Registration — raw UDP REGISTER (no SIPRegistrationUserAgent) ────────
        // We build the REGISTER request manually and listen for the response.
        // This avoids any SIPRegistrationUserAgent constructor version issues.
        private void StartRegistration()
        {
            if (_transport == null) return;
            StopRegistration();

            _regCts = new CancellationTokenSource();
            var tok = _regCts.Token;

            Log(LogLevel.Information,
                $"[REG] Starting registration loop: {Config.Username}@{Config.SipServer}");
            SetRegState(RegistrationState.Registering, 0);

            _ = Task.Run(async () =>
            {
                int cseq        = 1;
                int successWait = 50_000;  // re-register before 60s expires
                int failWait    = 20_000;

                while (!tok.IsCancellationRequested)
                {
                    try
                    {
                        bool ok = await DoRegisterAsync(cseq, tok);
                        cseq++;

                        if (ok)
                        {
                            if (RegState != RegistrationState.Registered)
                            {
                                Log(LogLevel.Information, "[REG] ✓ Registered");
                                SetRegState(RegistrationState.Registered, 200);
                            }
                            await Task.Delay(successWait, tok);
                        }
                        else
                        {
                            Log(LogLevel.Warning, $"[REG] ✗ Failed (code={LastRegStatusCode})");
                            SetRegState(RegistrationState.Failed, LastRegStatusCode);
                            await Task.Delay(failWait, tok);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Warning, $"[REG] Error: {ex.Message}");
                        SetRegState(RegistrationState.Failed, 0);
                        try { await Task.Delay(20_000, tok); } catch { break; }
                    }
                }

                SetRegState(RegistrationState.Unregistered, 0);
                Log(LogLevel.Information, "[REG] Registration loop stopped");
            });
        }

        // Sends a REGISTER request and returns true on 200 OK.
        // Handles 401/407 digest challenge automatically.
        private async Task<bool> DoRegisterAsync(int cseq, CancellationToken tok)
        {
            if (_transport == null) return false;

            string server  = Config.SipServer.Trim();
            string user    = Config.Username.Trim();
            string pass    = Config.Password;
            string domain  = string.IsNullOrWhiteSpace(Config.Domain) ? server : Config.Domain.Trim();
            string display = string.IsNullOrWhiteSpace(Config.DisplayName) ? user : Config.DisplayName.Trim();
            string callId  = Guid.NewGuid().ToString("N");

            Log(LogLevel.Debug, $"[REG] REGISTER {user}@{server} (cseq={cseq})");

            // Resolve the registrar — split port before DNS so IP:port strings work
            int remotePort = 5060;
            string serverHost = server;
            if (server.Contains(':'))
            {
                var parts = server.Split(':');
                serverHost = parts[0];
                if (parts.Length > 1 && int.TryParse(parts[1], out int sp)) remotePort = sp;
            }

            IPAddress registrarIp;
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(serverHost);
                registrarIp = addrs[0];
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"[REG] DNS failed for '{serverHost}': {ex.Message}");
                LastRegStatusCode = 0;
                return false;
            }

            var remoteEP  = new IPEndPoint(registrarIp, remotePort);
            var localIP   = Config.PublicIp;
            int localPort = Config.LocalSipPort;
            var branchId  = "z9hG4bK" + Guid.NewGuid().ToString("N")[..12];
            var tag       = Guid.NewGuid().ToString("N")[..8];

            SIPResponse? firstResp  = await SendRawRegister(remoteEP, localIP, localPort,
                user, domain, display, server, callId, branchId, tag, cseq, null, tok);

            if (firstResp == null)
            {
                Log(LogLevel.Warning, "[REG] No response from server");
                LastRegStatusCode = 0;
                return false;
            }

            Log(LogLevel.Information, $"[REG] First response: {(int)firstResp.Status} {firstResp.ReasonPhrase}");
            if (firstResp.Status == SIPResponseStatusCodesEnum.Ok) { LastRegStatusCode = 200; return true; }

            // Digest auth challenge
            if (firstResp.Status == SIPResponseStatusCodesEnum.Unauthorised ||
                firstResp.Status == SIPResponseStatusCodesEnum.ProxyAuthenticationRequired)
            {
                Log(LogLevel.Information, $"[REG] {(int)firstResp.Status} — sending credentials");

                var authHeaders = firstResp.Header.AuthenticationHeaders;
                if (authHeaders == null || authHeaders.Count == 0)
                {
                    Log(LogLevel.Warning, "[REG] No auth header in challenge");
                    LastRegStatusCode = (int)firstResp.Status;
                    return false;
                }

                var ah     = authHeaders[0];
                string realm = ah.SIPDigest.Realm ?? domain;
                string nonce = ah.SIPDigest.Nonce ?? "";

                // Compute digest response
                string ha1 = MD5Hash($"{user}:{realm}:{pass}");
                string ha2 = MD5Hash($"REGISTER:{GetSipUri(user, domain)}");
                string dResp = MD5Hash($"{ha1}:{nonce}:{ha2}");

                string authStr = $"Digest username=\"{user}\", realm=\"{realm}\", " +
                                 $"nonce=\"{nonce}\", uri=\"{GetSipUri(user, domain)}\", " +
                                 $"algorithm=MD5, response=\"{dResp}\"";

                string branch2 = "z9hG4bK" + Guid.NewGuid().ToString("N")[..12];
                var secondResp = await SendRawRegister(remoteEP, localIP, localPort,
                    user, domain, display, server, callId, branch2, tag, cseq + 1, authStr, tok);

                if (secondResp == null)
                {
                    Log(LogLevel.Warning, "[REG] No response to auth REGISTER");
                    LastRegStatusCode = 0;
                    return false;
                }

                LastRegStatusCode = (int)secondResp.Status;
                if (secondResp.Status == SIPResponseStatusCodesEnum.Ok) return true;

                Log(LogLevel.Warning,
                    $"[REG] Auth failed: {(int)secondResp.Status} {secondResp.ReasonPhrase}");
                return false;
            }

            LastRegStatusCode = (int)firstResp.Status;
            Log(LogLevel.Warning,
                $"[REG] Unexpected: {(int)firstResp.Status} {firstResp.ReasonPhrase}");
            // Extra diagnostics to help identify mis-configuration
            try
            {
                if (firstResp.Header?.To != null)
                    Log(LogLevel.Debug, $"[REG]   Response To:      {firstResp.Header.To}");
                if (!string.IsNullOrEmpty(firstResp.Header?.Warning))
                    Log(LogLevel.Debug, $"[REG]   Warning header:   {firstResp.Header.Warning}");
                if (!string.IsNullOrEmpty(firstResp.Body))
                    Log(LogLevel.Debug, $"[REG]   Body:             {firstResp.Body}");
            }
            catch { }
            return false;
        }

        private static string GetSipUri(string user, string domain)
            => $"sip:{user}@{domain}";

        private static string MD5Hash(string input)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        // Sends a REGISTER via SIPTransport (same socket as 5061) so the response
        // comes back on the same port and fires SIPTransportResponseReceived.
        private async Task<SIPResponse?> SendRawRegister(
            IPEndPoint remoteEP, string localIP, int localPort,
            string user, string domain, string display, string server,
            string callId, string branch, string tag, int cseq,
            string? authHeader, CancellationToken tok)
        {
            if (_transport == null) return null;

            string contact = $"sip:{user}@{localIP}:{localPort}";
            string fromTo  = $"sip:{user}@{domain}";
            string via     = $"SIP/2.0/UDP {localIP}:{localPort};branch={branch};rport";

            // Build raw SIP string with \r\n line endings (required by SIP parser)
            var sb = new StringBuilder();
            sb.Append($"REGISTER sip:{server} SIP/2.0\r\n");
            sb.Append($"Via: {via}\r\n");
            sb.Append($"Max-Forwards: 70\r\n");
            sb.Append($"From: \"{display}\" <{fromTo}>;tag={tag}\r\n");
            sb.Append($"To: \"{display}\" <{fromTo}>\r\n");
            sb.Append($"Call-ID: {callId}\r\n");
            sb.Append($"CSeq: {cseq} REGISTER\r\n");
            sb.Append($"Contact: <{contact}>\r\n");
            sb.Append($"Expires: 60\r\n");
            sb.Append($"User-Agent: AudisService/1.4\r\n");
            if (authHeader != null)
                sb.Append($"Authorization: {authHeader}\r\n");
            sb.Append("Content-Length: 0\r\n");
            sb.Append("\r\n");

            string sipMsg = sb.ToString();
            Log(LogLevel.Information, $"[REG] Sending to {remoteEP}:\n{sipMsg.Replace("\r\n", " | ")}");

            SIPRequest req;
            try { req = SIPRequest.ParseSIPRequest(sipMsg); }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"[REG] Parse failed: {ex.Message}");
                return null;
            }

            var tcs = new TaskCompletionSource<SIPResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Task OnResp(SIPEndPoint rEP, SIPEndPoint lEP, SIPResponse r)
            {
                if (r.Header.CSeqMethod == SIPMethodsEnum.REGISTER &&
                    r.Header.CallId == callId)
                    tcs.TrySetResult(r);
                return Task.CompletedTask;
            }

            _transport.SIPTransportResponseReceived += OnResp;
            try
            {
                var destEP = new SIPEndPoint(SIPProtocolsEnum.udp, remoteEP);
                await _transport.SendRequestAsync(destEP, req);
                Log(LogLevel.Debug, $"[REG] REGISTER sent via SIPTransport to {remoteEP}");

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(tok);
                linked.CancelAfter(8000);
                return await tcs.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"[REG] Transport send error: {ex.Message}");
                return null;
            }
            finally { _transport.SIPTransportResponseReceived -= OnResp; }
        }

        private void StopRegistration()
        {
            _regCts?.Cancel();
            _regCts = null;
        }

        public async Task TestRegistrationAsync()
        {
            if (_transport == null) { Log(LogLevel.Error, "[REG] Engine not started"); return; }

            Log(LogLevel.Information, "[REG] === TEST REGISTRATION START ===");
            Log(LogLevel.Information, $"[REG] Server:   {Config.SipServer}");
            Log(LogLevel.Information, $"[REG] Username: {Config.Username}");
            Log(LogLevel.Information, $"[REG] Domain:   " +
                $"{(string.IsNullOrWhiteSpace(Config.Domain) ? "(same as server)" : Config.Domain)}");
            Log(LogLevel.Information, $"[REG] Password: " +
                $"{(string.IsNullOrEmpty(Config.Password) ? "(empty)" : "***")}");

            // Stop the background loop so it doesn't interfere with this single-shot test
            StopRegistration();
            await Task.Delay(150);

            // Run one registration attempt directly (no background loop)
            SetRegState(RegistrationState.Registering, 0);
            using var cts = new CancellationTokenSource(10_000);
            try
            {
                bool ok = await DoRegisterAsync(1, cts.Token);
                if (!ok && RegState != RegistrationState.Failed)
                    SetRegState(RegistrationState.Failed, LastRegStatusCode);
            }
            catch (OperationCanceledException)
            {
                Log(LogLevel.Warning, "[REG] Timed out — no response within 10 s");
            }

            Log(LogLevel.Information, $"[REG] Result: {RegState} (code={LastRegStatusCode})");
            Log(LogLevel.Information, "[REG] === TEST REGISTRATION END ===");

            // Restart the background loop after test
            StartRegistration();
        }

        // ── Outbound Call ────────────────────────────────────────────────────────
        public async Task CallAsync(string target)
        {
            if (_transport == null) { Log(LogLevel.Error, "[CALL] Engine not started"); return; }

            if (State != ClientCallState.Idle)
            {
                Log(LogLevel.Warning, $"[CALL] State is {State} — force-resetting to Idle");
                _callState?.Cancel();
                _callCts?.Cancel();
                if (_ua?.IsCallActive == true) { try { _ua.Hangup(); } catch { } }
                SetState(ClientCallState.Idle);
                await Task.Delay(300);
            }

            if (!target.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            {
                // If it's just an extension number with no domain, append the SIP server
                // so Asterisk knows where to route it (e.g. "103" → "103@192.168.100.177")
                if (!target.Contains('@') && Config.UseRegistration &&
                    !string.IsNullOrWhiteSpace(Config.SipServer))
                {
                    target = $"{target}@{Config.SipServer}";
                    Log(LogLevel.Debug, $"[CALL] Extension-only target, resolved to: {target}");
                }
                target = $"sip:{target}";
            }

            Log(LogLevel.Information, $"[CALL] ─── Calling {target} ───");
            Log(LogLevel.Information,
                $"[CALL] Auth: user={Config.Username ?? "(none)"} reg={Config.UseRegistration}");
            SetState(ClientCallState.Calling);

            _callCts   = new CancellationTokenSource();
            _callState = new CallState();

            // ── Recording buffers ─────────────────────────────────────────────────
            // Collected even when IsGlobalRecordingEnabled is false (zero overhead,
            // avoids having to start/stop recording mid-call).  Written to disk only
            // if the flag is set when the call ends.
            var incomingAudioBuffer = new List<byte>();   // raw µ-law from remote
            var outgoingAudioBuffer = new List<byte>();   // µ-law we send
            string callId = Guid.NewGuid().ToString("N")[..8];

            _ua         = new SIPUserAgent(_transport, null);
            _rtpSession = new VoIPMediaSession();
            _rtpSession.AcceptRtpFromAny = true;

            try { await _rtpSession.AudioExtrasSource.CloseAudio(); } catch { }

            string   lastDtmfKey  = "";
            DateTime lastDtmfTime = DateTime.MinValue;

            _ua.OnDtmfTone += (tone, dur) =>
            {
                string d = tone.ToString().Replace("Tone", "");
                if (d == "Star") d = "*"; if (d == "Pound") d = "#";
                if (d == "10")   d = "*"; if (d == "11")   d = "#";
                if (d == lastDtmfKey && (DateTime.Now - lastDtmfTime).TotalMilliseconds < 200) return;
                lastDtmfKey = d; lastDtmfTime = DateTime.Now;
                Log(LogLevel.Information, $"[DTMF←] SIP-INFO: {d}");
                _callState.LastDigit = d;
                OnDtmfReceived?.Invoke(d);
            };

            _rtpSession.OnRtpPacketReceived += (ep, type, pkt) =>
            {
                int pt = (int)type;

                // ── Record incoming audio (non-DTMF payload types) ──────────────
                if (pt <= 95 && pkt.Payload?.Length > 0)
                    lock (incomingAudioBuffer) incomingAudioBuffer.AddRange(pkt.Payload);

                if (pt >= 96 && pt <= 127 && pkt.Payload?.Length >= 4)
                {
                    byte eventId = pkt.Payload[0];
                    bool isEnd   = (pkt.Payload[1] & 0x80) != 0;
                    if (!isEnd) return;
                    string? d = ParseDtmf(eventId);
                    if (d == null) return;
                    if (d == lastDtmfKey && (DateTime.Now - lastDtmfTime).TotalMilliseconds < 200) return;
                    lastDtmfKey = d; lastDtmfTime = DateTime.Now;
                    Log(LogLevel.Information, $"[DTMF←] RFC2833: {d} (event={eventId})");
                    _callState.LastDigit = d;
                    OnDtmfReceived?.Invoke(d);
                }
            };

            _ua.OnCallHungup += _dlg =>
            {
                Log(LogLevel.Information, "[CALL] Remote hung up");
                _callState.IsActive = false;
                SetState(ClientCallState.Ended);
                _callCts?.Cancel();
                var _th = Task.Delay(600).ContinueWith(_c => SetState(ClientCallState.Idle));
            };

            string? user = Config.UseRegistration && !string.IsNullOrEmpty(Config.Username)
                ? Config.Username : null;
            string? pass = Config.UseRegistration && !string.IsNullOrEmpty(Config.Password)
                ? Config.Password : null;

            Log(LogLevel.Information, "[CALL] Sending INVITE...");
            bool answered;
            try
            {
                // HangUp() sends SIP CANCEL via _ua.Cancel() + cancels _callCts
                answered = await _ua.Call(target, user, pass, _rtpSession);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"[CALL] INVITE failed: {ex.GetType().Name}: {ex.Message}");
                SetState(ClientCallState.Ended);
                await Task.Delay(400);
                SetState(ClientCallState.Idle);
                return;
            }

            if (!answered)
            {
                // If HangUp() was called, state is already Idle – don't re-set it
                if (State != ClientCallState.Idle)
                {
                    Log(LogLevel.Warning, "[CALL] Not answered / rejected");
                    SetState(ClientCallState.Ended);
                    await Task.Delay(400);
                    SetState(ClientCallState.Idle);
                }
                return;
            }

            SetState(ClientCallState.Connected);
            Log(LogLevel.Information, "[CALL] ✓ Connected — playing greeting");
            Log(LogLevel.Information, $"[REC] Recording: {(Config.RecordCalls ? "ON" : "OFF")}");

            // ── Audio mode ─────────────────────────────────────────────────────────
            // Standard : play eliska.wav then route DTMF via ServerConfig.KeyMappings
            // CustomWav: play the operator-chosen .wav instead, DTMF routing unchanged.
            string greetingFile;
            if (Config.AudioMode == SipAudioMode.CustomWav &&
                !string.IsNullOrWhiteSpace(Config.CustomWav))
            {
                greetingFile = Config.CustomWav;
                Log(LogLevel.Information, $"[AUDIO-MODE] CustomWav → {greetingFile}");
            }
            else
            {
                greetingFile = "eliska.wav";
                Log(LogLevel.Information, "[AUDIO-MODE] Standard → eliska.wav");
            }

            try
            {
                await PlayFile(_rtpSession, greetingFile, _callState, outgoingAudioBuffer);

                while (_callState.IsActive && !_callCts.Token.IsCancellationRequested)
                {
                    string? digit = _callState.ConsumeDigit();
                    if (digit == null) { await Task.Delay(50); continue; }
                    Log(LogLevel.Information, $"[CALL] Key: {digit}");
                    await HandleDigit(_rtpSession, digit, _callState, outgoingAudioBuffer);
                }
            }
            catch (OperationCanceledException) { Log(LogLevel.Information, "[CALL] Cancelled"); }
            catch (Exception ex)               { Log(LogLevel.Error, $"[CALL] Session error: {ex.Message}"); }
            finally
            {
                if (_ua.IsCallActive)
                {
                    Log(LogLevel.Information, "[CALL] Sending BYE");
                    _ua.Hangup();
                }

                // ── Save recording ────────────────────────────────────────────────
                if (Config.RecordCalls &&
                    (incomingAudioBuffer.Count > 0 || outgoingAudioBuffer.Count > 0))
                {
                    try
                    {
                        Directory.CreateDirectory(RecDir);
                        string fname = Path.Combine(RecDir,
                            $"client_call_{callId}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                        byte[] mixed = MixAudioBuffers(
                            incomingAudioBuffer.ToArray(),
                            outgoingAudioBuffer.ToArray());
                        SaveWavFile(fname, mixed);
                        Log(LogLevel.Information,
                            $"[REC] Saved → {Path.GetFileName(fname)} " +
                            $"({mixed.Length} bytes, in={incomingAudioBuffer.Count} out={outgoingAudioBuffer.Count})");
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Error, $"[REC] Save failed: {ex.Message}");
                    }
                }

                SetState(ClientCallState.Ended);
                await Task.Delay(400);
                SetState(ClientCallState.Idle);
                Log(LogLevel.Information, "[CALL] ─── Ended — ready ───");
            }
        }

        private async Task HandleDigit(VoIPMediaSession session, string digit, CallState state,
                                       List<byte> outBuf)
        {
            if (ServerConfig?.KeyMappings != null &&
                ServerConfig.KeyMappings.TryGetValue(digit, out string? action) &&
                !string.IsNullOrEmpty(action) &&
                !action.StartsWith("SYSTEM_") && !action.StartsWith("AI_") &&
                !action.StartsWith("INFO_")   && !action.StartsWith("VOICEMAIL"))
            {
                Log(LogLevel.Information, $"[CALL] Key {digit} → {action}");
                await PlayFile(session, action, state, outBuf);
            }
            else Log(LogLevel.Debug, $"[CALL] Key {digit} → no audio mapping");
        }

        // ── HangUp ──────────────────────────────────────────────────────────────
        public void HangUp()
        {
            Log(LogLevel.Information, $"[CALL] HangUp (state={State})");

            if (State == ClientCallState.Calling)
            {
                // Pre-answer: send CANCEL to abort the INVITE
                try { _ua?.Cancel(); Log(LogLevel.Information, "[CALL] CANCEL sent"); } catch { }
            }
            else if (_ua?.IsCallActive == true)
            {
                SetState(ClientCallState.Ending);
                try { _ua.Hangup(); } catch { }
            }

            _callState?.Cancel();
            _callCts?.Cancel();     // also unblocks _ua.Call() via the token

            if (State != ClientCallState.Idle)
            {
                SetState(ClientCallState.Ended);
                var _th = Task.Delay(400).ContinueWith(_c => SetState(ClientCallState.Idle));
            }
        }

        // ── DTMF ─────────────────────────────────────────────────────────────────
        public void SendDtmf(char digit)
        {
            if (_ua == null || !_ua.IsCallActive || _transport == null) return;
            byte eventId = digit switch
            {
                '0'=>0,'1'=>1,'2'=>2,'3'=>3,'4'=>4,'5'=>5,'6'=>6,'7'=>7,'8'=>8,'9'=>9,
                '*'=>10,'#'=>11, _=>255
            };
            if (eventId == 255) return;
            Log(LogLevel.Debug, $"[DTMF→] {digit} via {Config.DtmfMethod}");
            if (Config.DtmfMethod == DtmfMethod.SipInfo) _ = SendDtmfSipInfoAsync(digit);
            else SendDtmfRfc2833(eventId);
        }

        private async Task SendDtmfSipInfoAsync(char digit)
        {
            try
            {
                if (_ua?.Dialogue == null || _transport == null) return;
                var req = _ua.Dialogue.GetInDialogRequest(SIPMethodsEnum.INFO);
                req.Header.ContentType   = "application/dtmf-relay";
                req.Body = $"Signal={digit}\r\nDuration=160\r\n";
                req.Header.ContentLength = Encoding.UTF8.GetByteCount(req.Body);
                await _transport.SendRequestAsync(req);
                Log(LogLevel.Information, $"[DTMF→] SIP INFO: {digit}");
            }
            catch (Exception ex) { Log(LogLevel.Error, $"[DTMF→] SIP INFO failed: {ex.Message}"); }
        }

        private void SendDtmfRfc2833(byte eventId)
        {
            if (_rtpSession == null) return;
            try
            {
                for (int i = 0; i < 3; i++)
                    _rtpSession.SendAudio(160, MuLawSilence());
                Log(LogLevel.Information, $"[DTMF→] RFC2833: event={eventId}");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"[DTMF→] RFC2833 failed, using SIP INFO: {ex.Message}");
                _ = SendDtmfSipInfoAsync((char)('0' + eventId));
            }
        }

        // ── Audio playback ────────────────────────────────────────────────────────
        private async Task PlayFile(VoIPMediaSession session, string filename, CallState state,
                                    List<byte> outBuf)
        {
            string path = Path.Combine(AudioDir, filename);
            if (!File.Exists(path)) { Log(LogLevel.Warning, $"[AUDIO] Not found: {path}"); return; }
            byte[] pcm;
            try   { pcm = File.ReadAllBytes(path); }
            catch { Log(LogLevel.Error, $"[AUDIO] Cannot read: {path}"); return; }
            Log(LogLevel.Information, $"[AUDIO] Playing {filename} ({pcm.Length} bytes)");
            await PlayPcmBytes(session, pcm, state, outBuf);
        }

        private async Task PlayPcmBytes(VoIPMediaSession session, byte[] pcmData, CallState state,
                                        List<byte> outBuf)
        {
            if (pcmData == null || pcmData.Length == 0) return;
            int pos = 0;
            if (pcmData.Length > 44 && pcmData[0]=='R' && pcmData[1]=='I' &&
                pcmData[2]=='F' && pcmData[3]=='F')
                pos = 44;

            const int  BPP = 320;
            const int  SPP = 160;
            const long TPP = 200000;
            var  sw = Stopwatch.StartNew();
            long n  = 0;

            while (pos + BPP <= pcmData.Length && state.IsActive &&
                   state.LastDigitPeek() == null &&
                   _callCts?.Token.IsCancellationRequested == false)
            {
                byte[] g711 = new byte[SPP];
                for (int i = 0; i < BPP; i += 2)
                {
                    short s = (short)(pcmData[pos+i] | (pcmData[pos+i+1]<<8));
                    g711[i/2] = MuLawEncoder.LinearToMuLawSample(s);
                }
                session.SendAudio((uint)SPP, g711);

                // Capture outgoing audio for recording regardless of flag —
                // the buffer is only written to disk in CallAsync when the flag is set.
                lock (outBuf) outBuf.AddRange(g711);

                pos += BPP; n++;

                long wait = n * TPP - sw.ElapsedTicks;
                if (wait > 0)
                {
                    int ms = (int)(wait / 10000);
                    for (int c = 0; c < ms/5; c++) { await Task.Delay(5); if (!state.IsActive) return; }
                    if (ms % 5 > 0) await Task.Delay(ms % 5);
                }
            }
        }

        // ── Mix + Save (same algorithm as SipEngine) ──────────────────────────────
        private byte[] MixAudioBuffers(byte[] incoming, byte[] outgoing)
        {
            int maxLen = Math.Max(incoming.Length, outgoing.Length);
            byte[] mixed = new byte[maxLen];
            for (int i = 0; i < maxLen; i++)
            {
                byte  inB  = i < incoming.Length ? incoming[i] : (byte)0xFF;
                byte  outB = i < outgoing.Length ? outgoing[i] : (byte)0xFF;
                short inP  = MuLawDecoder.MuLawToLinearSample(inB);
                short outP = MuLawDecoder.MuLawToLinearSample(outB);
                int   mix  = Math.Clamp((inP + outP) / 2, short.MinValue, short.MaxValue);
                mixed[i]   = MuLawEncoder.LinearToMuLawSample((short)mix);
            }
            return mixed;
        }

        private void SaveWavFile(string filepath, byte[] mulawData)
        {
            using var fs = new FileStream(filepath, FileMode.Create);
            using var bw = new BinaryWriter(fs);
            byte[] pcm = new byte[mulawData.Length * 2];
            for (int i = 0; i < mulawData.Length; i++)
            {
                short s = MuLawDecoder.MuLawToLinearSample(mulawData[i]);
                pcm[i * 2]     = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)(s >> 8);
            }
            bw.Write(new char[] { 'R','I','F','F' });
            bw.Write(36 + pcm.Length);
            bw.Write(new char[] { 'W','A','V','E' });
            bw.Write(new char[] { 'f','m','t',' ' });
            bw.Write(16); bw.Write((short)1); bw.Write((short)1);
            bw.Write(8000); bw.Write(16000); bw.Write((short)2); bw.Write((short)16);
            bw.Write(new char[] { 'd','a','t','a' });
            bw.Write(pcm.Length);
            bw.Write(pcm);
        }
        // ── Helpers ──────────────────────────────────────────────────────────────
        private static byte[] MuLawSilence() => new byte[160].Also(b => Array.Fill(b, (byte)0xFF));

        private string? ParseDtmf(byte id) =>
            id <= 9 ? id.ToString() : id == 10 ? "*" : id == 11 ? "#" : null;

        private void SetState(ClientCallState s)
        {
            if (State == s) return;
            Log(LogLevel.Debug, $"[STATE] Call: {State}→{s}");
            State = s;
            OnCallStateChanged?.Invoke(s);
        }

        private void SetRegState(RegistrationState s, int code)
        {
            LastRegStatusCode = code;
            if (RegState == s && code == 0) return;
            Log(LogLevel.Debug, $"[STATE] Reg: {RegState}→{s} code={code}");
            RegState = s;
            OnRegistrationStateChanged?.Invoke(s, code);
        }

        private void Log(LogLevel lvl, string msg) => OnLog?.Invoke(lvl, msg);

        // ── Inner types ──────────────────────────────────────────────────────────
        private class CallState
        {
            private volatile bool _isActive = true;
            private string?       _digit;
            private readonly object _lock = new();
            public bool    IsActive        { get => _isActive; set => _isActive = value; }
            public string? LastDigitPeek() { lock(_lock) return _digit; }
            public string? ConsumeDigit()  { lock(_lock) { var d=_digit; _digit=null; return d; } }
            public string? LastDigit       { set { lock(_lock) _digit=value; } }
            public void    Cancel()        => _isActive = false;
        }
    }

    internal static class ArrayExt
    {
        public static T[] Also<T>(this T[] arr, Action<T[]> action) { action(arr); return arr; }
    }
}
