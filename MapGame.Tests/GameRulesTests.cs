using MapGame;

namespace MapGame.Tests;

public sealed class GameRulesTests
{
    [Fact]
    public void NormalizeGuess_IgnoresCaseDiacriticsAndPunctuation()
    {
        Assert.Equal("ceska republika", GameCatalog.NormalizeGuess("  ČESKÁ-republika "));
    }

    [Fact]
    public void FindAnswer_AcceptsSlovakAndEnglishNames()
    {
        var catalog = CreateCatalog();

        Assert.Equal("SVK", catalog.FindAnswer("europe", "SLOVENSKO")?.Id);
        Assert.Equal("SVK", catalog.FindAnswer("europe", "Slovakia")?.Id);
    }

    [Fact]
    public void FindAnswer_DoesNotResolveAmbiguousAliases()
    {
        var catalog = new GameCatalog(
        [
            new GameRegion
            {
                Id = "test",
                Features =
                [
                    Feature("one", "Kongo", "Congo"),
                    Feature("two", "Kongo", "Congo")
                ]
            }
        ]);

        Assert.Null(catalog.FindAnswer("test", "Kongo"));
    }

    [Fact]
    public void StartGame_HidesScoreUntilTheGameEnds()
    {
        var games = CreateManager(new TestTimeProvider());
        var game = games.Start(new StartGameRequest("europe", "time", 60, null));

        Assert.Equal("playing", game.Status);
        Assert.Null(game.GuessedCount);
        Assert.Null(game.TotalCount);
        Assert.Empty(game.FoundIds);
    }

    [Fact]
    public void LivesGame_LosesLifeForWrongAndAlreadyGuessedAnswers()
    {
        var games = CreateManager(new TestTimeProvider());
        var game = games.Start(new StartGameRequest("europe", "lives", null, 2));

        var correct = games.Guess(game.Id, "Slovensko")!;
        Assert.Equal("correct", correct.Outcome);
        Assert.Equal(2, correct.Game.LivesRemaining);

        var duplicate = games.Guess(game.Id, "Slovakia")!;
        Assert.Equal("already-found", duplicate.Outcome);
        Assert.Equal(1, duplicate.Game.LivesRemaining);

        var wrong = games.Guess(game.Id, "Atlantis")!;
        Assert.Equal("finished", wrong.Outcome);
        Assert.Equal("lives", wrong.Game.EndReason);
        Assert.Equal(1, wrong.Game.GuessedCount);
        Assert.Equal(2, wrong.Game.TotalCount);
    }

    [Fact]
    public void TimeGame_EndsAtTheLimitAndRevealsTheFinalScore()
    {
        var clock = new TestTimeProvider();
        var games = CreateManager(clock);
        var game = games.Start(new StartGameRequest("europe", "time", 60, null));
        games.Guess(game.Id, "Slovensko");

        clock.Advance(TimeSpan.FromSeconds(61));
        var result = games.Get(game.Id)!;

        Assert.Equal("finished", result.Status);
        Assert.Equal("time", result.EndReason);
        Assert.Equal(1, result.GuessedCount);
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public void Finish_EndsGameEarlyAndRevealsTheCurrentScore()
    {
        var games = CreateManager(new TestTimeProvider());
        var game = games.Start(new StartGameRequest("europe", "time", 60, null));
        games.Guess(game.Id, "Slovensko");

        var result = games.Finish(game.Id)!;

        Assert.Equal("finished", result.Status);
        Assert.Equal("manual", result.EndReason);
        Assert.Equal(1, result.GuessedCount);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal("manual", games.Finish(game.Id)!.EndReason);
    }

    [Fact]
    public void StartGame_RejectsOutOfRangeOptions()
    {
        var games = CreateManager(new TestTimeProvider());

        Assert.Throws<ArgumentException>(() => games.Start(new StartGameRequest("europe", "time", 59, null)));
        Assert.Throws<ArgumentException>(() => games.Start(new StartGameRequest("europe", "time", null, null)));
        Assert.Throws<ArgumentException>(() => games.Start(new StartGameRequest("europe", "lives", null, 6)));
        Assert.Throws<ArgumentException>(() => games.Start(new StartGameRequest("europe", "lives", null, null)));
        Assert.Throws<ArgumentException>(() => games.Start(new StartGameRequest("unknown", "time", 60, null)));
    }

    [Fact]
    public void BundledMaps_ContainAllRegionsAndFiftyStatesWithoutLeakingAnswersToTheBrowser()
    {
        using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream("MapGame.Data.maps.json")!;
        var catalog = GameCatalog.Load(stream);
        var publicShapesPath = Path.Combine(AppContext.BaseDirectory, "map-shapes.json");
        var publicShapes = File.ReadAllText(publicShapesPath);

        Assert.Equal(6, catalog.Regions.Count);
        Assert.Equal(50, catalog.GetRegion("usa").Features.Count);
        Assert.Equal("SVK", catalog.FindAnswer("europe", "Slovensko")?.Id);
        Assert.Equal("US-NY", catalog.FindAnswer("usa", "New York")?.Id);
        foreach (var region in catalog.Regions)
        {
            foreach (var feature in region.Features)
            {
                Assert.Equal(feature.Id, catalog.FindAnswer(region.Id, feature.Name)?.Id);
            }
        }
        Assert.DoesNotContain("Slovensko", publicShapes, StringComparison.Ordinal);
        Assert.DoesNotContain("englishName", publicShapes, StringComparison.Ordinal);
    }

    private static GameCatalog CreateCatalog()
    {
        return new GameCatalog(
        [
            new GameRegion
            {
                Id = "europe",
                Name = "Európa",
                Features =
                [
                    Feature("SVK", "Slovensko", "Slovakia"),
                    Feature("AUT", "Rakúsko", "Austria")
                ]
            }
        ]);
    }

    private static MapFeature Feature(string id, string name, string englishName)
    {
        return new MapFeature
        {
            Id = id,
            Name = name,
            EnglishName = englishName,
            Geometry = new MapGeometry()
        };
    }

    private static SinglePlayerGameManager CreateManager(TestTimeProvider timeProvider)
    {
        return new SinglePlayerGameManager(CreateCatalog(), timeProvider);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
