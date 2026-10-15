using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MapGame;

public sealed class LanRoomManager(
    GameCatalog catalog,
    TimeProvider timeProvider,
    ILogger<LanRoomManager> logger)
{
    private const int MaximumMessageBytes = 4096;
    private const int MinimumDurationSeconds = 60;
    private const int MaximumDurationSeconds = 7200;
    private const string RoomAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, LanRoom> _rooms = new(StringComparer.Ordinal);
    private readonly object _roomsLock = new();
    private readonly SemaphoreSlim _connections = new(128, 128);
    public const int MaximumRooms = 32;
    public const int MaximumPlayers = 16;
    private static readonly string[] PlayerColors =
    [
        "#2563eb", "#dc2626", "#16a34a", "#9333ea",
        "#ea580c", "#0891b2", "#db2777", "#854d0e",
        "#4f46e5", "#0f766e", "#be123c", "#65a30d",
        "#a21caf", "#b45309", "#475569", "#7c3aed"
    ];

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var room in _rooms.Values)
        {
            IReadOnlyList<LanPeer> peers;
            IReadOnlyList<LeaderboardEntry>? results = null;
            var expired = false;
            lock (room.SyncRoot)
            {
                peers = ConnectedPeers(room);
                if (room.Status == "playing" && room.EndsAt <= now)
                {
                    results = FinishRoom(room);
                    room.FinishedAt = now;
                }
                expired = (room.Status == "lobby" && now - room.CreatedAt >= TimeSpan.FromMinutes(30))
                    || (room.Status == "finished" && now - room.FinishedAt >= TimeSpan.FromMinutes(15))
                    || now - room.CreatedAt >= TimeSpan.FromHours(4)
                    || peers.Count == 0;
                if (expired)
                {
                    room.Status = "closed";
                    _rooms.TryRemove(room.Code, out _);
                }
            }
            if (expired)
            {
                await Task.WhenAll(peers.Select(peer => SendAsync(peer,
                    new { type = "room-closed", message = "Platnosť čakárne alebo výsledkov vypršala." }, cancellationToken)));
                await Task.WhenAll(peers.Select(peer => peer.CloseOutputAsync()));
            }
            else if (results is not null) await SendFinishedAsync(room, peers, results, cancellationToken);
        }
    }

    public async Task HandleConnectionAsync(WebSocket socket, CancellationToken cancellationToken, string? connectionId = null)
    {
        if (!await _connections.WaitAsync(0, cancellationToken)) { socket.Abort(); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromHours(4));
        cancellationToken = lifetime.Token;
        var peer = new LanPeer(socket, connectionId ?? Guid.NewGuid().ToString("N"));
        var windowStart = timeProvider.GetUtcNow();
        var messageCount = 0;
        logger.LogInformation("LAN connected connection={ConnectionId}", peer.ConnectionId);
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                var message = await ReceiveMessageAsync(socket, receiveTimeout.Token);
                if (message is null)
                {
                    break;
                }

                var now = timeProvider.GetUtcNow();
                if (now - windowStart >= TimeSpan.FromSeconds(10)) { windowStart = now; messageCount = 0; }
                if (++messageCount > 60) { socket.Abort(); break; }
                await ProcessMessageAsync(peer, message, cancellationToken);
            }
        }
        catch (WebSocketException exception)
        {
            logger.LogWarning(exception, "LAN socket failed connection={ConnectionId} room={Room} player={PlayerId}",
                peer.ConnectionId, peer.RoomCode, peer.PlayerId);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("LAN cancelled connection={ConnectionId}", peer.ConnectionId);
        }
        finally
        {
            logger.LogInformation("LAN disconnected connection={ConnectionId} room={Room} player={PlayerId} state={State} closeStatus={CloseStatus}",
                peer.ConnectionId, peer.RoomCode, peer.PlayerId, socket.State, socket.CloseStatus);
            if (socket.State == WebSocketState.CloseReceived) await peer.CloseOutputAsync();
            if (socket.State != WebSocketState.Closed) socket.Abort();
            try { await DisconnectAsync(peer, CancellationToken.None); }
            finally { _connections.Release(); }
        }
    }

    private async Task ProcessMessageAsync(
        LanPeer peer,
        LanClientMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Type is "create" or "join" or "start" or "end" or "leave")
        {
            logger.LogInformation("LAN command connection={ConnectionId} type={Type} room={Room}",
                peer.ConnectionId, message.Type, peer.RoomCode);
        }
        switch (message.Type)
        {
            case "create":
                await CreateRoomAsync(peer, message.Name, cancellationToken);
                break;
            case "join":
                await JoinRoomAsync(peer, message.RoomCode, message.Name, cancellationToken);
                break;
            case "start":
                await StartGameAsync(peer, message.RegionId, message.TimeLimitSeconds, message.GameMode, cancellationToken);
                break;
            case "guess":
                await SubmitGuessAsync(peer, message.Guess, cancellationToken);
                break;
            case "end":
                await EndRoomAsync(peer, cancellationToken);
                break;
            case "leave":
                LeaveRoom(peer);
                break;
            case "sync":
                await SyncAsync(peer, cancellationToken);
                break;
            default:
                await SendErrorAsync(peer, "Neznáma požiadavka.", cancellationToken);
                break;
        }
    }

    private async Task CreateRoomAsync(LanPeer peer, string? name, CancellationToken cancellationToken)
    {
        if (peer.RoomCode is not null)
        {
            await SendErrorAsync(peer, "Už si pripojený k hre.", cancellationToken);
            return;
        }

        if (!TryNormalizeName(name, out var playerName))
        {
            await SendErrorAsync(peer, "Meno musí mať 1 až 24 znakov.", cancellationToken);
            return;
        }

        LanRoom? room = null;
        lock (_roomsLock)
        {
            if (_rooms.Count < MaximumRooms)
            {
                do
                {
                    room = new LanRoom(CreateRoomCode(), timeProvider.GetUtcNow());
                    lock (room.SyncRoot)
                    {
                        if (_rooms.TryAdd(room.Code, room))
                        {
                            AddPlayer(room, peer, playerName, isHost: true);
                            break;
                        }
                    }
                } while (true);
            }
        }
        if (room is null)
        {
            await SendErrorAsync(peer, "Server má plné čakárne. Skús to neskôr.", cancellationToken);
            return;
        }

        await SendLobbyUpdateAsync(room, cancellationToken);
        logger.LogInformation("LAN room created connection={ConnectionId} room={Room} player={PlayerId}",
            peer.ConnectionId, room.Code, peer.PlayerId);
    }

    private async Task JoinRoomAsync(
        LanPeer peer,
        string? roomCode,
        string? name,
        CancellationToken cancellationToken)
    {
        if (peer.RoomCode is not null)
        {
            await SendErrorAsync(peer, "Už si pripojený k hre.", cancellationToken);
            return;
        }

        if (!TryNormalizeName(name, out var playerName))
        {
            await SendErrorAsync(peer, "Meno musí mať 1 až 24 znakov.", cancellationToken);
            return;
        }

        var code = roomCode?.Trim().ToUpperInvariant() ?? "";
        if (!_rooms.TryGetValue(code, out var room))
        {
            await SendErrorAsync(peer, "Hra s týmto ID neexistuje.", cancellationToken);
            return;
        }

        var joined = false;
        var duplicateName = false;
        lock (room.SyncRoot)
        {
            duplicateName = room.Players.Values.Any(player =>
                string.Equals(player.Name, playerName, StringComparison.OrdinalIgnoreCase));
            if (room.Status == "lobby" && !duplicateName && room.Players.Count < MaximumPlayers)
            {
                AddPlayer(room, peer, playerName, isHost: false);
                joined = true;
            }
        }

        if (!joined)
        {
            await SendErrorAsync(peer, duplicateName
                ? "Toto meno už používa iný hráč. Zvoľ si iné meno."
                : "Táto hra sa už začala alebo je plná (najviac 16 hráčov).", cancellationToken);
            return;
        }

        await SendLobbyUpdateAsync(room, cancellationToken);
        logger.LogInformation("LAN room joined connection={ConnectionId} room={Room} player={PlayerId}",
            peer.ConnectionId, room.Code, peer.PlayerId);
    }

    private async Task StartGameAsync(
        LanPeer peer,
        string? regionId,
        int? timeLimitSeconds,
        string? gameMode,
        CancellationToken cancellationToken)
    {
        if (!TryGetRoom(peer, out var room))
        {
            await SendErrorAsync(peer, "Najprv sa pripoj k hre.", cancellationToken);
            return;
        }

        gameMode ??= "classic";
        if (gameMode is not ("classic" or "battle-normal" or "battle-hardcore"))
        {
            await SendErrorAsync(peer, "Zvolený herný režim neexistuje.", cancellationToken);
            return;
        }

        IReadOnlyList<LanPeer> peers;
        GameRegion selectedRegion;
        try
        {
            selectedRegion = string.Equals(regionId, "random", StringComparison.OrdinalIgnoreCase)
                ? catalog.GetRandomRegion()
                : catalog.GetRegion(regionId ?? "");
        }
        catch (ArgumentException)
        {
            await SendErrorAsync(peer, "Zvolený región neexistuje.", cancellationToken);
            return;
        }

        GameRegion? region;
        DateTimeOffset endsAt;
        lock (room.SyncRoot)
        {
            var player = FindPlayer(room, peer);
            if (player is null || !player.IsHost)
            {
                peers = [];
                region = null;
                endsAt = default;
            }
            else if (room.Status != "lobby")
            {
                peers = [];
                region = null;
                endsAt = default;
            }
            else if (timeLimitSeconds is null or < MinimumDurationSeconds or > MaximumDurationSeconds)
            {
                peers = [];
                region = null;
                endsAt = default;
            }
            else
            {
                region = selectedRegion;
                room.Region = region;
                room.GameMode = gameMode;
                room.StartedAt = timeProvider.GetUtcNow();
                room.EndsAt = room.StartedAt.Value.AddSeconds(timeLimitSeconds.GetValueOrDefault());
                room.Status = "playing";
                foreach (var roomPlayer in room.Players.Values)
                {
                    roomPlayer.FoundIds.Clear();
                    roomPlayer.SecondsAtBestScore = 0;
                    roomPlayer.IsActive = true;
                }

                peers = ConnectedPeers(room);
                endsAt = room.EndsAt.Value;
            }
        }

        if (region is null)
        {
            await SendErrorAsync(peer, "Hru môže spustiť iba hostiteľ a čas musí byť 1 až 120 minút.", cancellationToken);
            return;
        }

        IReadOnlyList<LeaderboardEntry> leaderboard;
        lock (room.SyncRoot) leaderboard = BuildLeaderboard(room);
        await Task.WhenAll(peers.Select(target => SendAsync(
            target,
            new
            {
                type = "game-started",
                regionId = region.Id,
                regionName = region.Name,
                gameMode,
                ownership = Array.Empty<OwnedCountry>(),
                revision = room.Revision,
                leaderboard,
                endsAt,
                remainingSeconds = timeLimitSeconds
            },
            cancellationToken)));
        logger.LogInformation("LAN game started room={Room} region={Region} players={Count} endsAt={EndsAt}",
            room.Code, region.Id, peers.Count, endsAt);
    }

    private async Task SubmitGuessAsync(LanPeer peer, string? guess, CancellationToken cancellationToken)
    {
        if (!TryGetRoom(peer, out var room))
        {
            await SendErrorAsync(peer, "Nie si pripojený k hre.", cancellationToken);
            return;
        }

        object? response = null;
        var ownershipChanged = false;
        IReadOnlyList<LanPeer> peersToFinish = [];
        IReadOnlyList<LeaderboardEntry>? results = null;
        lock (room.SyncRoot)
        {
            var player = FindPlayer(room, peer);
            if (room.Status == "playing" && room.EndsAt!.Value <= timeProvider.GetUtcNow())
            {
                results = FinishRoom(room);
                peersToFinish = ConnectedPeers(room);
            }
            else if (room.Status != "playing" || room.Region is null || player is null || !player.IsActive)
            {
                response = new { type = "error", message = "Hra práve nebeží." };
            }
            else if (guess is null || guess.Length > 100)
            {
                response = new { type = "guess-result", outcome = "incorrect", message = "Tip je príliš dlhý." };
            }
            else
            {
                var feature = catalog.FindAnswer(room.Region.Id, guess);
                if (feature is null)
                {
                    response = new { type = "guess-result", outcome = "incorrect", message = "Tento štát v zvolenom regióne nie je." };
                }
                else if (room.IsBattle && room.Owners.TryGetValue(feature.Id, out var ownerId)
                    && ownerId != player.Id && room.GameMode == "battle-normal")
                {
                    response = new { type = "guess-result", outcome = "already-claimed", message = "Tento štát už obsadil iný hráč." };
                }
                else if (!player.FoundIds.Add(feature.Id))
                {
                    response = new { type = "guess-result", outcome = "already-found", message = "Tento štát už máš uhádnutý." };
                }
                else
                {
                    if (room.IsBattle)
                    {
                        if (room.Owners.TryGetValue(feature.Id, out var previousOwner))
                            room.Players[previousOwner].FoundIds.Remove(feature.Id);
                        room.Owners[feature.Id] = player.Id;
                        room.Revision++;
                        ownershipChanged = true;
                    }
                    player.SecondsAtBestScore = (int)Math.Floor((timeProvider.GetUtcNow() - room.StartedAt!.Value).TotalSeconds);
                    response = new
                    {
                        type = "guess-result",
                        outcome = "correct",
                        message = $"Správne: {feature.Name}.",
                        foundIds = player.FoundIds.ToArray(),
                        leaderboard = BuildLeaderboard(room),
                        ownership = BuildOwnership(room),
                        revision = room.Revision
                    };
                    if (room.IsBattle && room.Owners.Count == room.Region.Features.Count)
                    {
                        results = FinishRoom(room);
                        peersToFinish = ConnectedPeers(room);
                    }
                }
            }
        }

        if (results is not null)
        {
            await SendFinishedAsync(room, peersToFinish, results, cancellationToken);
        }
        else if (response is not null)
        {
            await SendAsync(peer, response, cancellationToken);
            if (ownershipChanged) await SendBattleUpdateAsync(room, peer, cancellationToken);
        }
    }

    private async Task SyncAsync(LanPeer peer, CancellationToken cancellationToken)
    {
        if (!TryGetRoom(peer, out var room))
        {
            await SendErrorAsync(peer, "Nie si pripojený k hre.", cancellationToken);
            return;
        }

        object? response = null;
        IReadOnlyList<LanPeer> peersToFinish = [];
        IReadOnlyList<LeaderboardEntry>? results = null;
        lock (room.SyncRoot)
        {
            var player = FindPlayer(room, peer);
            if (room.Status == "playing" && room.EndsAt!.Value <= timeProvider.GetUtcNow())
            {
                results = FinishRoom(room);
                peersToFinish = ConnectedPeers(room);
            }
            else if (room.Status == "playing" && player is not null)
            {
                response = BuildTick(room, player);
            }
            else if (room.Status == "finished" && room.Results is not null)
            {
                results = room.Results;
                peersToFinish = [peer];
            }
        }

        if (results is not null)
        {
            await SendFinishedAsync(room, peersToFinish, results, cancellationToken);
        }
        else if (response is not null)
        {
            await SendAsync(peer, response, cancellationToken);
        }
    }

    private async Task EndRoomAsync(LanPeer peer, CancellationToken cancellationToken)
    {
        if (!TryGetRoom(peer, out var room))
        {
            await SendErrorAsync(peer, "Nie si pripojený k hre.", cancellationToken);
            return;
        }

        IReadOnlyList<LeaderboardEntry>? results;
        IReadOnlyList<LanPeer> peers;
        lock (room.SyncRoot)
        {
            var player = FindPlayer(room, peer);
            if (player is null || room.Status != "playing")
            {
                results = null;
                peers = [];
            }
            else if (!player.IsHost)
            {
                results = null;
                peers = [];
            }
            else
            {
                results = FinishRoom(room);
                peers = ConnectedPeers(room);
            }
        }

        if (results is null)
        {
            await SendErrorAsync(peer, "Spoločnú hru môže ukončiť iba hostiteľ.", cancellationToken);
            return;
        }

        await SendFinishedAsync(room, peers, results, cancellationToken, "host-ended");
    }

    private void LeaveRoom(LanPeer peer)
    {
        if (!TryGetRoom(peer, out var room))
        {
            return;
        }

        lock (room.SyncRoot)
        {
            var player = FindPlayer(room, peer);
            if (player is not null && !player.IsHost)
            {
                player.IsActive = false;
            }
        }
    }

    private async Task SendLobbyUpdateAsync(LanRoom room, CancellationToken cancellationToken)
    {
        IReadOnlyList<LanPeer> peers;
        object[] players;
        string status;
        string code;
        lock (room.SyncRoot)
        {
            peers = ConnectedPeers(room);
            players = room.Players.Values
                .Where(player => player.Peer is not null)
                .Select(player => (object)new { name = player.Name, isHost = player.IsHost })
                .ToArray();
            status = room.Status;
            code = room.Code;
        }

        await Task.WhenAll(peers.Select(target => SendAsync(
            target,
            new
            {
                type = "lobby-update",
                roomCode = code,
                status,
                players,
                isHost = target.IsHost
            },
            cancellationToken)));
    }

    private async Task DisconnectAsync(LanPeer peer, CancellationToken cancellationToken)
    {
        if (!TryGetRoom(peer, out var room))
        {
            return;
        }

        var deleteRoom = false;
        var notifyRoom = false;
        IReadOnlyList<LanPeer> closedRoomPeers = [];
        lock (room.SyncRoot)
        {
            var player = FindPlayer(room, peer);
            if (player is null)
            {
                return;
            }

            player.Peer = null;
            peer.RoomCode = null;
            peer.PlayerId = null;

            if (room.Status != "finished" && player.IsHost)
            {
                closedRoomPeers = ConnectedPeers(room);
                deleteRoom = true;
                notifyRoom = true;
            }
            else
            {
                if (room.Status == "lobby")
                {
                    room.Players.Remove(player.Id);
                }

                deleteRoom = !room.Players.Values.Any(candidate => candidate.Peer is not null);
                notifyRoom = !deleteRoom;
            }
        }

        if (deleteRoom)
        {
            _rooms.TryRemove(room.Code, out _);
            if (closedRoomPeers.Count > 0)
            {
                await Task.WhenAll(closedRoomPeers.Select(target => SendAsync(
                    target,
                    new { type = "room-closed", message = "Hostiteľ sa odpojil. Hra bola zatvorená." },
                    cancellationToken)));
            }
        }
        else if (notifyRoom)
        {
            await SendLobbyUpdateAsync(room, cancellationToken);
        }
    }

    private async Task SendFinishedAsync(
        LanRoom room,
        IReadOnlyList<LanPeer> peers,
        IReadOnlyList<LeaderboardEntry> results,
        CancellationToken cancellationToken,
        string? reason = null)
    {
        OwnedCountry[] ownership;
        lock (room.SyncRoot) ownership = BuildOwnership(room);
        await Task.WhenAll(peers.Select(target => SendAsync(
            target,
            new { type = "finished", results, reason, gameMode = room.GameMode, ownership, revision = room.Revision },
            cancellationToken)));
    }

    private object BuildTick(LanRoom room, LanPlayer player) => new
    {
        type = "tick",
        remainingSeconds = Math.Max(0, (int)Math.Ceiling((room.EndsAt!.Value - timeProvider.GetUtcNow()).TotalSeconds)),
        foundIds = player.FoundIds.ToArray(),
        leaderboard = BuildLeaderboard(room),
        ownership = BuildOwnership(room),
        revision = room.Revision
    };

    private static OwnedCountry[] BuildOwnership(LanRoom room) => room.Owners
        .Select(entry => new OwnedCountry(entry.Key, room.Players[entry.Value].Color))
        .ToArray();

    private async Task SendBattleUpdateAsync(LanRoom room, LanPeer sender, CancellationToken cancellationToken)
    {
        (LanPeer Peer, object Message)[] updates;
        lock (room.SyncRoot)
        {
            if (room.Status != "playing") return;
            updates = room.Players.Values
                .Where(player => player.Peer is not null && player.Peer != sender)
                .Select(player => (player.Peer!, BuildTick(room, player))).ToArray();
        }
        await Task.WhenAll(updates.Select(update => SendAsync(update.Peer, update.Message, cancellationToken)));
    }

    private IReadOnlyList<LeaderboardEntry> FinishRoom(LanRoom room)
    {
        if (room.Results is not null)
        {
            return room.Results;
        }

        room.Status = "finished";
        room.FinishedAt = timeProvider.GetUtcNow();
        room.Results = BuildLeaderboard(room);
        return room.Results;
    }

    private static IReadOnlyList<LeaderboardEntry> BuildLeaderboard(LanRoom room)
    {
        var totalCount = room.Region!.Features.Count;
        var players = room.Players.Values
            .Where(player => room.IsBattle || player.IsActive)
            .OrderByDescending(player => player.FoundIds.Count)
            .ThenBy(player => room.IsBattle || player.FoundIds.Count == 0 ? int.MaxValue : player.SecondsAtBestScore)
            .ThenBy(player => player.Name, StringComparer.CurrentCulture)
            .ToArray();
        return players
            .Select((player, index) => new LeaderboardEntry(
                room.IsBattle ? Array.FindIndex(players, other => other.FoundIds.Count == player.FoundIds.Count) + 1 : index + 1,
                player.Name,
                player.FoundIds.Count,
                totalCount,
                room.IsBattle || player.FoundIds.Count == 0 ? null : player.SecondsAtBestScore,
                room.IsBattle ? player.Color : null))
            .ToArray();
    }

    private static void AddPlayer(LanRoom room, LanPeer peer, string name, bool isHost)
    {
        var color = PlayerColors.First(color => room.Players.Values.All(player => player.Color != color));
        var player = new LanPlayer(Guid.NewGuid().ToString("N"), name, isHost, peer, color);
        room.Players.Add(player.Id, player);
        peer.RoomCode = room.Code;
        peer.PlayerId = player.Id;
        peer.IsHost = isHost;
    }

    private static LanPlayer? FindPlayer(LanRoom room, LanPeer peer)
    {
        return peer.PlayerId is not null
            ? room.Players.GetValueOrDefault(peer.PlayerId)
            : null;
    }

    private bool TryGetRoom(LanPeer peer, out LanRoom room)
    {
        if (peer.RoomCode is not null && _rooms.TryGetValue(peer.RoomCode, out var foundRoom))
        {
            room = foundRoom;
            return true;
        }

        room = null!;
        return false;
    }

    private static IReadOnlyList<LanPeer> ConnectedPeers(LanRoom room)
    {
        return room.Players.Values
            .Select(player => player.Peer)
            .Where(peer => peer is not null)
            .Cast<LanPeer>()
            .ToArray();
    }

    private static bool TryNormalizeName(string? value, out string name)
    {
        name = value?.Trim() ?? "";
        return name.Length is > 0 and <= 24 && name.All(character => !char.IsControl(character));
    }

    private static string CreateRoomCode()
    {
        Span<byte> randomBytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(randomBytes);
        return string.Create(randomBytes.Length, randomBytes.ToArray(), (characters, bytes) =>
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                characters[index] = RoomAlphabet[bytes[index] % RoomAlphabet.Length];
            }
        });
    }

    private static async Task<LanClientMessage?> ReceiveMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MaximumMessageBytes)
            {
                socket.Abort();
                return null;
            }

            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        try
        {
            return JsonSerializer.Deserialize<LanClientMessage>(message.ToArray(), JsonOptions);
        }
        catch (JsonException)
        {
            return new LanClientMessage { Type = "invalid" };
        }
    }

    private async Task SendAsync(LanPeer peer, object message, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await peer.SendAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) { peer.Abort(); }
        catch (WebSocketException exception)
        {
            logger.LogWarning(exception, "Could not send a LAN message to player {PlayerId}.", peer.PlayerId);
            peer.Abort();
        }
    }

    private Task SendErrorAsync(LanPeer peer, string message, CancellationToken cancellationToken)
    {
        logger.LogWarning("LAN rejected connection={ConnectionId} room={Room} reason={Reason}",
            peer.ConnectionId, peer.RoomCode, message);
        return SendAsync(peer, new { type = "error", message }, cancellationToken);
    }

    private sealed class LanRoom(string code, DateTimeOffset createdAt)
    {
        public object SyncRoot { get; } = new();
        public string Code { get; } = code;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset? FinishedAt { get; set; }
        public Dictionary<string, LanPlayer> Players { get; } = new(StringComparer.Ordinal);
        public string Status { get; set; } = "lobby";
        public GameRegion? Region { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? EndsAt { get; set; }
        public IReadOnlyList<LeaderboardEntry>? Results { get; set; }
        public string GameMode { get; set; } = "classic";
        public bool IsBattle => GameMode != "classic";
        public Dictionary<string, string> Owners { get; } = new(StringComparer.Ordinal);
        public long Revision { get; set; }
    }

    private sealed class LanPlayer(string id, string name, bool isHost, LanPeer peer, string color)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Color { get; } = color;
        public bool IsHost { get; } = isHost;
        public LanPeer? Peer { get; set; } = peer;
        public HashSet<string> FoundIds { get; } = new(StringComparer.Ordinal);
        public int SecondsAtBestScore { get; set; }
        public bool IsActive { get; set; } = true;
    }

    private sealed class LanPeer(WebSocket socket, string connectionId)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public string? RoomCode { get; set; }
        public string ConnectionId { get; } = connectionId;
        public string? PlayerId { get; set; }
        public bool IsHost { get; set; }

        public void Abort() => socket.Abort();

        public async Task CloseOutputAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var acquired = false;
            try
            {
                await _sendLock.WaitAsync(timeout.Token);
                acquired = true;
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Room closed", timeout.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
                socket.Abort();
            }
            finally { if (acquired) _sendLock.Release(); }
        }

        public async Task SendAsync(object message, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    private sealed class LanClientMessage
    {
        public string Type { get; init; } = "";
        public string? Name { get; init; }
        public string? RoomCode { get; init; }
        public string? RegionId { get; init; }
        public string? Guess { get; init; }
        public int? TimeLimitSeconds { get; init; }
        public string? GameMode { get; init; }
    }

    private sealed record OwnedCountry(string Id, string Color);

    private sealed record LeaderboardEntry(
        int Place,
        string Name,
        int GuessedCount,
        int TotalCount,
        int? Seconds,
        string? Color);
}
