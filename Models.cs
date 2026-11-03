using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinKit;

internal sealed record AppSpec(string Id, string Name, string Description, string RepositoryUrl, string Executable)
{
    public string LatestReleaseUrl => RepositoryUrl.Replace("https://github.com/", "https://api.github.com/repos/", StringComparison.Ordinal) + "/releases/latest";
    public static readonly AppSpec[] All =
    [
        new("comienzo", "Comienzo", "A faster Start menu", "https://github.com/victor141516/comienzo", "Comienzo.exe"),
        new("voolime", "Voolime", "Volume controls for each app", "https://github.com/victor141516/voolime", "Voolime.exe"),
        new("trapezoid", "Trapezoid", "Arrange windows with shortcuts", "https://github.com/victor141516/trapezoid", "Trapezoid.exe"),
        new("captura", "Captura", "Capture and annotate screenshots", "https://github.com/victor141516/Captura", "Captura.exe"),
        new("altabi", "Altabi", "Alt+Tab grouped by app", "https://github.com/victor141516/altabi", "Altabi.exe")
    ];
}

internal sealed class Preferences
{
    public Dictionary<string, bool> Enabled { get; set; } = new();
    public bool StartWithWindows { get; set; } = true;
    public bool IsEnabled(AppSpec app) => Enabled.GetValueOrDefault(app.Id);
}

internal sealed record InstalledPackage(string Version, string AssetName, string Sha256, string ExecutablePath);
internal sealed record AppStatus(bool Enabled, string Message, bool IsError = false, string? Version = null);

internal sealed class StateStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public bool Exists => File.Exists(SettingsPath);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public Preferences Load() => Exists
        ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath)) ?? throw new InvalidDataException("The settings file is empty.")
        : new Preferences();
    public void Save(Preferences preferences) => WriteJson(SettingsPath, preferences);
    public string AppRoot(AppSpec app) => Path.Combine(Root, "apps", app.Id);
    public string PackagePath(AppSpec app) => Path.Combine(AppRoot(app), "installed.json");
    public InstalledPackage? ReadPackage(AppSpec app)
    {
        if (!File.Exists(PackagePath(app))) return null;
        var package = JsonSerializer.Deserialize<InstalledPackage>(File.ReadAllText(PackagePath(app)));
        if (package is null || !IsInside(package.ExecutablePath, AppRoot(app)) || !File.Exists(package.ExecutablePath)) return null;
        return package;
    }
    public void WritePackage(AppSpec app, InstalledPackage package) => WriteJson(PackagePath(app), package);
    public static bool IsInside(string path, string directory)
    {
        string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }
}

internal sealed record Release(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("assets")] ReleaseAsset[] Assets,
    [property: JsonPropertyName("prerelease")] bool Prerelease = false,
    [property: JsonPropertyName("draft")] bool Draft = false);

internal sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string Url,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest = null);
