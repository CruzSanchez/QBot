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
        if (address is null) return false;

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

    private static IPAddress? FindTunnelAddress(string match) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && (n.Name.Contains(match, StringComparison.OrdinalIgnoreCase)
                            || n.Description.Contains(match, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
}
