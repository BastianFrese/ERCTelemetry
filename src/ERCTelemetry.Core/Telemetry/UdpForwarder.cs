using System.Net;
using System.Net.Sockets;
using ERCTelemetry.Core.Settings;

namespace ERCTelemetry.Core.Telemetry;

/// <summary>Re-sends every raw telemetry datagram to configured UDP destinations so other
/// apps (RaceLab, SimHub, …) can consume the same stream the game sends to this app.
/// Targets are read live from the settings provider on every <see cref="Forward"/> — a
/// settings change applies without a restart. A failing target never throws into the
/// caller (the UDP receive loop); errors are counted instead.</summary>
public sealed class UdpForwarder : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly object _gate = new();
    private UdpClient? _client;
    private IPEndPoint[] _endpoints = [];
    private IReadOnlyList<ForwardTarget>? _lastTargets;
    private int _disposed;

    public UdpForwarder(Func<AppSettings> settings)
    {
        _settings = settings;
    }

    /// <summary>Datagrams successfully sent to at least one enabled target.</summary>
    public long ForwardedPackets { get; private set; }

    /// <summary>Send attempts that failed (unresolvable address, socket error).</summary>
    public long FailedSends { get; private set; }

    /// <summary>Sends one raw datagram to every enabled target. Cheap and fire-and-forget —
    /// safe to call from the UDP receive loop on every packet.</summary>
    public void Forward(ReadOnlyMemory<byte> data)
    {
        if (_disposed != 0)
        {
            return;
        }

        var settings = _settings();
        if (!settings.ForwardingEnabled || settings.ForwardTargets is not { Count: > 0 } targets)
        {
            return;
        }

        var endpoints = GetEndpoints(targets);
        if (endpoints.Length == 0)
        {
            return;
        }

        var client = GetClient();
        if (client is null)
        {
            return;
        }

        var sent = false;
        foreach (var endpoint in endpoints)
        {
            try
            {
                client.Send(data.Span, endpoint);
                sent = true;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // UDP send is fire-and-forget; only socket-level failures land here.
                FailedSends++;
            }
        }

        if (sent)
        {
            ForwardedPackets++;
        }
    }

    /// <summary>Resolves the enabled targets to endpoints, caching the result until the
    /// target list reference changes (settings are immutable records — a save produces a
    /// new list). Unresolvable hostnames are skipped, not thrown.</summary>
    private IPEndPoint[] GetEndpoints(IReadOnlyList<ForwardTarget> targets)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_lastTargets, targets))
            {
                return _endpoints;
            }

            var list = new List<IPEndPoint>(targets.Count);
            foreach (var target in targets)
            {
                if (!target.Enabled)
                {
                    continue;
                }

                if (IPAddress.TryParse(target.Address, out var ip))
                {
                    list.Add(new IPEndPoint(ip, target.Port));
                }
                else
                {
                    try
                    {
                        var addresses = Dns.GetHostAddresses(target.Address);
                        if (addresses.Length > 0)
                        {
                            list.Add(new IPEndPoint(addresses[0], target.Port));
                        }
                    }
                    catch (SocketException)
                    {
                        // Unresolvable hostname — the target is skipped this round.
                    }
                }
            }

            _lastTargets = targets;
            _endpoints = list.ToArray();
            return _endpoints;
        }
    }

    private UdpClient? GetClient()
    {
        lock (_gate)
        {
            if (_client is not null)
            {
                return _client;
            }

            try
            {
                _client = new UdpClient();
            }
            catch (SocketException)
            {
                return null;
            }

            return _client;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
