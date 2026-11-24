using System.Net;
using System.Net.Sockets;
using System.Text;
using DesktopNMS.Core.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>The host's limit on requests in flight (#99), against a real loopback server so attempts and timeouts are the client's own.</summary>
public sealed class RequestGateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task No_more_requests_are_in_flight_than_the_gate_lets_through()
    {
        using var server = new SlowServer { Delay = TimeSpan.FromMilliseconds(50) };
        var gate = new CountingGate(slots: 2);
        using var transport = Transport(server, gate);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            using var answer = await transport.SendAsync(HttpMethod.Get, "devices/" + i);
        })).WaitAsync(Patience);

        Assert.Equal(12, server.Requests.Count);
        Assert.InRange(server.MostAtOnce, 1, 2);
        Assert.Equal(12, gate.Entered);
        Assert.Equal(12, gate.Left);
    }

    [Fact]
    public async Task Waiting_for_a_turn_does_not_count_towards_the_timeout()
    {
        // Five seconds is the shortest timeout a connection takes; the wait is longer.
        using var server = new SlowServer();
        var gate = new CountingGate(slots: 1);
        using var transport = Transport(server, gate, timeoutSeconds: 5);

        using var held = await gate.EnterAsync(CancellationToken.None);
        var request = transport.SendAsync(HttpMethod.Get, "system");
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.False(request.IsCompleted);
        Assert.Empty(server.Requests);

        held.Dispose();
        using var answer = await request.WaitAsync(Patience);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Each_retry_takes_its_own_turn()
    {
        using var server = new SlowServer();
        server.Statuses.Enqueue(HttpStatusCode.ServiceUnavailable);
        var gate = new CountingGate(slots: 1);
        using var transport = Transport(server, gate);

        using var answer = await transport.SendAsync(HttpMethod.Get, "system").WaitAsync(Patience);

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(2, gate.Entered);
        Assert.Equal(2, gate.Left);
    }

    [Fact]
    public async Task A_failed_request_gives_its_turn_back()
    {
        using var server = new SlowServer();
        server.Statuses.Enqueue(HttpStatusCode.NotFound);
        var gate = new CountingGate(slots: 1);
        using var transport = Transport(server, gate);

        await Assert.ThrowsAsync<LibreNmsApiException>(() => transport.SendAsync(HttpMethod.Get, "devices/1").WaitAsync(Patience));

        Assert.Equal(1, gate.Entered);
        Assert.Equal(1, gate.Left);
    }

    [Fact]
    public async Task Cancelling_the_wait_sends_nothing()
    {
        using var server = new SlowServer();
        var gate = new CountingGate(slots: 1);
        using var transport = Transport(server, gate);
        using var cancel = new CancellationTokenSource();

        using var held = await gate.EnterAsync(CancellationToken.None);
        var request = transport.SendAsync(HttpMethod.Get, "system", cancellationToken: cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Patience));
        Assert.Empty(server.Requests);
        Assert.Equal(1, gate.Entered); // Only the turn held above.
        Assert.Equal(0, gate.Left);
    }

    [Fact]
    public async Task A_request_waiting_when_the_connection_changes_goes_to_the_new_one()
    {
        using var first = new SlowServer();
        using var second = new SlowServer();
        var gate = new CountingGate(slots: 1);
        using var transport = Transport(first, gate);

        using var held = await gate.EnterAsync(CancellationToken.None);
        var request = transport.SendAsync(HttpMethod.Get, "system");
        transport.Configure(new LibreNmsConnection(second.Root, "token"));
        held.Dispose();

        using var answer = await request.WaitAsync(Patience);
        Assert.Empty(first.Requests);
        Assert.Single(second.Requests);
    }

    [Fact]
    public async Task Raw_and_web_file_requests_take_a_turn_too()
    {
        using var server = new SlowServer();
        var gate = new CountingGate(slots: 1);
        using var transport = Transport(server, gate);

        await transport.SendRawAsync("devices/1/graphs").WaitAsync(Patience);
        await transport.GetWebFileAsync("images/logo.png", 1024).WaitAsync(Patience);

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(2, gate.Entered);
        Assert.Equal(2, gate.Left);
    }

    [Fact]
    public async Task Unimus_and_Graylog_take_a_turn_for_each_request()
    {
        using var server = new SlowServer { Body = """{"data":[],"streams":[]}""" };
        var unimusGate = new CountingGate(slots: 1);
        using var unimus = new UnimusApi(NullLogger<UnimusApi>.Instance) { RequestGate = unimusGate };
        unimus.Configure(new UnimusConnection(server.Root, "token"));
        var graylogGate = new CountingGate(slots: 1);
        using var graylog = new GraylogApi(NullLogger<GraylogApi>.Instance) { RequestGate = graylogGate };
        graylog.Configure(new GraylogConnection(server.Root, "2.1", null, "user", "password"));

        await unimus.ListAllDevicesAsync().WaitAsync(Patience);
        await graylog.GetStreamsAsync().WaitAsync(Patience);

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal((1, 1), (unimusGate.Entered, unimusGate.Left));
        Assert.Equal((1, 1), (graylogGate.Entered, graylogGate.Left));
    }

    [Fact]
    public async Task Without_a_gate_requests_are_sent_as_before()
    {
        using var server = new SlowServer();
        using var transport = Transport(server, gate: null);

        using var answer = await transport.SendAsync(HttpMethod.Get, "system").WaitAsync(Patience);

        Assert.Null(transport.RequestGate);
        Assert.Single(server.Requests);
    }

    private static LibreNmsTransport Transport(SlowServer server, IRequestGate? gate, int timeoutSeconds = 30)
    {
        var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance) { RequestGate = gate };
        transport.Configure(new LibreNmsConnection(server.Root, "token", timeoutSeconds: timeoutSeconds));
        return transport;
    }

    private sealed class CountingGate(int slots) : IRequestGate
    {
        private readonly SemaphoreSlim _slots = new(slots, slots);
        private int _entered;
        private int _left;

        public int Entered => Volatile.Read(ref _entered);

        public int Left => Volatile.Read(ref _left);

        public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            await _slots.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _entered);
            return new Turn(this);
        }

        private sealed class Turn(CountingGate gate) : IDisposable
        {
            private int _given;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _given, 1) == 0)
                {
                    Interlocked.Increment(ref gate._left);
                    gate._slots.Release();
                }
            }
        }
    }

    /// <summary>A loopback server that answers after <see cref="Delay"/>, counting how many requests it's holding at once.</summary>
    private sealed class SlowServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _sync = new();
        private readonly List<string> _requests = [];
        private int _atOnce;
        private int _mostAtOnce;

        public SlowServer()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public Uri Root => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        public string Body { get; init; } = """{"status":"ok"}""";

        /// <summary>Statuses for the next answers; 200 once they run out.</summary>
        public Queue<HttpStatusCode> Statuses { get; } = new();

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToArray();
                }
            }
        }

        public int MostAtOnce
        {
            get
            {
                lock (_sync)
                {
                    return _mostAtOnce;
                }
            }
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    _ = AnswerAsync(await _listener.AcceptTcpClientAsync(_stop.Token));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Stopped.
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using var connection = client;
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync(_stop.Token);
                if (request is null)
                {
                    return;
                }

                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token)))
                {
                }

                HttpStatusCode status;
                lock (_sync)
                {
                    _requests.Add(request);
                    _mostAtOnce = Math.Max(_mostAtOnce, ++_atOnce);
                    status = Statuses.TryDequeue(out var queued) ? queued : HttpStatusCode.OK;
                }

                await Task.Delay(Delay, _stop.Token);

                // Before the answer goes out, so the client can't have a next request here first.
                lock (_sync)
                {
                    _atOnce--;
                }

                var body = Encoding.UTF8.GetBytes(Body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)status} {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // The test ended, or the client hung up.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
