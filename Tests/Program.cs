using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using WinKit;

if (args.Contains("--background")) { Thread.Sleep(60000); return 0; }
if (args.FirstOrDefault() == "--verify-live")
{
    string liveRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "WinKitLive-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(liveRoot);
    using var releases = new GitHubReleases();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    var catalog = Catalog.Parse(await releases.FetchCatalogAsync(timeout.Token));
    Console.WriteLine($"Catalog {Catalog.Location}: {string.Join(", ", catalog.Select(app => app.Name))}");
    foreach (var app in catalog)
    {
        ManagedRelease release = await releases.ResolveAsync(app, timeout.Token);
        string appDirectory = Path.Combine(liveRoot, app.Id);
        Directory.CreateDirectory(appDirectory);
        string archive = Path.Combine(appDirectory, release.Asset.Name);
        if (File.Exists(archive)) File.Delete(archive);
        Console.WriteLine($"Downloading {app.Name} {release.Version}: {release.Asset.Name}");
        await releases.DownloadAsync(release.Asset, archive, new SilentProgress(), timeout.Token);
        string executable = await PackageExtractor.ExtractAsync(archive, Path.Combine(appDirectory, Guid.NewGuid().ToString("N")), app.Executable, release.Sha256, timeout.Token);
        Console.WriteLine($"VERIFIED {app.Name} {release.Version} SHA256={release.Sha256} EXE={new FileInfo(executable).Length} bytes");
    }
    Console.WriteLine($"All {catalog.Count} latest GitHub packages downloaded and verified. No applications were started or startup entries changed.");
    return 0;
}
string root = Path.Combine(Path.GetTempPath(), "WinKitTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Log.Root = root;
AppSpec[] Apps = new[] { "one", "two", "three", "four", "five" }.Select(id =>
    new AppSpec(id, char.ToUpperInvariant(id[0]) + id[1..], "Test utility", "https://github.com/example/" + id, char.ToUpperInvariant(id[0]) + id[1..] + ".exe")).ToArray();
var tests = new List<(string, Func<Task>)>
{
    ("Latest release and architecture are read from manifest", ReleaseSelection),
    ("Non-portable or unsafe release is rejected", InvalidRelease),
    ("Partial and full selections install only selected utilities", Selections),
    ("Adding a utility preserves existing startup selections", ExistingSelection),
    ("Every startup checks latest release without redundant downloads", RepeatStartup),
    ("New release replaces running app and retains rollback package", Upgrade),
    ("Offline startup launches installed package", Offline),
    ("Failed download leaves installed package usable", FailedDownload),
    ("Failed launch rolls back to previous package", Rollback),
    ("Disable cancels pending download and prevents restart", CancelDownload),
    ("Rapid disable and re-enable uses the current selection", RapidToggle),
    ("Disable persists and removes independent startup", Disable),
    ("ZIP traversal, missing EXE, and checksum mismatch are rejected", UnsafeArchives),
    ("Process stop is scoped to installed paths", ScopedProcesses),
    ("Paths outside managed directory are rejected", InvalidStoredPath),
    ("Invalid catalogs are rejected", InvalidCatalog),
    ("Catalog refresh adds new utilities and keeps removed enabled ones", CatalogRefresh),
    ("New utilities are preselected; older settings keep applied ones known", KnownUtilities),
    ("Checking reports updates and new utilities without downloading", CheckWithoutDownload),
    ("Offline catalog refresh keeps the cached list", OfflineCatalog),
    ("Background startup leaves never-offered utilities alone", BackgroundIgnoresNew)
};
int failures = 0;
try
{
    foreach (var (name, run) in tests)
    {
        try { await run(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }
    Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed.");
}
finally { try { Directory.Delete(root, true); } catch (IOException) { } }
return failures == 0 ? 0 : 1;

void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Reject(Func<Task> action)
{
    try { await action(); } catch (InvalidDataException) { return; }
    throw new Exception("Expected InvalidDataException.");
}
string NewRoot() => Path.Combine(root, Guid.NewGuid().ToString("N"));
Fixture Make() => new(NewRoot(), Apps);

Task ReleaseSelection()
{
    var app = Apps[0];
    var asset = new ReleaseAsset("custom-name.zip", app.RepositoryUrl + "/releases/download/v99/custom-name.zip", 123);
    var manifest = new ReleaseContract(1, true, true, true, [new("x64", asset.Name, app.Executable, new string('a', 64))]);
    var selected = GitHubReleases.SelectPackage(app, new Release("v99", [asset]), manifest, "x64");
    Assert(selected.Version == "v99" && selected.Asset.Name == asset.Name, "Version or asset was hardcoded.");
    return Task.CompletedTask;
}

async Task InvalidRelease()
{
    var app = Apps[0];
    var asset = new ReleaseAsset("portable.zip", app.RepositoryUrl + "/releases/download/v1/portable.zip", 100);
    var valid = new ReleaseContract(1, true, true, true, [new("x64", asset.Name, app.Executable, new string('a', 64))]);
    foreach (var contract in new[] { valid with { Background = false }, valid with { SingleInstance = false }, valid with { SelfContained = false } })
        await Reject(() => Task.FromResult(GitHubReleases.SelectPackage(app, new Release("v1", [asset]), contract, "x64")));
    await Reject(() => Task.FromResult(GitHubReleases.SelectPackage(app, new Release("v1", [asset with { Url = "https://evil.example/file.zip" }]), valid, "x64")));
    await Reject(() => Task.FromResult(GitHubReleases.SelectPackage(app, new Release("v1", [asset]), valid, "arm64")));
}

async Task Selections()
{
    for (int count = 1; count <= Apps.Length; count++)
    {
        using var f = Make();
        await Task.WhenAll(Apps.Take(count).Select(app => f.Manager.SetEnabledAsync(app, true)));
        Assert(f.Processes.Starts == count, "Wrong number of started apps.");
        Assert(f.Source.Downloads == count, "Unselected app downloaded.");
        Assert(Apps.Take(count).All(app => f.Store.ReadPackage(app) is not null), "Install missing.");
    }
}

async Task ExistingSelection()
{
    var store = new StateStore(NewRoot());
    store.SaveCatalog(Apps);
    var preferences = new Preferences();
    foreach (var app in Apps.Take(3)) preferences.Enabled[app.Id] = true;
    store.Save(preferences);
    var source = new FakeSource();
    using var manager = new Manager(store, source, new FakeCatalog(Apps), new FakeProcesses(), new FakeStartup());
    await manager.SynchronizeAllAsync();
    Assert(!manager.IsEnabled(Apps[3]), "The new utility was enabled without a saved selection.");
    Assert(source.Downloads == 3 && Apps.Take(3).All(manager.IsEnabled), "Existing selections changed.");
}

async Task RepeatStartup()
{
    using var f = Make();
    await f.Manager.SetEnabledAsync(Apps[0], true);
    await f.Manager.SynchronizeAllAsync();
    Assert(f.Source.Resolutions == 2 && f.Source.Downloads == 1, "Did not check latest release, or downloaded unchanged release again.");
    Assert(f.Processes.Starts == 1, "Duplicate process was started.");
}

async Task Upgrade()
{
    using var f = Make(); var app = Apps[0];
    await f.Manager.SetEnabledAsync(app, true);
    var old = f.Store.ReadPackage(app)!;
    f.Source.Version = "v2";
    await f.Manager.SynchronizeAllAsync();
    Assert(f.Store.ReadPackage(app)!.Version == "v2", "Upgrade did not activate.");
    Assert(File.Exists(old.ExecutablePath), "Rollback package was removed.");
    Assert(f.Processes.Starts == 2 && f.Processes.Running.Count == 1, "Upgrade duplicated process.");
}

async Task Offline()
{
    using var f = Make(); var app = Apps[0];
    await f.Manager.SetEnabledAsync(app, true);
    f.Processes.Running.Clear(); f.Source.Offline = true;
    await f.Manager.SynchronizeAllAsync();
    Assert(f.Processes.Running.Count == 1 && f.Manager.GetStatus(app).IsError, "Offline fallback failed.");
}

async Task FailedDownload()
{
    using var f = Make(); var app = Apps[0];
    await f.Manager.SetEnabledAsync(app, true);
    f.Source.Version = "v2"; f.Source.BrokenDownload = true;
    await f.Manager.SynchronizeAllAsync();
    Assert(f.Store.ReadPackage(app)!.Version == "v1" && f.Processes.Running.Count == 1, "Failed download replaced working installation.");
}

async Task Rollback()
{
    using var f = Make(); var app = Apps[0];
    await f.Manager.SetEnabledAsync(app, true);
    string previous = f.Store.ReadPackage(app)!.ExecutablePath;
    f.Source.Version = "v2"; f.Processes.FailNextStart = true;
    await f.Manager.SynchronizeAllAsync();
    Assert(f.Store.ReadPackage(app)!.Version == "v1" && f.Processes.Running.Contains(previous), "Rollback failed.");
}

async Task CancelDownload()
{
    using var f = Make(); var app = Apps[0];
    f.Source.Block = true;
    Task installation = f.Manager.SetEnabledAsync(app, true);
    await f.Source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await f.Manager.SetEnabledAsync(app, false);
    f.Source.Continue.TrySetResult();
    await installation;
    Assert(!f.Manager.IsEnabled(app) && f.Processes.Starts == 0 && f.Store.ReadPackage(app) is null, "Disabled application was restarted.");
    Assert(!f.Manager.GetStatus(app).Enabled, "Stale progress overwrote disabled status.");
}

async Task RapidToggle()
{
    using var f = Make(); var app = Apps[0];
    f.Source.Block = true;
    Task first = f.Manager.SetEnabledAsync(app, true);
    await f.Source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await f.Manager.SetEnabledAsync(app, false);
    Task second = f.Manager.SetEnabledAsync(app, true);
    f.Source.Continue.TrySetResult();
    await Task.WhenAll(first, second);
    Assert(f.Manager.IsEnabled(app) && f.Processes.Starts == 1, "Obsolete operation started or current operation was canceled.");
}

async Task Disable()
{
    using var f = Make(); var app = Apps[1];
    await f.Manager.SetEnabledAsync(app, true);
    await f.Manager.SetEnabledAsync(app, false);
    Assert(!f.Store.Load().IsEnabled(app) && f.Processes.Running.Count == 0 && f.Startup.Removed.Contains(app.Id), "Disable did not persist/stop/unregister.");
    int downloads = f.Source.Downloads;
    await f.Manager.SynchronizeAllAsync();
    Assert(f.Source.Downloads == downloads && f.Processes.Running.Count == 0, "Disabled application returned at startup.");
}

async Task UnsafeArchives()
{
    foreach (string name in new[] { "../escape.exe", "nested/App.exe", "App.exe" })
    {
        string dir = NewRoot(); Directory.CreateDirectory(dir); string archive = Path.Combine(dir, "package.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var output = zip.CreateEntry(name).Open(); output.Write(new byte[] { 77, 90, 0, 0 });
        }
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
        await Reject(() => PackageExtractor.ExtractAsync(archive, Path.Combine(dir, "out"), "App.exe", name == "App.exe" ? new string('0', 64) : hash, CancellationToken.None));
    }
}

async Task ScopedProcesses()
{
    string managed = NewRoot(), unrelated = NewRoot();
    Directory.CreateDirectory(managed); Directory.CreateDirectory(unrelated);
    foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
    {
        File.Copy(file, Path.Combine(managed, Path.GetFileName(file)));
        File.Copy(file, Path.Combine(unrelated, Path.GetFileName(file)));
    }
    string name = "WinKit.Tests.exe";
    var info = new ProcessStartInfo(Path.Combine(managed, name), "--background") { UseShellExecute = false };
    using var owned = Process.Start(info)!;
    info.FileName = Path.Combine(unrelated, name);
    using var other = Process.Start(info)!;
    try
    {
        await Task.Delay(500);
        var spec = new AppSpec("probe", "Probe", "", "https://github.com/example/probe", name);
        var processes = new ProcessControl();
        processes.Stop(spec, managed);
        Assert(owned.HasExited && !other.HasExited, "Process stop affected the unrelated path.");
    }
    finally
    {
        foreach (var process in new[] { owned, other }) if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
    }
}

Task InvalidStoredPath()
{
    using var f = Make(); var app = Apps[0];
    string outside = Path.Combine(root, "outside.exe"); File.WriteAllText(outside, "MZ");
    f.Store.WritePackage(app, new("v1", "x", new string('a', 64), outside));
    Assert(f.Store.ReadPackage(app) is null, "Path outside application root was accepted.");
    return Task.CompletedTask;
}

async Task InvalidCatalog()
{
    string Entry(string id = "one", string name = "One", string repository = "https://github.com/example/one", string executable = "One.exe") =>
        $$"""{"id":"{{id}}","name":"{{name}}","description":"d","repository":"{{repository}}","executable":"{{executable}}"}""";
    string Document(params string[] entries) => $$"""{"schema":1,"utilities":[{{string.Join(",", entries)}}]}""";
    Assert(Catalog.Parse(Document(Entry(), Entry("two", "Two", "https://github.com/example/two", "Two.exe"))).Count == 2, "A valid catalog was rejected.");
    string[] invalid =
    [
        Document(Entry()).Replace("\"schema\":1", "\"schema\":2"),
        Document(Entry(id: "Bad Id")),
        Document(Entry(name: "")),
        Document(Entry(repository: "https://evil.example/example/one")),
        Document(Entry(repository: "http://github.com/example/one")),
        Document(Entry(repository: "https://github.com/example/one/releases")),
        Document(Entry(executable: "..\\\\One.exe")),
        Document(Entry(executable: "One.dll")),
        Document(Entry(), Entry(executable: "Other.exe")),
        Document(Entry(), Entry(id: "two"))
    ];
    foreach (string json in invalid) await Reject(() => Task.FromResult(Catalog.Parse(json)));
}

async Task CatalogRefresh()
{
    var store = new StateStore(NewRoot());
    store.SaveCatalog(Apps[..3]);
    store.Save(new Preferences { Enabled = { ["one"] = true, ["two"] = false, ["three"] = false }, Known = ["one", "two", "three"] });
    var catalog = new FakeCatalog([Apps[1], Apps[3]]);
    using var manager = new Manager(store, new FakeSource(), catalog, new FakeProcesses(), new FakeStartup());
    int notifications = 0;
    manager.CatalogChanged += () => notifications++;
    Assert(await manager.RefreshCatalogAsync() && notifications == 1, "The catalog change was not reported.");
    Assert(string.Join(",", manager.Apps.Select(app => app.Id)) == "two,four,one", "Removed enabled utility was dropped, or a removed disabled one kept.");
    Assert(manager.IsNew(Apps[3]) && !manager.IsNew(Apps[1]), "New utility detection failed.");
    Assert(!await manager.RefreshCatalogAsync() && notifications == 1, "An unchanged catalog was reported as changed.");
    using var reloaded = new Manager(store, new FakeSource(), new FakeCatalog([]), new FakeProcesses(), new FakeStartup());
    Assert(reloaded.Apps.SequenceEqual(manager.Apps), "The refreshed catalog was not cached.");
}

async Task KnownUtilities()
{
    var fresh = new StateStore(NewRoot());
    fresh.SaveCatalog(Apps);
    using (var first = new Manager(fresh, new FakeSource(), new FakeCatalog(Apps), new FakeProcesses(), new FakeStartup()))
        Assert(first.IsFirstRun && Apps.All(first.IsNew) && first.GetStatus(Apps[0]).Message == "Ready to install", "A first run must offer every utility.");

    // Settings written before the dynamic catalog have Enabled entries for every applied utility.
    var store = new StateStore(NewRoot());
    store.SaveCatalog(Apps[..3]);
    store.Save(new Preferences { Enabled = { ["one"] = true, ["two"] = false } });
    using var manager = new Manager(store, new FakeSource(), new FakeCatalog(Apps[..3]), new FakeProcesses(), new FakeStartup());
    Assert(!manager.IsFirstRun && !manager.IsNew(Apps[0]) && !manager.IsNew(Apps[1]) && manager.IsNew(Apps[2]), "Migration of applied utilities failed.");
    Assert(manager.GetStatus(Apps[2]).Message.StartsWith("New"), "A new utility is not labelled as new.");
    await manager.SetEnabledAsync(Apps[2], false);
    using var reloaded = new Manager(store, new FakeSource(), new FakeCatalog(Apps[..3]), new FakeProcesses(), new FakeStartup());
    Assert(!reloaded.IsNew(Apps[2]), "A utility the user decided on is still new.");
}

async Task CheckWithoutDownload()
{
    using var f = Make(); var app = Apps[0];
    await f.Manager.SetEnabledAsync(app, true);
    f.Source.Version = "v2";
    await f.Manager.CheckAllAsync();
    var status = f.Manager.GetStatus(app);
    Assert(status.Message == "Update to v2 available" && status.Version == "v1", $"Update not reported: {status.Message}");
    Assert(f.Manager.GetStatus(Apps[1]).Message == "v2 ready to install", "A not-installed utility does not show its latest version.");
    Assert(f.Source.Downloads == 1 && f.Store.ReadPackage(app)!.Version == "v1", "Checking downloaded or installed an update.");
    await f.Manager.SetEnabledAsync(app, true);
    Assert(f.Store.ReadPackage(app)!.Version == "v2", "Applying did not install the update.");
}

async Task OfflineCatalog()
{
    using var f = Make();
    f.Catalog.Offline = true;
    try { await f.Manager.CheckAllAsync(); throw new Exception("An unavailable catalog was not reported."); }
    catch (InvalidOperationException) { }
    Assert(f.Manager.Apps.Count == Apps.Length && f.Source.Resolutions == Apps.Length, "The cached utilities were not checked offline.");

    var empty = new StateStore(NewRoot());
    using var manager = new Manager(empty, new FakeSource(), new FakeCatalog(Apps) { Offline = true }, new FakeProcesses(), new FakeStartup());
    try { await manager.CheckAllAsync(); throw new Exception("A missing catalog was not reported."); }
    catch (InvalidOperationException) { }
    Assert(manager.Apps.Count == 0, "Utilities appeared without a catalog.");
}

async Task BackgroundIgnoresNew()
{
    var store = new StateStore(NewRoot());
    store.SaveCatalog(Apps[..2]);
    store.Save(new Preferences { Enabled = { ["one"] = false }, Known = ["one"] });
    var startup = new FakeStartup();
    using var manager = new Manager(store, new FakeSource(), new FakeCatalog(Apps[..2]), new FakeProcesses(), startup);
    await manager.SynchronizeAllAsync();
    Assert(startup.Removed.Contains("one") && !startup.Removed.Contains("two"), "A never-offered utility's own startup entry was touched.");
}

sealed class Fixture : IDisposable
{
    public StateStore Store { get; }
    public FakeSource Source { get; } = new();
    public FakeCatalog Catalog { get; }
    public FakeProcesses Processes { get; } = new();
    public FakeStartup Startup { get; } = new();
    public Manager Manager { get; }
    public Fixture(string root, AppSpec[] apps)
    {
        Store = new(root);
        Store.SaveCatalog(apps);
        Catalog = new(apps);
        Manager = new(Store, Source, Catalog, Processes, Startup);
    }
    public void Dispose() => Manager.Dispose();
}

sealed class FakeCatalog(IEnumerable<AppSpec> apps) : ICatalogSource
{
    public AppSpec[] Apps { get; set; } = apps.ToArray();
    public bool Offline { get; set; }
    public Task<string> FetchCatalogAsync(CancellationToken token) =>
        Offline ? throw new HttpRequestException("Network unavailable") : Task.FromResult(WinKit.Catalog.Serialize(Apps));
}

sealed class FakeSource : IReleaseSource
{
    public int Resolutions, Downloads;
    public string Version = "v1";
    public bool Offline, BrokenDownload, Block;
    public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, byte[]> _bytes = new();
    public Task<ManagedRelease> ResolveAsync(AppSpec app, CancellationToken token)
    {
        Interlocked.Increment(ref Resolutions);
        if (Offline) throw new IOException("Network unavailable");
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using var file = zip.CreateEntry(app.Executable).Open();
            file.Write(new byte[] { 77, 90, (byte)Version[^1], 0 });
        }
        var bytes = memory.ToArray();
        string url = app.RepositoryUrl + "/releases/download/" + Version + "/portable.zip";
        lock (_bytes) _bytes[url] = bytes;
        return Task.FromResult(new ManagedRelease(Version, new("portable.zip", url, bytes.Length), Convert.ToHexString(SHA256.HashData(bytes))));
    }
    public async Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<int> progress, CancellationToken token)
    {
        Interlocked.Increment(ref Downloads);
        if (Block) { Started.TrySetResult(); await Continue.Task; }
        // Deliberately ignore cancellation here to exercise the manager's commit guard.
        if (BrokenDownload) throw new IOException("Interrupted download");
        byte[] bytes; lock (_bytes) bytes = _bytes[asset.Url];
        await File.WriteAllBytesAsync(destination, bytes);
        progress.Report(100);
    }
}

sealed class FakeProcesses : IProcessControl
{
    public HashSet<string> Running { get; } = new();
    public int Starts;
    public bool FailNextStart;
    public bool IsRunning(AppSpec app, string directory) { lock (Running) return Running.Any(path => StateStore.IsInside(path, directory)); }
    public void Start(AppSpec app, string executable)
    {
        lock (Running)
        {
            if (FailNextStart) { FailNextStart = false; throw new IOException("New version failed to start"); }
            if (Running.Add(executable)) Starts++;
        }
    }
    public void Stop(AppSpec app, string directory) { lock (Running) Running.RemoveWhere(path => StateStore.IsInside(path, directory)); }
}

sealed class FakeStartup : IStartupControl
{
    public HashSet<string> Removed { get; } = new();
    public void SetManagerEnabled(bool enabled) { }
    public void RemoveUtility(AppSpec app) { lock (Removed) Removed.Add(app.Id); }
}

sealed class SilentProgress : IProgress<int> { public void Report(int value) { } }
