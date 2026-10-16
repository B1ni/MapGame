using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using MapGame;

var executableRoot = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
var contentRoot = File.Exists(Path.Combine(executableRoot, "wwwroot", "index.html"))
    ? executableRoot
    : Directory.GetCurrentDirectory();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = Path.Combine(contentRoot, "wwwroot")
});
var hosting = new HostingPolicy(builder.Configuration);
// Also disable implicit forwarding enabled by hosting environment variables.
builder.Services.PostConfigure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.None);
builder.WebHost.UseUrls(hosting.ListenUrl);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 4096;
    options.Limits.MaxConcurrentConnections = 256;
    options.Limits.MaxConcurrentUpgradedConnections = 128;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
});
// Public traffic is intentionally limited as one service, not by spoofable proxy headers.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/ws"
            ? RateLimitPartition.GetFixedWindowLimiter("game", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3000, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            })
            : RateLimitPartition.GetNoLimiter("static"));
});
builder.Services.AddSingleton(provider =>
{
    using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream("MapGame.Data.maps.json")
        ?? throw new InvalidDataException("Embedded game data is missing.");
    return GameCatalog.Load(stream);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SinglePlayerGameManager>();
builder.Services.AddSingleton<LanRoomManager>();
builder.Services.AddHostedService<GameMaintenance>();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
var logDirectory = Path.Combine(contentRoot, "Logs");
var fileLogs = builder.Configuration.GetValue("Hosting:FileLogs", !hosting.Internet);
if (fileLogs)
{
    try
    {
        builder.Logging.AddProvider(new DiagnosticFileLogger(logDirectory));
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Logy sa nedajú zapísať do {logDirectory}: {exception.Message}");
        logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapGame", "Logs");
        builder.Logging.AddProvider(new DiagnosticFileLogger(logDirectory));
    }
}

var app = builder.Build();
app.Logger.LogInformation("Startup version={Version} build={BuildId} executable={Executable} contentRoot={ContentRoot} logs={LogDirectory}",
    typeof(GameCatalog).Assembly.GetName().Version, typeof(GameCatalog).Module.ModuleVersionId,
    Environment.ProcessPath, contentRoot, fileLogs ? logDirectory : "stdout");
var catalog = app.Services.GetRequiredService<GameCatalog>();
var games = app.Services.GetRequiredService<SinglePlayerGameManager>();
var rooms = app.Services.GetRequiredService<LanRoomManager>();
var localSubnets = NetworkInterface.GetAllNetworkInterfaces()
    .Where(network => network.OperationalStatus == OperationalStatus.Up)
    .SelectMany(network => network.GetIPProperties().UnicastAddresses)
    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
        && address.IPv4Mask is not null)
    .Select(address => (Address: address.Address, Mask: address.IPv4Mask!))
    .ToArray();

// Render terminates TLS. Use a configured canonical origin, never untrusted
// X-Forwarded-* headers. The health probe alone may use the container's host.
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health" && HttpMethods.IsGet(context.Request.Method))
    {
        await context.Response.WriteAsJsonAsync(new { status = "ok" });
        return;
    }
    if (!hosting.AcceptsHost(context.Request.Host)
        || (HttpMethods.IsPost(context.Request.Method)
            && !hosting.AcceptsOrigin(context.Request.Headers.Origin.ToString(), context.Request, false)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (hosting.Internet)
    {
        context.Request.Scheme = "https";
        context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var requestId = context.TraceIdentifier;
    var path = context.Request.Path.ToString();
    var failed = false;
    var loggedPath = path.StartsWith("/api/games/", StringComparison.Ordinal) ? "/api/games/{id}"
        : path is "/" or "/ws" or "/api/hosting" or "/api/network" or "/api/regions" or "/api/games" or "/api/diagnostics"
            ? path : "/asset-or-other";
    app.Logger.LogInformation("HTTP begin request={RequestId} remote={Remote} local={Local} method={Method} host={Host} path={Path}",
        requestId, context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress,
        context.Request.Method, context.Request.Host, loggedPath);
    try
    {
        await next();
    }
    catch (Exception exception)
    {
        failed = true;
        app.Logger.LogError(exception, "HTTP failed request={RequestId} path={Path}", requestId, loggedPath);
        throw;
    }
    finally
    {
        app.Logger.LogInformation("HTTP end request={RequestId} path={Path} status={Status} elapsedMs={Elapsed}",
            requestId, loggedPath, failed && !context.Response.HasStarted ? 500 : context.Response.StatusCode, watch.ElapsedMilliseconds);
    }
});

app.Use(async (context, next) =>
{
    if (hosting.Internet) { await next(); return; }
    var remoteAddress = context.Connection.RemoteIpAddress;
    var remoteIpv4 = remoteAddress?.AddressFamily == AddressFamily.InterNetworkV6
        && remoteAddress.IsIPv4MappedToIPv6
            ? remoteAddress.MapToIPv4()
            : remoteAddress;

    if (remoteIpv4 is null
        || (remoteIpv4.AddressFamily != AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(remoteIpv4))
        || (!IPAddress.IsLoopback(remoteIpv4)
            && !localSubnets.Any(subnet => IsInSubnet(remoteIpv4, subnet.Address, subnet.Mask))))
    {
        app.Logger.LogWarning("LAN denied request={RequestId} remote={Remote}: address outside local subnets",
            context.TraceIdentifier, remoteAddress);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || context.File.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || context.File.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            || context.File.Name == "map-shapes.json")
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/api/regions", () => catalog.Regions.Select(region => new
{
    region.Id,
    region.Name,
    Count = region.Features.Count
}));

app.MapGet("/api/hosting", () => new { mode = hosting.Internet ? "internet" : "lan" });
app.MapGet("/api/network", () => hosting.Internet
    ? new[] { hosting.PublicOrigin!.GetLeftPart(UriPartial.Authority) }
    : LanNetwork.GetAddresses());

app.MapPost("/api/diagnostics", (ClientDiagnosticRequest request, HttpContext context) =>
{
    if (request.ClientId is null || request.ClientId.Length > 80
        || request.Event is null || request.Event.Length > 80 || request.Detail?.Length > 1500
        || context.Request.ContentLength > 4096)
    {
        app.Logger.LogWarning("Invalid client diagnostic request={RequestId}", context.TraceIdentifier);
        return Results.BadRequest();
    }
    // Client strings are untrusted and may contain names, guesses or answers.
    // Keep full diagnostics only in the user's downloadable browser report.
    app.Logger.LogInformation("Client diagnostic received request={RequestId}", context.TraceIdentifier);
    return Results.NoContent();
});

app.MapPost("/api/games", (StartGameRequest request) =>
{
    try
    {
        return Results.Ok(games.Start(request));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (GameCapacityException)
    {
        return Results.Json(new { error = "Server je plný. Skús to neskôr." }, statusCode: 503);
    }
});

app.MapGet("/api/games/{id}", (string id) =>
{
    var game = games.Get(id);
    return game is null ? Results.NotFound() : Results.Ok(game);
});

app.MapPost("/api/games/{id}/finish", (string id) =>
{
    var game = games.Finish(id);
    return game is null ? Results.NotFound() : Results.Ok(game);
});

app.MapPost("/api/games/{id}/guess", (string id, GuessRequest request) =>
{
    var result = games.Guess(id, request.Guess);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        app.Logger.LogWarning("WebSocket rejected request={RequestId}: missing upgrade", context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var origin = context.Request.Headers.Origin.ToString();
    if (!hosting.AcceptsOrigin(origin, context.Request, true))
    {
        app.Logger.LogWarning("WebSocket rejected request={RequestId}: origin mismatch", context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    app.Logger.LogInformation("WebSocket accepted request={RequestId} remote={Remote}",
        context.TraceIdentifier, context.Connection.RemoteIpAddress);
    await rooms.HandleConnectionAsync(socket, context.RequestAborted, context.TraceIdentifier);
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    if (hosting.Internet)
    {
        Console.WriteLine($"Map Game online: {hosting.PublicOrigin} (internal {hosting.ListenUrl}); logy: stdout / Render Logs");
        return;
    }
    if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected)
    {
        Console.Title = "Map Game - live logy (zatvorením ukončíš server)";
    }
    Console.WriteLine("Map Game beží na http://localhost:5080");
    Console.WriteLine($"Diagnostické logy: {logDirectory}");
    Console.WriteLine("Toto okno zobrazuje živé logy. Nechaj ho otvorené počas hry.");
    foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
    {
        var properties = network.GetIPProperties();
        app.Logger.LogInformation("Network adapter={Adapter} type={Type} status={Status} addresses={Addresses} gateways={Gateways}",
            network.Name, network.NetworkInterfaceType, network.OperationalStatus,
            string.Join(", ", properties.UnicastAddresses.Select(address => $"{address.Address}/{address.IPv4Mask}")),
            string.Join(", ", properties.GatewayAddresses.Select(address => address.Address)));
    }
    foreach (var address in LanNetwork.GetAddresses())
    {
        Console.WriteLine($"Zariadenia v rovnakej sieti: {address}");
        app.Logger.LogInformation("Invite candidate address={Address}", address);
    }

    if (OperatingSystem.IsWindows() && !args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo("http://localhost:5080")
            {
                UseShellExecute = true
            });
            app.Logger.LogInformation("Browser launch requested url={Url}", "http://localhost:5080");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            app.Logger.LogError(exception, "Prehliadač sa nepodarilo otvoriť. Otvor http://localhost:5080 ručne.");
        }
    }
});

app.Run();

static bool IsInSubnet(IPAddress address, IPAddress subnet, IPAddress mask)
{
    var addressBytes = address.GetAddressBytes();
    var subnetBytes = subnet.GetAddressBytes();
    var maskBytes = mask.GetAddressBytes();

    for (var index = 0; index < addressBytes.Length; index++)
    {
        if ((addressBytes[index] & maskBytes[index]) != (subnetBytes[index] & maskBytes[index]))
        {
            return false;
        }
    }

    return true;
}

public partial class Program;
