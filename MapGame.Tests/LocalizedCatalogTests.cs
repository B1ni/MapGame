using System.Text;
using System.Text.Json;
using MapGame;

namespace MapGame.Tests;

public sealed class LocalizedCatalogTests
{
    private static GameCatalog Load()
    {
        using var geometry = Resource("maps.json");
        return GameCatalog.Load(geometry);
    }

    private static Stream Resource(string name) =>
        typeof(GameCatalog).Assembly.GetManifestResourceStream($"MapGame.Data.{name}")!;

    [Fact]
    public void NorthAmericaIncludesUsaAsOneCountryWhileUsMapKeepsFiftyStatesAndSingleHistory()
    {
        var catalog = Load();
        foreach (var alias in new[] { "USA", "US", "Spojene staty americke", "United States", "America", "Amerika" })
            Assert.Equal("USA", catalog.FindAnswer("north-america", alias)?.Id);
        Assert.Equal("US-CA", catalog.FindAnswer("usa", "California")?.Id);
        Assert.Equal(50, catalog.GetRegion("usa").Features.Count);
        Assert.Null(catalog.FindAnswer("usa", "USA"));
        var games = new SinglePlayerGameManager(catalog, TimeProvider.System);
        var game = games.Start(new("north-america", "lives", null, 5));
        Assert.Empty(game.History);
        var reply = games.Guess(game.Id, "USA")!;
        Assert.Equal("correct", reply.Outcome);
        Assert.Equal(new[] { "Spojené štáty americké" }, reply.Game.History);
        Assert.Null(reply.Game.GuessedCount);
        Assert.Equal("already-found", games.Guess(game.Id, "United States")!.Outcome);
        Assert.Single(games.Get(game.Id)!.History);
    }

    [Fact]
    public void EveryRegionAndFeatureHasBothLanguagesAndEveryNameAndCodeResolves()
    {
        var catalog = Load();
        var counts = new Dictionary<string, int>
        {
            ["europe"] = 49, ["asia"] = 46, ["north-america"] = 23,
            ["south-america"] = 12, ["africa"] = 54, ["usa"] = 50
        };
        Assert.Equal(234, catalog.Regions.Sum(region => region.Features.Count));
        Assert.Equal(counts.Keys.Order(), catalog.Regions.Select(region => region.Id).Order());
        foreach (var region in catalog.Regions)
        {
            Assert.Equal(counts[region.Id], region.Features.Count);
            Assert.Equal(new[] { "en", "sk" }, region.Localizations.Keys.Order());
            Assert.Equal(region.Name, region.GetName("unsupported"));
            foreach (var feature in region.Features)
            {
                Assert.Equal(new[] { "en", "sk" }, feature.Localizations.Keys.Order());
                Assert.Contains(feature.Id, feature.Aliases);
                Assert.Contains(feature.Aliases, code => code.Length == 2 || feature.Id.StartsWith("GB-"));
                foreach (var name in feature.Localizations.Values.Select(name => name.Name).Concat(feature.Aliases))
                {
                    Assert.False(string.IsNullOrWhiteSpace(name));
                    Assert.Equal(feature.Id, catalog.FindAnswer(region.Id, name)?.Id);
                    Assert.Equal(feature.Id, catalog.FindAnswer(region.Id, GameCatalog.NormalizeGuess(name))?.Id);
                    Assert.Equal(feature.Id, catalog.FindAnswer(region.Id, $"  {name.ToUpperInvariant()}  ")?.Id);
                }
                Assert.Equal(feature.Name, feature.GetName("sk"));
                Assert.Equal(feature.EnglishName, feature.GetName("en"));
                if (region.Id == "usa")
                    Assert.Equal(feature.Id, catalog.FindAnswer("usa", feature.Id[3..])?.Id);
            }
        }
    }

    [Theory]
    [InlineData("Kosovo")]
    [InlineData("KOSOVO")]
    [InlineData("Kosovska republika")]
    [InlineData("Republic of Kosovo")]
    [InlineData("XK")]
    [InlineData("XKX")]
    [InlineData("KOS")]
    public void KosovoCanBeGuessedLastAndCompletesEurope(string answer)
    {
        var catalog = Load();
        Assert.Equal("KOS", catalog.FindAnswer("europe", answer)?.Id);
        var games = new SinglePlayerGameManager(catalog, TimeProvider.System);
        var game = games.Start(new("europe", "lives", null, 5));
        foreach (var feature in catalog.GetRegion("europe").Features.Where(feature => feature.Id != "KOS"))
            Assert.Equal("correct", games.Guess(game.Id, feature.Name)!.Outcome);
        var reply = games.Guess(game.Id, answer)!;
        Assert.Contains("KOS", reply.Game.FoundIds);
        Assert.Equal("finished", reply.Game.Status);
        Assert.Equal(49, reply.Game.GuessedCount);
        Assert.Equal(49, reply.Game.TotalCount);
    }

    [Fact]
    public void EveryCuratedAliasResolvesUnlessSharedByDifferentAnswers()
    {
        var catalog = Load();
        foreach (var region in catalog.Regions)
        {
            var aliases = region.Features.SelectMany(feature =>
                feature.Localizations.Values.SelectMany(name => name.Aliases.Prepend(name.Name))
                    .Concat(feature.Aliases)
                    .Select(alias => (Feature: feature.Id, Alias: alias)));
            foreach (var group in aliases.GroupBy(entry => GameCatalog.NormalizeGuess(entry.Alias)))
            {
                var ids = group.Select(entry => entry.Feature).Distinct().ToArray();
                if (ids.Length == 1)
                    Assert.Equal(ids[0], catalog.FindAnswer(region.Id, group.Key)?.Id);
                else
                    Assert.Null(catalog.FindAnswer(region.Id, group.Key));
            }
        }
        Assert.Null(catalog.FindAnswer("africa", "Kongo"));
        Assert.Equal("COG", catalog.FindAnswer("africa", "Congo")?.Id);
    }

    [Theory]
    [InlineData("GB-ENG", "Anglicko", "England", "ENG")]
    [InlineData("GB-SCT", "Skotsko", "Scotland", "SCO")]
    [InlineData("GB-WLS", "Wales", "Wales", "WAL")]
    [InlineData("GB-NIR", "Severne Irsko", "Northern Ireland", "NIR")]
    public void UkAnswersAreIndependent(string id, params string[] aliases)
    {
        var catalog = Load();
        foreach (var alias in aliases.Prepend(id))
            Assert.Equal(id, catalog.FindAnswer("europe", alias)?.Id);
        var games = new SinglePlayerGameManager(catalog, TimeProvider.System);
        var game = games.Start(new("europe", "lives", null, 5));
        Assert.Equal("correct", games.Guess(game.Id, aliases[0])!.Outcome);
        var duplicate = games.Guess(game.Id, aliases[^1])!;
        Assert.Equal("already-found", duplicate.Outcome);
        Assert.Equal(4, duplicate.Game.LivesRemaining);
        Assert.Equal(new[] { id }, duplicate.Game.FoundIds);
    }

    [Theory]
    [InlineData("UK")]
    [InlineData("GB")]
    [InlineData("GBR")]
    [InlineData("Británia")]
    [InlineData("Veľká Británia")]
    [InlineData("Britain")]
    [InlineData("Great Britain")]
    [InlineData("United Kingdom")]
    [InlineData("Spojené kráľovstvo")]
    [InlineData("Czechoslovakia")]
    [InlineData("Československo")]
    public void AmbiguousOrHistoricalUnrelatedAnswersDoNotResolve(string guess) =>
        Assert.Null(Load().FindAnswer("europe", guess));

    [Theory]
    [InlineData("asia", "Turkey", "TUR")]
    [InlineData("asia", "UAE", "ARE")]
    [InlineData("asia", "KLDR", "PRK")]
    [InlineData("africa", "Cote d'Ivoire", "CIV")]
    [InlineData("africa", "JAR", "ZAF")]
    [InlineData("usa", "CA", "US-CA")]
    [InlineData("usa", "New York State", "US-NY")]
    public void CommonNamesAndAbbreviationsAreAccepted(string region, string guess, string id) =>
        Assert.Equal(id, Load().FindAnswer(region, guess)?.Id);

    [Fact]
    public void EmbeddedGeometryAndPublicFeaturesHaveIdenticalIdsAndCoordinatesButNoAnswers()
    {
        var catalog = Load();
        using var embedded = JsonDocument.Parse(Resource("maps.json"));
        using var shapes = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "map-shapes.json")));
        var serverRegions = embedded.RootElement.GetProperty("regions").EnumerateArray().ToArray();
        var publicRegions = shapes.RootElement.GetProperty("regions").EnumerateArray().ToArray();
        Assert.Equal(serverRegions.Length, publicRegions.Length);
        foreach (var (server, browser) in serverRegions.Zip(publicRegions))
        {
            var regionId = server.GetProperty("id").GetString()!;
            Assert.Equal(regionId, browser.GetProperty("id").GetString());
            Assert.Equal(catalog.GetRegion(regionId).Name, browser.GetProperty("name").GetString());
            Assert.Equal(new[] { "features", "id" }, server.EnumerateObject().Select(p => p.Name).Order());
            var features = server.GetProperty("features").EnumerateArray().ToArray();
            var publicFeatures = browser.GetProperty("features").EnumerateArray().ToArray();
            Assert.Equal(features.Length, publicFeatures.Length);
            foreach (var (feature, publicFeature) in features.Zip(publicFeatures))
            {
                Assert.Equal(new[] { "geometry", "id" }, feature.EnumerateObject().Select(p => p.Name).Order());
                Assert.Equal(new[] { "geometry", "id" }, publicFeature.EnumerateObject().Select(p => p.Name).Order());
                Assert.Equal(feature.GetRawText(), publicFeature.GetRawText());
                Assert.NotEqual("GBR", feature.GetProperty("id").GetString());
            }
        }
    }

    [Theory]
    [InlineData("""{"regions":{},"features":{}}""")]
    [InlineData("""{"regions":{"test":{"names":{"sk":{"name":"Test"},"en":{"name":"Test"}}}},"features":{"ONE":{"names":{"sk":{"name":"Jeden"}}}}}""")]
    [InlineData("""{"regions":{"test":{"names":{"sk":{"name":"Test"},"en":{"name":"Test"}}}},"features":{"OTHER":{"names":{"sk":{"name":"Iný"},"en":{"name":"Other"}}}}}""")]
    public void LoaderRejectsMissingLanguagesAndMismatchedStableIds(string names)
    {
        using var geometry = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"regions":[{"id":"test","features":[{"id":"ONE"}]}]}"""));
        using var labels = new MemoryStream(Encoding.UTF8.GetBytes(names));
        Assert.Throws<InvalidDataException>(() => GameCatalog.Load(geometry, labels));
    }
}
