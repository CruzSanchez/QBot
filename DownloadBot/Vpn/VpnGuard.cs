using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DownloadBot.Vpn;

public sealed class VpnOptions
{
    // Off = every download command works as before.
    public bool Enabled { get; set; } = false;

    // Substring of the VPN tunnel adapter's name/description (ExpressVPN's is "ExpressVPNOpenVPN WinTUN Adapter").
    public string AdapterMatch { get; set; } = "ExpressVPN";

    // Fetched *from the tunnel adapter's IP*; any 2xx answer proves traffic really flows through the VPN.
    public string ProbeUrl { get; set; } = "https://api.ipify.org";

    // ExpressVPN's WireGuard protocol creates no tunnel adapter, so there is nothing to bind a probe to. When no adapter
    // is found and this points at expressvpnctl.exe, ask ExpressVPN itself. Empty = adapter probe only.
    public string ControlCommand { get; set; } = "";

    // Split-tunnel entry that must route through the VPN for downloads to be protected.
    public string SplitTunnelApp { get; set; } = "qBittorrent";
}

public interface IVpnGuard
{
    Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default);
}

// With ExpressVPN split tunneling only chosen apps (qBittorrent) use the tunnel, so the bot's own public IP is
// always the home one and tells us nothing. Instead: find the tunnel adapter, bind a request to its address
// (which Windows routes through the tunnel) and see whether it gets an answer. Fails closed.
public sealed class VpnGuard(IOptions<VpnOptions> options, ILogger<VpnGuard> logger) : IVpnGuard
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2); // must fit Discord's 3 s ack window

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _connected;
    private DateTime _checkedAt = DateTime.MinValue;

    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) return true;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTime.UtcNow - _checkedAt < CacheTtl) return _connected;

            var connected = await ProbeAsync(cancellationToken);
            if (connected != _connected && _checkedAt != DateTime.MinValue)
                logger.LogWarning("VPN is now {State}", connected ? "connected" : "DISCONNECTED");
            _connected = connected;
            _checkedAt = DateTime.UtcNow;
            return connected;
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var address = FindTunnelAddress(opts.AdapterMatch);
        if (address is null)
            return !string.IsNullOrWhiteSpace(opts.ControlCommand) && await ProbeControlAsync(opts, cancellationToken);

        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        socket.Bind(new IPEndPoint(address, 0));
                        await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            using var http = new HttpClient(handler) { Timeout = ProbeTimeout };
            using var response = await http.GetAsync(opts.ProbeUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            logger.LogDebug(ex, "VPN probe through {Address} failed", address);
            return false;
        }
    }

    // Connected AND the download app is set to use the VPN in split tunneling (otherwise "connected" proves nothing).
    public static bool IsProtected(string connectionState, string splitApps, string app) =>
        connectionState.Trim().Equals("Connected", StringComparison.OrdinalIgnoreCase)
        && splitApps.Split(['\r', '\n', '|']).Any(l =>
            l.Trim().StartsWith("vpn:", StringComparison.OrdinalIgnoreCase)
            && l.Contains(app, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> ProbeControlAsync(VpnOptions opts, CancellationToken cancellationToken)
    {
        try
        {
            var state = await RunControlAsync(opts.ControlCommand, "get connectionstate", cancellationToken);
            if (!state.Trim().Equals("Connected", StringComparison.OrdinalIgnoreCase)) return false;
            var apps = await RunControlAsync(opts.ControlCommand, "get split-app", cancellationToken);
            return IsProtected(state, apps, opts.SplitTunnelApp);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException
                                       or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            logger.LogDebug(ex, "VPN control probe failed");
            return false;
        }
    }

    private static async Task<string> RunControlAsync(string exe, string args, CancellationToken cancellationToken)
    {
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("expressvpnctl did not start");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);
        try { return await proc.StandardOutput.ReadToEndAsync(cts.Token); }
        finally { if (!proc.HasExited) proc.Kill(); }
    }

    private static IPAddress? FindTunnelAddress(string match) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && (n.Name.Contains(match, StringComparison.OrdinalIgnoreCase)
                            || n.Description.Contains(match, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
}
