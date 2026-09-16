using System.Text.Json;

namespace ITVCoffin.Proxy;

// Runtime configuration. Precedence: CLI flag > config file > built-in default.
// All paths default to relative locations so the tool is portable.
public sealed class ProxyConfig
{
    public int TcpPort { get; set; } = 25313;
    public int HttpPort { get; set; } = 18080;
    public string OfficialTcpHost { get; set; } = "1.13.127.58";
    public int OfficialTcpPort { get; set; } = 30531;
    public string HttpHost { get; set; } = "cweb.jinzhangshu.com";
    public string OfficialHost { get; set; } = "official.jinzhangshu.com";
    public string CaptureDir { get; set; } = "captures";

    // Set when a config file was actually used (for the startup banner).
    public string? ConfigPath { get; private set; }

    private string? _resolvedCaptureDir;
    public string ResolvedCaptureDir => _resolvedCaptureDir ??= Path.GetFullPath(CaptureDir);

    public static ProxyConfig Load(string[] args)
    {
        var cfg = new ProxyConfig();

        var cfgPath = ArgValue(args, "--config");
        if (cfgPath == null && File.Exists("config.json"))
            cfgPath = "config.json";
        if (cfgPath != null)
        {
            if (!File.Exists(cfgPath))
                throw new FileNotFoundException($"config file not found: {cfgPath}");
            cfg.ApplyJson(File.ReadAllText(cfgPath));
            cfg.ConfigPath = Path.GetFullPath(cfgPath);
        }

        // CLI overrides win over the config file.
        cfg.TcpPort = ArgInt(args, "--tcp-port", cfg.TcpPort);
        cfg.HttpPort = ArgInt(args, "--http-port", cfg.HttpPort);
        cfg.OfficialTcpHost = ArgValue(args, "--official-tcp-host") ?? cfg.OfficialTcpHost;
        cfg.OfficialTcpPort = ArgInt(args, "--official-tcp-port", cfg.OfficialTcpPort);
        cfg.HttpHost = ArgValue(args, "--http-host") ?? cfg.HttpHost;
        cfg.OfficialHost = ArgValue(args, "--official-host") ?? cfg.OfficialHost;
        cfg.CaptureDir = ArgValue(args, "--capture-dir") ?? cfg.CaptureDir;
        return cfg;
    }

    private void ApplyJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return;
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name.ToLowerInvariant())
            {
                case "tcpport": if (p.Value.TryGetInt32(out var tp)) TcpPort = tp; break;
                case "httpport": if (p.Value.TryGetInt32(out var hp)) HttpPort = hp; break;
                case "officialtcphost": if (p.Value.ValueKind == JsonValueKind.String) OfficialTcpHost = p.Value.GetString() ?? OfficialTcpHost; break;
                case "officialtcpport": if (p.Value.TryGetInt32(out var op)) OfficialTcpPort = op; break;
                case "httphost": if (p.Value.ValueKind == JsonValueKind.String) HttpHost = p.Value.GetString() ?? HttpHost; break;
                case "officialhost": if (p.Value.ValueKind == JsonValueKind.String) OfficialHost = p.Value.GetString() ?? OfficialHost; break;
                case "capturedir": if (p.Value.ValueKind == JsonValueKind.String) CaptureDir = p.Value.GetString() ?? CaptureDir; break;
            }
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }

    private static int ArgInt(string[] args, string name, int def)
    {
        var v = ArgValue(args, name);
        return v != null && int.TryParse(v, out var n) ? n : def;
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        ProxyConfig cfg;
        try
        {
            cfg = ProxyConfig.Load(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] {ex.Message}");
            return 2;
        }

        Console.WriteLine("=========================================================");
        Console.WriteLine("  ITVCoffin (驱入虚空棺材) — capture proxy");
        Console.WriteLine("=========================================================");
        Console.WriteLine($"  TCP listen    : 0.0.0.0:{cfg.TcpPort}");
        Console.WriteLine($"  TCP upstream  : {cfg.OfficialTcpHost}:{cfg.OfficialTcpPort}");
        Console.WriteLine($"  HTTP listen   : 127.0.0.1:{cfg.HttpPort}");
        Console.WriteLine($"  HTTP upstream : {cfg.HttpHost} / {cfg.OfficialHost}");
        Console.WriteLine($"  capture dir   : {cfg.ResolvedCaptureDir}");
        if (cfg.ConfigPath != null)
            Console.WriteLine($"  config file   : {cfg.ConfigPath}");
        Console.WriteLine("  Press Ctrl+C to stop.");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var httpTask = CaptureProxy.RunHttpAsync(cfg, cts.Token);
        var tcpTask = CaptureProxy.RunTcpAsync(cfg, cts.Token);

        // A bind failure must be visible, not silently swallowed.
        var first = await Task.WhenAny(httpTask, tcpTask);
        if (first.IsFaulted)
        {
            Console.Error.WriteLine($"[fatal] {first.Exception?.GetBaseException().Message}");
            cts.Cancel();
            try { await Task.WhenAll(httpTask, tcpTask); } catch { /* shutting down */ }
            return 1;
        }

        try { await Task.WhenAll(httpTask, tcpTask); }
        catch (OperationCanceledException) { }
        Console.WriteLine("[proxy] stopped.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ITVCoffin proxy — capture official game traffic through a local MITM proxy.");
        Console.WriteLine();
        Console.WriteLine("usage: ITVCoffin.Proxy [options]");
        Console.WriteLine();
        Console.WriteLine("options:");
        Console.WriteLine("  --config <path>            JSON config file (default: ./config.json if present)");
        Console.WriteLine("  --tcp-port <n>             TCP listen port (default 25313)");
        Console.WriteLine("  --http-port <n>            HTTP listen port (default 18080)");
        Console.WriteLine("  --official-tcp-host <h>    upstream game TCP host (default 1.13.127.58)");
        Console.WriteLine("  --official-tcp-port <n>    upstream game TCP port (default 30531)");
        Console.WriteLine("  --http-host <h>            upstream HTTP host (default cweb.jinzhangshu.com)");
        Console.WriteLine("  --official-host <h>        upstream official host (default official.jinzhangshu.com)");
        Console.WriteLine("  --capture-dir <dir>        capture output dir (default captures)");
        Console.WriteLine("  -h, --help                 show this help");
    }
}
