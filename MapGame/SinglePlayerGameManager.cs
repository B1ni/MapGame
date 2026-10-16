namespace MapGame;

public sealed class SinglePlayerGameManager(GameCatalog catalog, TimeProvider timeProvider)
{
    private readonly Dictionary<string, GameSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _sessionsLock = new();
    public const int MaximumSessions = 256;

    public void Cleanup()
    {
        lock (_sessionsLock) CleanupLocked();
    }

    private void CleanupLocked()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var session in _sessions.Values.ToArray())
        {
            lock (session.SyncRoot)
            {
                session.UpdateStatus(now);
                if (now - session.StartedAt >= TimeSpan.FromHours(4)
                    || now - session.LastAccess >= TimeSpan.FromMinutes(30)
                    || session.FinishedAt is { } finished && now - finished >= TimeSpan.FromMinutes(15))
                    _sessions.Remove(session.Id);
            }
        }
    }

    public GameView Start(StartGameRequest request)
    {
        var regionId = request.RegionId ?? "";
        var region = string.Equals(regionId, "random", StringComparison.OrdinalIgnoreCase)
            ? catalog.GetRandomRegion()
            : catalog.GetRegion(regionId);

        var mode = (request.Mode ?? "").ToLowerInvariant() switch
        {
            "time" => GameMode.Time,
            "lives" => GameMode.Lives,
            _ => throw new ArgumentException("Zvolený herný režim neexistuje.", nameof(request))
        };

        if (mode == GameMode.Time && request.TimeLimitSeconds is null or < 60 or > 7200)
        {
            throw new ArgumentException("Časový limit musí byť od 1 do 120 minút.", nameof(request));
        }

        if (mode == GameMode.Lives && request.Lives is null or < 1 or > 5)
        {
            throw new ArgumentException("Počet životov musí byť od 1 do 5.", nameof(request));
        }

        var session = new GameSession(
            Guid.NewGuid().ToString("N"),
            region,
            mode,
            request.TimeLimitSeconds,
            request.Lives,
            timeProvider.GetUtcNow());

        lock (_sessionsLock)
        {
            CleanupLocked();
            if (_sessions.Count >= MaximumSessions) throw new GameCapacityException();
            _sessions.Add(session.Id, session);
        }

        return session.GetView(timeProvider.GetUtcNow());
    }

    public GameView? Get(string id)
    {
        var session = Find(id);
        if (session is null)
        {
            return null;
        }

        lock (session.SyncRoot)
        {
            return session.GetView(timeProvider.GetUtcNow());
        }
    }

    public GameView? Finish(string id)
    {
        var session = Find(id);
        if (session is null)
        {
            return null;
        }

        lock (session.SyncRoot)
        {
            var now = timeProvider.GetUtcNow();
            session.UpdateStatus(now);
            if (session.EndReason is null)
            {
                session.EndReason = "manual";
                session.FinishedAt = now;
            }

            return session.GetView(now);
        }
    }

    public GuessReply? Guess(string id, string? guess)
    {
        var session = Find(id);
        if (session is null)
        {
            return null;
        }

        lock (session.SyncRoot)
        {
            var now = timeProvider.GetUtcNow();
            session.UpdateStatus(now);
            if (session.EndReason is not null)
            {
                return new GuessReply("finished", "Hra sa už skončila.", session.GetView(now));
            }

            if (guess is null || guess.Length > 100)
            {
                session.RecordWrongGuess(now);
                var outcome = session.EndReason == "lives" ? "finished" : "incorrect";
                var message = session.EndReason == "lives"
                    ? "Nesprávny tip. Došli ti životy."
                    : "Neplatný tip.";
                return new GuessReply(outcome, message, session.GetView(now));
            }

            var feature = catalog.FindAnswer(session.Region.Id, guess);
            if (feature is null)
            {
                session.RecordWrongGuess(now);
                var outcome = session.EndReason == "lives" ? "finished" : "incorrect";
                var message = session.EndReason == "lives"
                    ? "Nesprávny tip. Došli ti životy."
                    : "Tento štát v zvolenom regióne nie je.";
                return new GuessReply(outcome, message, session.GetView(now));
            }

            if (!session.FoundIds.Add(feature.Id))
            {
                session.RecordWrongGuess(now);
                var outcome = session.EndReason == "lives" ? "finished" : "already-found";
                var message = session.EndReason == "lives"
                    ? "Tento štát už máš uhádnutý. Došli ti životy."
                    : "Tento štát už máš uhádnutý.";
                return new GuessReply(outcome, message, session.GetView(now));
            }

            if (session.FoundIds.Count == session.Region.Features.Count)
            {
                session.EndReason = "completed";
                session.FinishedAt = now;
            }

            return new GuessReply("correct", $"Správne: {feature.Name}.", session.GetView(now));
        }
    }

    private GameSession? Find(string id)
    {
        lock (_sessionsLock)
        {
            CleanupLocked();
            var session = _sessions.GetValueOrDefault(id);
            if (session is not null)
            {
                lock (session.SyncRoot) session.LastAccess = timeProvider.GetUtcNow();
            }
            return session;
        }

    }
}

public sealed class GameCapacityException : Exception;

public sealed record StartGameRequest(
    string? RegionId,
    string? Mode,
    int? TimeLimitSeconds,
    int? Lives);

public sealed record GuessRequest(string? Guess);

public sealed record GuessReply(string Outcome, string Message, GameView Game);

public sealed record GameView(
    string Id,
    string RegionId,
    string RegionName,
    string Mode,
    string Status,
    string[] FoundIds,
    int? RemainingSeconds,
    int? LivesRemaining,
    int? GuessedCount,
    int? TotalCount,
    string? EndReason,
    string[] History);

public enum GameMode
{
    Time,
    Lives
}

internal sealed class GameSession
{
    public GameSession(
        string id,
        GameRegion region,
        GameMode mode,
        int? timeLimitSeconds,
        int? lives,
        DateTimeOffset startedAt)
    {
        Id = id;
        Region = region;
        Mode = mode;
        TimeLimitSeconds = timeLimitSeconds;
        InitialLives = lives;
        LivesRemaining = lives;
        StartedAt = startedAt;
        LastAccess = startedAt;
    }

    public object SyncRoot { get; } = new();
    public string Id { get; }
    public GameRegion Region { get; }
    public GameMode Mode { get; }
    public int? TimeLimitSeconds { get; }
    public int? InitialLives { get; }
    public int? LivesRemaining { get; private set; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset LastAccess { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? EndReason { get; set; }
    public HashSet<string> FoundIds { get; } = new(StringComparer.Ordinal);

    public void RecordWrongGuess(DateTimeOffset now)
    {
        if (Mode != GameMode.Lives || LivesRemaining is null)
        {
            return;
        }

        LivesRemaining--;
        if (LivesRemaining == 0)
        {
            EndReason = "lives";
            FinishedAt = now;
        }
    }

    public void UpdateStatus(DateTimeOffset now)
    {
        if (EndReason is null && Mode == GameMode.Time && RemainingSeconds(now) == 0)
        {
            EndReason = "time";
            FinishedAt = now;
        }
    }

    public GameView GetView(DateTimeOffset now)
    {
        UpdateStatus(now);
        var finished = EndReason is not null;
        return new GameView(
            Id,
            Region.Id,
            Region.Name,
            Mode == GameMode.Time ? "time" : "lives",
            finished ? "finished" : "playing",
            FoundIds.ToArray(),
            Mode == GameMode.Time ? RemainingSeconds(now) : null,
            LivesRemaining,
            finished ? FoundIds.Count : null,
            finished ? Region.Features.Count : null,
            EndReason,
            FoundIds.Select(id => Region.Features.Single(feature => feature.Id == id).Name).ToArray());
    }

    private int RemainingSeconds(DateTimeOffset now)
    {
        return Math.Max(0, (int)Math.Ceiling((StartedAt.AddSeconds(TimeLimitSeconds!.Value) - now).TotalSeconds));
    }
}
