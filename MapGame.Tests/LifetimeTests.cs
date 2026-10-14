using MapGame;

namespace MapGame.Tests;

public sealed class LifetimeTests
{
    [Fact]
    public void SinglePlayerCapacityAndIdleExpirationReclaimMemory()
    {
        using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream("MapGame.Data.maps.json")!;
        var clock = new ManualClock();
        var games = new SinglePlayerGameManager(GameCatalog.Load(stream), clock);
        var request = new StartGameRequest("europe", "lives", null, 5);
        var first = games.Start(request);
        for (var index = 1; index < SinglePlayerGameManager.MaximumSessions; index++) games.Start(request);
        Assert.Throws<GameCapacityException>(() => games.Start(request));
        clock.Advance(TimeSpan.FromMinutes(31));
        games.Cleanup();
        Assert.Null(games.Get(first.Id));
        Assert.NotNull(games.Start(request));
    }

    [Fact]
    public void ActiveTimedGamePreservesPrivacyAndFinishedResultExpires()
    {
        using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream("MapGame.Data.maps.json")!;
        var clock = new ManualClock();
        var games = new SinglePlayerGameManager(GameCatalog.Load(stream), clock);
        var game = games.Start(new StartGameRequest("europe", "time", 7200, null));
        for (var index = 0; index < 7; index++)
        {
            clock.Advance(TimeSpan.FromMinutes(15));
            var view = games.Get(game.Id)!;
            Assert.Equal("playing", view.Status);
            Assert.Null(view.GuessedCount);
            Assert.Null(view.TotalCount);
        }
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal("finished", games.Get(game.Id)!.Status);
        clock.Advance(TimeSpan.FromMinutes(16));
        games.Cleanup();
        Assert.Null(games.Get(game.Id));
    }
}

public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan elapsed) => _now += elapsed;
}
