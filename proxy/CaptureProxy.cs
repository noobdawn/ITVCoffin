using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ITVCoffin.Proxy;

// Capture mode: transparent MITM proxy.
//   HTTP -> forwarded to the real official HTTPS hosts (client speaks plain HTTP to us)
//   TCP  -> forwarded to the real official game server
//
// All traffic is logged to <captureDir>/tcp_<timestamp>.log. That log is the parse entry
// point for scripts/parse_capture.py and its Data-line format MUST stay stable:
//
//   <HH:mm:ss.fff> [C->S|S->C] Data type=<Type> id=<id> route='<route>' dataLen=<n> data=<HEX>
//
// Everything (ports, upstream hosts, capture directory) comes from ProxyConfig.
public static class CaptureProxy
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    private static StreamWriter? _tcpLog;
    private static readonly object LogLock = new();

    private static void Log(string line)
    {
        lock (LogLock)
        {
            Console.WriteLine(line);
            _tcpLog?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        }
    }

    // ---------------- TCP proxy ----------------

    public static async Task RunTcpAsync(ProxyConfig cfg, CancellationToken ct)
    {
        Directory.CreateDirectory(cfg.ResolvedCaptureDir);
        _tcpLog = new StreamWriter(Path.Combine(cfg.ResolvedCaptureDir, $"tcp_{DateTime.Now:yyyyMMdd_HHmmss}.log"), true) { AutoFlush = true };

        var listener = new TcpListener(IPAddress.Any, cfg.TcpPort);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Log($"[proxy] BIND FAILED TCP 0.0.0.0:{cfg.TcpPort}: {ex.Message}");
            throw;
        }
        Log($"[proxy] TCP 0.0.0.0:{cfg.TcpPort}  ->  {cfg.OfficialTcpHost}:{cfg.OfficialTcpPort}");

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            _ = Task.Run(() => RelayTcp(client, cfg), ct);
        }
    }

    private static async Task RelayTcp(TcpClient client, ProxyConfig cfg)
    {
        using var _ = client;
        client.NoDelay = true;
        var upstream = new TcpClient { NoDelay = true };
        try
        {
            await upstream.ConnectAsync(cfg.OfficialTcpHost, cfg.OfficialTcpPort);
        }
        catch (Exception ex)
        {
            Log($"[proxy] upstream connect failed ({cfg.OfficialTcpHost}:{cfg.OfficialTcpPort}): {ex.Message}");
            return;
        }
        Log($"[proxy] client {client.Client.RemoteEndPoint} connected, upstream ok");

        var cs = client.GetStream();
        var us = upstream.GetStream();
        var c2s = new FrameReader("C->S");
        var s2c = new FrameReader("S->C");

        var t1 = Pump(cs, us, c2s);
        var t2 = Pump(us, cs, s2c);
        await Task.WhenAny(t1, t2);
        upstream.Close();
        Log("[proxy] session closed");
    }

    private static async Task Pump(NetworkStream from, NetworkStream to, FrameReader reader)
    {
        var buf = new byte[65536];
        try
        {
            while (true)
            {
                int n = await from.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                var chunk = buf[..n];
                await to.WriteAsync(chunk);
                reader.Feed(chunk);
            }
        }
        catch { }
    }

    // Reassembles the [1B type][3B len][payload] frames out of a byte stream and logs them.
    private sealed class FrameReader
    {
        private readonly string _tag;
        private readonly List<byte> _buf = new();
        public FrameReader(string tag) => _tag = tag;

        public void Feed(byte[] chunk)
        {
            _buf.AddRange(chunk);
            while (_buf.Count >= 4)
            {
                int len = (_buf[1] << 16) | (_buf[2] << 8) | _buf[3];
                if (_buf.Count < 4 + len) break;
                var type = (PacketType)_buf[0];
                var payload = _buf.GetRange(4, len).ToArray();
                _buf.RemoveRange(0, 4 + len);

                if (type == PacketType.Data)
                {
                    var c = Wire.DecodeContract(payload);
                    if (c != null)
                    {
                        var cc = c.Value;
                        Log($"[{_tag}] Data type={cc.Type} id={cc.Id} route='{cc.Route}' dataLen={cc.Data.Length} data={Convert.ToHexString(cc.Data)}");
                        continue;
                    }
                }
                Log($"[{_tag}] {type} len={len} {(type == PacketType.Handshake ? Encoding.UTF8.GetString(payload) : Convert.ToHexString(payload))}");
            }
        }
    }

    // ---------------- HTTP proxy ----------------

    public static async Task RunHttpAsync(ProxyConfig cfg, CancellationToken ct)
    {
        Directory.CreateDirectory(cfg.ResolvedCaptureDir);
        var listener = new TcpListener(IPAddress.Loopback, cfg.HttpPort);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Log($"[proxy] BIND FAILED HTTP 127.0.0.1:{cfg.HttpPort}: {ex.Message}");
            throw;
        }
        Log($"[proxy] HTTP 127.0.0.1:{cfg.HttpPort}");

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            _ = Task.Run(() => HandleHttp(client, cfg), ct);
        }
    }

    private static async Task HandleHttp(TcpClient client, ProxyConfig cfg)
    {
        using var _ = client;
        try
        {
            var s = client.GetStream();
            var head = await ReadUntilAsync(s, "\r\n\r\n");
            if (head == null) return;
            var headText = Encoding.ASCII.GetString(head);
            var lines = headText.Split("\r\n");
            var reqLine = lines[0].Split(' ');
            var method = reqLine[0];
            var path = reqLine.Length > 1 ? reqLine[1] : "/";

            int contentLength = 0;
            string? authorization = null;
            foreach (var line in lines.Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var name = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out contentLength);
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) authorization = val;
            }
            byte[] body = Array.Empty<byte>();
            if (contentLength > 0)
            {
                body = new byte[contentLength];
                await ReadExactlyAsync(s, body, contentLength);
            }
            var bodyText = Encoding.UTF8.GetString(body);

            var host = PickHost(path, bodyText, cfg);
            Log($"[http] >>> {method} {path} host={host} auth={(authorization == null ? "-" : authorization[..Math.Min(24, authorization.Length)] + "...")} body={bodyText}");

            int code; string ctype; string respText;
            if (path == "/server")
            {
                // Force an empty cluster list so the client keeps using our patched 127.0.0.1 TCP target.
                code = 200; ctype = "application/json"; respText = "{\"ips\":[]}";
                Log("[http] <<< (forced empty cluster ips) " + respText);
            }
            else
            {
                (code, ctype, respText) = await ForwardAsync(method, host, path, bodyText, authorization);
                Log($"[http] <<< {code} {ctype} {respText}");
            }

            var respBytes = Encoding.UTF8.GetBytes(respText);
            var hdr = $"HTTP/1.1 {code} OK\r\nContent-Type: {ctype}\r\nContent-Length: {respBytes.Length}\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(hdr));
            await s.WriteAsync(respBytes);
            await s.FlushAsync();
        }
        catch (Exception ex)
        {
            Log($"[http] err: {ex.Message}");
        }
    }

    private static string PickHost(string path, string body, ProxyConfig cfg)
    {
        // OfficialIP endpoints
        if (path == "/login" && !body.Contains("current_token")) return cfg.OfficialHost;
        if (path is "/ping" or "/sms_send" or "/verify" or "/wlc_check") return cfg.OfficialHost;
        if (path == "/server") return cfg.HttpHost;
        return cfg.HttpHost;
    }

    private static async Task<(int, string, string)> ForwardAsync(string method, string host, string path, string body, string? authorization)
    {
        var url = $"https://{host}{path}";
        try
        {
            using var req = new HttpRequestMessage(method == "GET" ? HttpMethod.Get : HttpMethod.Post, url);
            if (method != "GET")
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            if (!string.IsNullOrEmpty(authorization))
                req.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var resp = await Http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            var ct = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            return ((int)resp.StatusCode, ct, text);
        }
        catch (Exception ex)
        {
            Log($"[http] upstream error: {ex.Message}");
            return (502, "text/plain", "");
        }
    }

    private static async Task<byte[]?> ReadUntilAsync(NetworkStream s, string delim)
    {
        var delimBytes = Encoding.ASCII.GetBytes(delim);
        var buf = new List<byte>(1024);
        var tail = new Queue<byte>();
        while (buf.Count < 65536)
        {
            var one = new byte[1];
            int n = await s.ReadAsync(one, 0, 1);
            if (n <= 0) return null;
            buf.Add(one[0]);
            tail.Enqueue(one[0]);
            if (tail.Count > delimBytes.Length) tail.Dequeue();
            if (tail.Count == delimBytes.Length && tail.SequenceEqual(delimBytes))
                return buf.ToArray();
        }
        return null;
    }

    private static async Task ReadExactlyAsync(NetworkStream s, byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            int n = await s.ReadAsync(buf, off, count - off);
            if (n <= 0) throw new EndOfStreamException();
            off += n;
        }
    }
}
