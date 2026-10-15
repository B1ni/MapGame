using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Diagnostics;
using System.Text.Json;
using MapGame;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace MapGame.Tests;

public sealed class HostingTests
{
    [Fact]
    public void InternetConfigurationFailsClosedAndUsesRenderOriginAndPort()
    {
        Assert.Throws<InvalidOperationException>(() => Policy("Internet", null));
        foreach (var origin in new[] { "http://game.onrender.com", "https://game.onrender.com/path", "https://user@game.onrender.com", "https://game.onrender.com/?x=1" })
            Assert.Throws<InvalidOperationException>(() => Policy("Internet", origin));
        var policy = Policy("Internet", "https://game.onrender.com");
        Assert.Equal("http://0.0.0.0:10000", policy.ListenUrl);
        Assert.True(policy.AcceptsHost(new HostString("game.onrender.com")));
        Assert.False(policy.AcceptsHost(new HostString("evil.example")));
        var request = new DefaultHttpContext().Request;
        Assert.True(policy.AcceptsOrigin("https://game.onrender.com", request, true));
        foreach (var origin in new[] { "", "null", "invalid", "http://game.onrender.com", "https://game.onrender.com:5080", "https://evil.example", "http://localhost:5080" })
            Assert.False(policy.AcceptsOrigin(origin, request, true));
    }

    [Fact]
    public void LanKeepsSameOriginAndDownloadedLoopbackClientsButNotWrongPorts()
    {
        var policy = Policy("Lan", null);
        var request = new DefaultHttpContext().Request;
        request.Scheme = "http";
        request.Host = new HostString("192.168.1.2:5080");
        Assert.True(policy.AcceptsOrigin("http://192.168.1.2:5080", request, true));
        Assert.True(policy.AcceptsOrigin("http://localhost:5080", request, true));
        Assert.False(policy.AcceptsOrigin("http://192.168.1.2:9000", request, true));
        Assert.False(policy.AcceptsOrigin("garbage", request, true));
    }

    [Fact]
    public async Task ActualAppServesPublicConfigAssetsAndWebSocketsWithoutTrustingForwardedHeaders()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo("dotnet", $"\"{typeof(Program).Assembly.Location}\" --no-browser")
        {
            WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MapGame")),
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["PORT"] = port.ToString();
        start.Environment["Hosting__Mode"] = "Internet";
        start.Environment["Hosting__PublicOrigin"] = "https://game.onrender.com";
        start.Environment["Hosting__FileLogs"] = "false";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true";
        using var process = Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };
            var ready = false;
            for (var attempt = 0; attempt < 100 && !process.HasExited; attempt++)
            {
                try { ready = (await client.GetAsync("/health")).IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                if (ready) break;
                await Task.Delay(100);
            }
            Assert.True(ready, "App did not start.");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/hosting")).StatusCode);
            client.DefaultRequestHeaders.Host = "game.onrender.com";
            client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.example");
            client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
            Assert.Contains("internet", await client.GetStringAsync("/api/hosting"));
            Assert.Equal("[\"https://game.onrender.com\"]", await client.GetStringAsync("/api/network"));
            var page = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.True(page.Headers.Contains("Strict-Transport-Security"));
            Assert.Contains("lanJoinOptions", await page.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/data/map-shapes.json")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/data/country-names.json")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Data/country-names.json")).StatusCode);
            using var hostile = new HttpRequestMessage(HttpMethod.Post, "/api/games")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
            hostile.Headers.Add("Origin", "https://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(hostile)).StatusCode);
            using var huge = new StringContent(new string('x', 5000), System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/games", huge)).StatusCode);
            using var diagnostic = new StringContent("""{"clientId":"PRIVATE_NAME","event":"PRIVATE_GUESS","detail":"PRIVATE_ANSWER"}""", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/diagnostics", diagnostic)).StatusCode);
            foreach (var origin in new[] { "", "https://evil.example", "http://localhost:5080", "http://game.onrender.com" })
            {
                using var rejected = new ClientWebSocket();
                rejected.Options.SetRequestHeader("Host", "game.onrender.com");
                if (origin.Length > 0) rejected.Options.SetRequestHeader("Origin", origin);
                await Assert.ThrowsAsync<WebSocketException>(() => rejected.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None));
            }
            using var accepted = new ClientWebSocket();
            accepted.Options.SetRequestHeader("Host", "game.onrender.com");
            accepted.Options.SetRequestHeader("Origin", "https://game.onrender.com");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await accepted.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), timeout.Token);
            await accepted.SendAsync("""{"type":"create","name":"PRIVATE_NAME"}"""u8.ToArray(), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[4096];
            var reply = await accepted.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, reply.Count));
            Assert.Equal("lobby-update", json.RootElement.GetProperty("type").GetString());
            accepted.Abort();
            var limited = false;
            for (var attempt = 0; attempt < 3001; attempt++)
            {
                using var response = await client.GetAsync("/api/hosting");
                if (response.StatusCode == HttpStatusCode.TooManyRequests) { limited = true; break; }
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            Assert.True(limited, "API limiter did not return 429.");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        var logs = await outputTask + await errorTask;
        Assert.DoesNotContain("PRIVATE_NAME", logs);
        Assert.DoesNotContain("PRIVATE_GUESS", logs);
        Assert.DoesNotContain("PRIVATE_ANSWER", logs);
    }

    private static HostingPolicy Policy(string mode, string? origin) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hosting:Mode"] = mode, ["RENDER_EXTERNAL_URL"] = origin, ["PORT"] = "10000"
        }).Build());
}
