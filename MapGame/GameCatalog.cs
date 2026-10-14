using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapGame;

public sealed class GameCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, GameRegion> _regionsById;
    private readonly Dictionary<string, Dictionary<string, MapFeature>> _answersByRegion;

    public GameCatalog(IEnumerable<GameRegion> regions)
    {
        Regions = regions.ToArray();
        _regionsById = Regions.ToDictionary(region => region.Id, StringComparer.OrdinalIgnoreCase);
        _answersByRegion = Regions.ToDictionary(
            region => region.Id,
            BuildAnswerIndex,
            StringComparer.OrdinalIgnoreCase);

        if (Regions.Count == 0 || Regions.Any(region => region.Features.Count == 0))
        {
            throw new InvalidDataException("Map data must contain at least one feature per region.");
        }
    }

    public IReadOnlyList<GameRegion> Regions { get; }

    public static GameCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static GameCatalog Load(Stream stream)
    {
        var data = JsonSerializer.Deserialize<MapCatalogDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("The map data file is empty.");
        return new GameCatalog(data.Regions);
    }

    public GameRegion GetRegion(string? id)
    {
        return id is not null && _regionsById.TryGetValue(id, out var region)
            ? region
            : throw new ArgumentException("Zvolený región neexistuje.", nameof(id));
    }

    public GameRegion GetRandomRegion(Random? random = null)
    {
        return Regions[(random ?? Random.Shared).Next(Regions.Count)];
    }

    public MapFeature? FindAnswer(string regionId, string? guess)
    {
        var normalized = NormalizeGuess(guess ?? "");
        return normalized.Length > 0
            && _answersByRegion[regionId].TryGetValue(normalized, out var feature)
                ? feature
                : null;
    }

    public static string NormalizeGuess(string guess)
    {
        var decomposed = guess.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        var needsSpace = false;

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (needsSpace && result.Length > 0)
                {
                    result.Append(' ');
                }

                result.Append(char.ToLowerInvariant(character));
                needsSpace = false;
            }
            else
            {
                needsSpace = true;
            }
        }

        return result.ToString();
    }

    private static Dictionary<string, MapFeature> BuildAnswerIndex(GameRegion region)
    {
        return region.Features
            .SelectMany(feature => new[] { feature.Name, feature.EnglishName }
                .Concat(feature.Aliases)
                .Select(alias => (Key: NormalizeGuess(alias), Feature: feature)))
            .Where(entry => entry.Key.Length > 0)
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Select(entry => entry.Feature.Id).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Feature, StringComparer.Ordinal);
    }
}

public sealed class MapCatalogDocument
{
    public List<GameRegion> Regions { get; init; } = [];
}

public sealed class GameRegion
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<MapFeature> Features { get; init; } = [];
}

public sealed class MapFeature
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string EnglishName { get; init; } = "";
    public string[] Aliases { get; init; } = [];
    public MapGeometry Geometry { get; init; } = new();
}

public sealed class MapGeometry
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("coordinates")]
    public JsonElement Coordinates { get; init; }
}
