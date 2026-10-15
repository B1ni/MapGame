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
        using var names = File.OpenRead(Path.Combine(Path.GetDirectoryName(path)!, "country-names.json"));
        return Load(stream, names);
    }

    public static GameCatalog Load(Stream stream)
    {
        using var names = typeof(GameCatalog).Assembly.GetManifestResourceStream("MapGame.Data.country-names.json")
            ?? throw new InvalidDataException("The localized name catalog is missing.");
        return Load(stream, names);
    }

    public static GameCatalog Load(Stream stream, Stream names)
    {
        var data = JsonSerializer.Deserialize<MapCatalogDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("The map data file is empty.");
        var labels = JsonSerializer.Deserialize<LocalizedCatalogDocument>(names, JsonOptions)
            ?? throw new InvalidDataException("The localized name catalog is empty.");
        var regionIds = data.Regions.Select(region => region.Id).ToArray();
        var featureIds = data.Regions.SelectMany(region => region.Features).Select(feature => feature.Id).ToArray();
        ValidateLabels(regionIds, labels.Regions);
        ValidateLabels(featureIds, labels.Features);

        return new GameCatalog(data.Regions.Select(region => new GameRegion
        {
            Id = region.Id,
            Localizations = labels.Regions[region.Id].Names,
            Features = region.Features.Select(feature => new MapFeature
            {
                Id = feature.Id,
                Geometry = feature.Geometry,
                Localizations = labels.Features[feature.Id].Names,
                Aliases = labels.Features[feature.Id].Codes
            }).ToList()
        }));
    }

    private static void ValidateLabels(string[] ids, Dictionary<string, LocalizedEntry> entries)
    {
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
            || !ids.ToHashSet(StringComparer.Ordinal).SetEquals(entries.Keys)
            || entries.Values.Any(entry => new[] { "sk", "en" }.Any(language =>
                !entry.Names.TryGetValue(language, out var name) || string.IsNullOrWhiteSpace(name.Name))))
            throw new InvalidDataException("Geometry and localized sk/en catalog IDs must match exactly.");
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
                .Concat(feature.Localizations.Values.SelectMany(name => name.Aliases.Prepend(name.Name)))
                .Select(alias => (Key: NormalizeGuess(alias), Feature: feature)))
            .Where(entry => entry.Key.Length > 0)
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Select(entry => entry.Feature.Id).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Feature, StringComparer.Ordinal);
    }
}

public sealed class MapCatalogDocument
{
    public List<RegionGeometry> Regions { get; init; } = [];
}

public sealed class RegionGeometry
{
    public string Id { get; init; } = "";
    public List<FeatureGeometry> Features { get; init; } = [];
}

public sealed class FeatureGeometry
{
    public string Id { get; init; } = "";
    public MapGeometry Geometry { get; init; } = new();
}

public sealed class LocalizedCatalogDocument
{
    public Dictionary<string, LocalizedEntry> Regions { get; init; } = [];
    public Dictionary<string, LocalizedEntry> Features { get; init; } = [];
}

public sealed class LocalizedEntry
{
    public Dictionary<string, LocalizedName> Names { get; init; } = [];
    public string[] Codes { get; init; } = [];
}

public sealed class LocalizedName
{
    public string Name { get; init; } = "";
    public string[] Aliases { get; init; } = [];
}

public class LocalizedMapItem
{
    private readonly string _name = "";
    public Dictionary<string, LocalizedName> Localizations { get; init; } = [];
    public string Name
    {
        get => GetName("sk");
        init => _name = value;
    }

    public string GetName(string language) =>
        Localizations.GetValueOrDefault(language)?.Name
        ?? Localizations.GetValueOrDefault("sk")?.Name
        ?? _name;
}

public sealed class GameRegion : LocalizedMapItem
{
    public string Id { get; init; } = "";
    public List<MapFeature> Features { get; init; } = [];
}

public sealed class MapFeature : LocalizedMapItem
{
    private readonly string _englishName = "";
    public string Id { get; init; } = "";
    public string EnglishName
    {
        get => Localizations.GetValueOrDefault("en")?.Name ?? _englishName;
        init => _englishName = value;
    }
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
