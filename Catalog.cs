using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinKit;

internal interface ICatalogSource
{
    /// <summary>Downloads the current catalog document (JSON).</summary>
    Task<string> FetchCatalogAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The list of managed utilities is data, not code: <c>catalog.json</c> on the default branch of the
/// WinKit repository. WinKit downloads it on every refresh and caches the last good copy, so adding
/// a utility never requires a new WinKit release.
/// </summary>
internal static partial class Catalog
{
    public const string DefaultLocation = "https://raw.githubusercontent.com/victor141516/WinKit/HEAD/catalog.json";
    public const int MaximumBytes = 256 * 1024;

    /// <summary>Catalog URL or local file; <c>WINKIT_CATALOG</c> overrides it for development and tests.</summary>
    public static string Location =>
        Environment.GetEnvironmentVariable("WINKIT_CATALOG") is { Length: > 0 } location ? location : DefaultLocation;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static IReadOnlyList<AppSpec> Parse(string json)
    {
        if (json.Length > MaximumBytes) throw new InvalidDataException("The catalog is too large.");
        var document = JsonSerializer.Deserialize<CatalogDocument>(json, Options)
            ?? throw new InvalidDataException("The catalog is empty.");
        if (document.Schema != 1) throw new InvalidDataException($"Unsupported catalog schema {document.Schema}.");
        if (document.Utilities is null) throw new InvalidDataException("The catalog has no utilities.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<AppSpec>();
        foreach (var entry in document.Utilities)
        {
            if (entry is null || !IdPattern().IsMatch(entry.Id ?? ""))
                throw new InvalidDataException($"Invalid utility id \"{entry?.Id}\".");
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 40)
                throw new InvalidDataException($"Invalid name for {entry.Id}.");
            if ((entry.Description ?? "").Length > 80)
                throw new InvalidDataException($"The description of {entry.Id} is too long.");
            if (!RepositoryPattern().IsMatch(entry.Repository ?? ""))
                throw new InvalidDataException($"{entry.Id} must point to a https://github.com/<owner>/<repository> URL.");
            if (!ExecutablePattern().IsMatch(entry.Executable ?? ""))
                throw new InvalidDataException($"Invalid executable for {entry.Id}.");
            if (!ids.Add(entry.Id!) || !executables.Add(entry.Executable!))
                throw new InvalidDataException($"Duplicate utility {entry.Id}.");
            apps.Add(new AppSpec(entry.Id!, entry.Name.Trim(), entry.Description?.Trim() ?? "", entry.Repository!, entry.Executable!));
        }
        return apps;
    }

    public static string Serialize(IReadOnlyList<AppSpec> apps) => JsonSerializer.Serialize(
        new CatalogDocument(1, apps.Select(app => new CatalogEntry(app.Id, app.Name, app.Description, app.RepositoryUrl, app.Executable)).ToArray()),
        Options);

    /// <summary>
    /// Takes the remote catalog as the list of utilities, in its order. A utility removed from the
    /// catalog stays (after the others) while <paramref name="keep"/> holds, so an enabled utility
    /// can still be managed and disabled.
    /// </summary>
    public static IReadOnlyList<AppSpec> Merge(IReadOnlyList<AppSpec> remote, IReadOnlyList<AppSpec> current, Func<AppSpec, bool> keep)
    {
        var ids = remote.Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return remote.Concat(current.Where(app => !ids.Contains(app.Id) && keep(app))).ToArray();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();
    [GeneratedRegex("^https://github\\.com/[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}\\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutablePattern();

    private sealed record CatalogDocument(int Schema, CatalogEntry[]? Utilities);
    private sealed record CatalogEntry(string? Id, string? Name, string? Description, string? Repository, string? Executable);
}
