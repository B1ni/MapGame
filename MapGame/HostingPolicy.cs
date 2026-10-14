namespace MapGame;

public sealed class HostingPolicy
{
    public bool Internet { get; }
    public Uri? PublicOrigin { get; }
    public string ListenUrl { get; }

    public HostingPolicy(IConfiguration configuration)
    {
        var mode = configuration["Hosting:Mode"] ?? "Lan";
        if (!string.Equals(mode, "Lan", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "Internet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hosting:Mode must be Lan or Internet.");
        Internet = string.Equals(mode, "Internet", StringComparison.OrdinalIgnoreCase);
        if (Internet)
        {
            var origin = configuration["Hosting:PublicOrigin"] ?? configuration["RENDER_EXTERNAL_URL"];
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || uri.UserInfo.Length != 0
                || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidOperationException("Internet mode requires Hosting:PublicOrigin (or RENDER_EXTERNAL_URL), an HTTPS origin without a path.");
            PublicOrigin = uri;
        }
        var portText = configuration["PORT"] ?? "5080";
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("PORT must be between 1 and 65535.");
        ListenUrl = $"http://0.0.0.0:{port}";
    }

    public bool AcceptsHost(HostString host) => !Internet
        || string.Equals(host.Value, PublicOrigin!.Authority, StringComparison.OrdinalIgnoreCase);

    public bool AcceptsOrigin(string origin, HttpRequest request, bool webSocket)
    {
        if (string.IsNullOrEmpty(origin)) return !Internet || !webSocket;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var expected = Internet ? PublicOrigin! : new Uri($"{request.Scheme}://{request.Host}");
        if (uri.Scheme == expected.Scheme
            && string.Equals(uri.Authority, expected.Authority, StringComparison.OrdinalIgnoreCase)) return true;
        // Downloaded Windows clients may connect to another LAN server.
        return !Internet && uri.Scheme == "http" && uri.Port == 5080
            && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || System.Net.IPAddress.TryParse(uri.Host, out var ip) && System.Net.IPAddress.IsLoopback(ip));
    }
}
