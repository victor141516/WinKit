using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using WinKit;

if (args.Contains("--background")) { Thread.Sleep(60000); return 0; }
string root = Path.Combine(Path.GetTempPath(), "WinKitTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Log.Root = root;
var tests = new List<(string, Func<Task>)>
{
    ("Latest release and architecture are read from manifest", ReleaseSelection),
    ("Non-portable or unsafe release is rejected", InvalidRelease),
    ("One, two, and three selections install only selected utilities", Selections),
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
    ("Paths outside managed directory are rejected", InvalidStoredPath)
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
Fixture Make() => new(NewRoot());

Task ReleaseSelection()
{
    var app = AppSpec.All[0];
    var asset = new ReleaseAsset("custom-name.zip", app.RepositoryUrl + "/releases/download/v99/custom-name.zip", 123);
    var manifest = new ReleaseContract(1, true, true, true, [new("x64", asset.Name, app.Executable, new string('a', 64))]);
    var selected = GitHubReleases.SelectPackage(app, new Release("v99", [asset]), manifest, "x64");
    Assert(selected.Version == "v99" && selected.Asset.Name == asset.Name, "Version or asset was hardcoded.");
    return Task.CompletedTask;
}

async Task InvalidRelease()
{
    var app = AppSpec.All[0];
    var asset = new ReleaseAsset("portable.zip", app.RepositoryUrl + "/releases/download/v1/portable.zip", 100);
    var valid = new ReleaseContract(1, true, true, true, [new("x64", asset.Name, app.Executable, new string('a', 64))]);
    foreach (var contract in new[] { valid with { Background = false }, valid with { SingleInstance = false }, valid with { SelfContained = false } })
        await Reject(() => Task.FromResult(GitHubReleases.SelectPackage(app, new Release("v1", [asset]), contract, "x64")));
    await Reject(() => Task.FromResult(GitHubReleases.SelectPackage(app, new Release("v1", [asset with { Url = "https://evil.example/file.zip" }]), valid, "x64")));
    await Reject(() => Task.FromResult(GitHubReleases.SelectPackage(app, new Release("v1", [asset]), valid, "arm64")));
}

async Task Selections()
{
    for (int count = 1; count <= 3; count++)
    {
        using var f = Make();
        await Task.WhenAll(AppSpec.All.Take(count).Select(app => f.Manager.SetEnabledAsync(app, true)));
        Assert(f.Processes.Starts == count, "Wrong number of started apps.");
        Assert(f.Source.Downloads == count, "Unselected app downloaded.");
        Assert(AppSpec.All.Take(count).All(app => f.Store.ReadPackage(app) is not null), "Install missing.");
    }
}

async Task RepeatStartup()
{
    using var f = Make();
    await f.Manager.SetEnabledAsync(AppSpec.All[0], true);
    await f.Manager.SynchronizeAllAsync();
    Assert(f.Source.Resolutions == 2 && f.Source.Downloads == 1, "Did not check latest release, or downloaded unchanged release again.");
    Assert(f.Processes.Starts == 1, "Duplicate process was started.");
}

async Task Upgrade()
{
    using var f = Make(); var app = AppSpec.All[0];
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
    using var f = Make(); var app = AppSpec.All[0];
    await f.Manager.SetEnabledAsync(app, true);
    f.Processes.Running.Clear(); f.Source.Offline = true;
    await f.Manager.SynchronizeAllAsync();
    Assert(f.Processes.Running.Count == 1 && f.Manager.GetStatus(app).IsError, "Offline fallback failed.");
}

async Task FailedDownload()
{
    using var f = Make(); var app = AppSpec.All[0];
    await f.Manager.SetEnabledAsync(app, true);
    f.Source.Version = "v2"; f.Source.BrokenDownload = true;
    await f.Manager.SynchronizeAllAsync();
    Assert(f.Store.ReadPackage(app)!.Version == "v1" && f.Processes.Running.Count == 1, "Failed download replaced working installation.");
}

async Task Rollback()
{
    using var f = Make(); var app = AppSpec.All[0];
    await f.Manager.SetEnabledAsync(app, true);
    string previous = f.Store.ReadPackage(app)!.ExecutablePath;
    f.Source.Version = "v2"; f.Processes.FailNextStart = true;
    await f.Manager.SynchronizeAllAsync();
    Assert(f.Store.ReadPackage(app)!.Version == "v1" && f.Processes.Running.Contains(previous), "Rollback failed.");
}

async Task CancelDownload()
{
    using var f = Make(); var app = AppSpec.All[0];
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
    using var f = Make(); var app = AppSpec.All[0];
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
    using var f = Make(); var app = AppSpec.All[1];
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
    using var f = Make(); var app = AppSpec.All[0];
    string outside = Path.Combine(root, "outside.exe"); File.WriteAllText(outside, "MZ");
    f.Store.WritePackage(app, new("v1", "x", new string('a', 64), outside));
    Assert(f.Store.ReadPackage(app) is null, "Path outside application root was accepted.");
    return Task.CompletedTask;
}

sealed class Fixture : IDisposable
{
    public StateStore Store { get; }
    public FakeSource Source { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public FakeStartup Startup { get; } = new();
    public Manager Manager { get; }
    public Fixture(string root) { Store = new(root); Manager = new(Store, Source, Processes, Startup); }
    public void Dispose() => Manager.Dispose();
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
