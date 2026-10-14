using System.Net.WebSockets;
using System.Text.Json;
using MapGame;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MapGame.Tests;

public sealed class LanMultiplayerTests
{
    [Fact]
    public async Task LanRoomManager_ThreeNamedPlayersStayConnectedThroughStartGuessesAndEnd()
    {
        await using var server = await TestServer.StartAsync();
        using var host = await server.ConnectAsync();
        using var browser = await server.ConnectAsync();
        using var downloaded = await server.ConnectAsync();

        await SendAsync(host, new { type = "create", name = "Hostiteľ" });
        var lobby = await ReceiveAsync(host);
        var code = lobby.GetProperty("roomCode").GetString();
        await SendAsync(browser, new { type = "join", roomCode = code, name = "Jana" });
        await ReceiveAsync(host);
        await ReceiveAsync(browser);
        await SendAsync(downloaded, new { type = "join", roomCode = code, name = "Peter" });
        foreach (var socket in new[] { host, browser, downloaded })
        {
            lobby = await ReceiveAsync(socket);
            Assert.Equal("lobby-update", lobby.GetProperty("type").GetString());
            Assert.Equal(new[] { "Hostiteľ", "Jana", "Peter" },
                lobby.GetProperty("players").EnumerateArray().Select(player => player.GetProperty("name").GetString()));
        }

        await SendAsync(host, new { type = "start", regionId = "europe", timeLimitSeconds = 60 });
        foreach (var socket in new[] { host, browser, downloaded })
        {
            var start = await ReceiveAsync(socket);
            Assert.Equal("game-started", start.GetProperty("type").GetString());
            Assert.Equal("europe", start.GetProperty("regionId").GetString());
        }
        await SendAsync(browser, new { type = "guess", guess = "Slovensko" });
        Assert.Equal("correct", (await ReceiveAsync(browser)).GetProperty("outcome").GetString());
        await SendAsync(downloaded, new { type = "sync" });
        var tick = await ReceiveAsync(downloaded);
        Assert.Equal("tick", tick.GetProperty("type").GetString());
        Assert.Empty(tick.GetProperty("foundIds").EnumerateArray());
        await SendAsync(host, new { type = "end" });
        foreach (var socket in new[] { host, browser, downloaded })
        {
            var finish = await ReceiveAsync(socket);
            Assert.Equal("finished", finish.GetProperty("type").GetString());
            Assert.Equal("Jana", finish.GetProperty("results")[0].GetProperty("name").GetString());
            Assert.Equal(3, finish.GetProperty("results").GetArrayLength());
        }
    }

    [Fact]
    public async Task LanRoomManager_RejectsMissingNameUnknownRoomDuplicateNameAndMissingDuration()
    {
        await using var server = await TestServer.StartAsync();
        using var host = await server.ConnectAsync();
        using var guest = await server.ConnectAsync();
        await SendAsync(host, new { type = "create", name = "" });
        Assert.Equal("error", (await ReceiveAsync(host)).GetProperty("type").GetString());
        await SendAsync(host, new { type = "create", name = "Jana" });
        var code = (await ReceiveAsync(host)).GetProperty("roomCode").GetString();
        await SendAsync(guest, new { type = "join", roomCode = "XXXXXX", name = "Peter" });
        Assert.Equal("error", (await ReceiveAsync(guest)).GetProperty("type").GetString());
        await SendAsync(guest, new { type = "join", roomCode = code, name = "jana" });
        Assert.Contains("meno", (await ReceiveAsync(guest)).GetProperty("message").GetString());
        await SendAsync(host, new { type = "start", regionId = "europe" });
        Assert.Equal("error", (await ReceiveAsync(host)).GetProperty("type").GetString());
        await SendAsync(guest, new { type = "join", roomCode = code, name = "Peter" });
        Assert.Equal("lobby-update", (await ReceiveAsync(guest)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task LanRoomManager_HostDisconnectDuringGameNotifiesGuests()
    {
        await using var server = await TestServer.StartAsync();
        using var host = await server.ConnectAsync();
        using var guest = await server.ConnectAsync();
        await SendAsync(host, new { type = "create", name = "Host" });
        var code = (await ReceiveAsync(host)).GetProperty("roomCode").GetString();
        await SendAsync(guest, new { type = "join", roomCode = code, name = "Guest" });
        await ReceiveAsync(host);
        await ReceiveAsync(guest);
        await SendAsync(host, new { type = "start", regionId = "europe", timeLimitSeconds = 60 });
        await ReceiveAsync(host);
        await ReceiveAsync(guest);
        host.Abort();
        Assert.Equal("room-closed", (await ReceiveAsync(guest)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task RoomsExpireAndCleanupFinishesTimedMatchesWithoutClientPolling()
    {
        var clock = new ManualClock();
        await using var server = await TestServer.StartAsync(clock);
        using var host = await server.ConnectAsync();
        await SendAsync(host, new { type = "create", name = "Host" });
        await ReceiveAsync(host);
        await SendAsync(host, new { type = "start", regionId = "europe", timeLimitSeconds = 60 });
        await ReceiveAsync(host);
        clock.Advance(TimeSpan.FromSeconds(61));
        await server.Rooms.CleanupAsync();
        var finished = await ReceiveAsync(host);
        Assert.Equal("finished", finished.GetProperty("type").GetString());
        Assert.Equal(0, finished.GetProperty("results")[0].GetProperty("guessedCount").GetInt32());
        clock.Advance(TimeSpan.FromMinutes(16));
        await server.Rooms.CleanupAsync();
        Assert.Equal("room-closed", (await ReceiveAsync(host)).GetProperty("type").GetString());

        using var lobby = await server.ConnectAsync();
        await SendAsync(lobby, new { type = "create", name = "New host" });
        await ReceiveAsync(lobby);
        clock.Advance(TimeSpan.FromMinutes(31));
        await server.Rooms.CleanupAsync();
        Assert.Equal("room-closed", (await ReceiveAsync(lobby)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task RoomsAndPlayersHaveHardCapacityLimits()
    {
        await using var server = await TestServer.StartAsync();
        var sockets = new List<ClientWebSocket>();
        string? code = null;
        try
        {
            for (var index = 0; index < LanRoomManager.MaximumRooms; index++)
            {
                var socket = await server.ConnectAsync();
                sockets.Add(socket);
                await SendAsync(socket, new { type = "create", name = $"Host {index}" });
                var lobby = await ReceiveAsync(socket);
                Assert.Equal("lobby-update", lobby.GetProperty("type").GetString());
                if (index == 0) code = lobby.GetProperty("roomCode").GetString();
            }
            using var excess = await server.ConnectAsync();
            await SendAsync(excess, new { type = "create", name = "Extra host" });
            Assert.Equal("error", (await ReceiveAsync(excess)).GetProperty("type").GetString());
            for (var index = 1; index < LanRoomManager.MaximumPlayers; index++)
            {
                var guest = await server.ConnectAsync();
                sockets.Add(guest);
                await SendAsync(guest, new { type = "join", roomCode = code, name = $"Guest {index}" });
                Assert.Equal("lobby-update", (await ReceiveAsync(guest)).GetProperty("type").GetString());
                await ReceiveAsync(sockets[0]);
            }
            await SendAsync(excess, new { type = "join", roomCode = code, name = "Extra guest" });
            Assert.Equal("error", (await ReceiveAsync(excess)).GetProperty("type").GetString());
        }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }

    [Fact]
    public async Task OversizedWebSocketMessageIsRejected()
    {
        await using var server = await TestServer.StartAsync();
        using var socket = await server.ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(new byte[5000], WebSocketMessageType.Text, true, timeout.Token);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), timeout.Token));
    }

    [Fact]
    public async Task WebSocketMessageFloodIsDisconnected()
    {
        await using var server = await TestServer.StartAsync();
        using var socket = await server.ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var index = 0; index < 60; index++)
        {
            await SendAsync(socket, new { type = "invalid" });
            Assert.Equal("error", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        }
        await SendAsync(socket, new { type = "invalid" });
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), timeout.Token));
    }

    private static async Task SendAsync(ClientWebSocket socket, object message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message),
            WebSocketMessageType.Text, true, timeout.Token);
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private sealed class TestServer(WebApplication app, LanRoomManager rooms) : IAsyncDisposable
    {
        public LanRoomManager Rooms { get; } = rooms;
        public static async Task<TestServer> StartAsync(TimeProvider? timeProvider = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream("MapGame.Data.maps.json")!;
            var rooms = new LanRoomManager(GameCatalog.Load(stream), timeProvider ?? TimeProvider.System,
                NullLogger<LanRoomManager>.Instance);
            app.UseWebSockets();
            app.Map("/ws", async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await rooms.HandleConnectionAsync(socket, context.RequestAborted);
            });
            await app.StartAsync();
            return new TestServer(app, rooms);
        }

        public async Task<ClientWebSocket> ConnectAsync()
        {
            var socket = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri(app.Urls.Single().Replace("http:", "ws:") + "/ws"), timeout.Token);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
