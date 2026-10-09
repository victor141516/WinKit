namespace WinKit;

internal sealed class Manager : IDisposable
{
    private readonly StateStore _store;
    private readonly IReleaseSource _releases;
    private readonly IProcessControl _processes;
    private readonly IStartupControl _startup;
    private readonly object _settingsLock = new();
    private readonly Preferences _preferences;
    private readonly Dictionary<string, Slot> _slots = AppSpec.All.ToDictionary(app => app.Id, _ => new Slot());
    private bool _disposed;
    public event Action<AppSpec, AppStatus>? StatusChanged;
    public StateStore Store => _store;
    public bool HasSelection => AppSpec.All.Any(IsEnabled);
    public bool StartWithWindows { get { lock (_settingsLock) return _preferences.StartWithWindows; } }
    public bool IsEnabled(AppSpec app) { lock (_settingsLock) return _preferences.IsEnabled(app); }

    public Manager(StateStore store, IReleaseSource releases, IProcessControl processes, IStartupControl startup)
    {
        _store = store; _releases = releases; _processes = processes; _startup = startup;
        _preferences = store.Load();
        foreach (var app in AppSpec.All) _slots[app.Id].Status = new AppStatus(IsEnabled(app), IsEnabled(app) ? "Ready to check for updates" : "Disabled");
    }

    public AppStatus GetStatus(AppSpec app) { lock (_slots[app.Id].Gate) return _slots[app.Id].Status; }
    public void SetStartWithWindows(bool enabled)
    {
        lock (_settingsLock)
        {
            _startup.SetManagerEnabled(enabled);
            _preferences.StartWithWindows = enabled;
            _store.Save(_preferences);
        }
    }

    public async Task SetEnabledAsync(AppSpec app, bool enabled)
    {
        var slot = _slots[app.Id];
        // Cancel before waiting for the slot; a blocked HTTP request must not defer disable.
        lock (slot.Gate)
        {
            slot.Cancellation?.Cancel();
            lock (_settingsLock)
            {
                _preferences.Enabled[app.Id] = enabled;
                _store.Save(_preferences);
                _startup.SetManagerEnabled(_preferences.StartWithWindows);
            }
            try
            {
                _startup.RemoveUtility(app);
                if (!enabled)
                {
                    _processes.Stop(app, _store.AppRoot(app));
                    Publish(app, new AppStatus(false, "Disabled"));
                }
            }
            catch (Exception ex) { Publish(app, new AppStatus(enabled, ex.Message, true)); throw; }
        }
        if (enabled) await SynchronizeAsync(app);
    }

    public async Task SynchronizeAllAsync()
    {
        lock (_settingsLock) _startup.SetManagerEnabled(_preferences.StartWithWindows);
        await Task.WhenAll(AppSpec.All.Select(app => IsEnabled(app) ? SynchronizeAsync(app) : DisableAtStartupAsync(app)));
    }

    private Task DisableAtStartupAsync(AppSpec app)
    {
        try
        {
            _startup.RemoveUtility(app);
            _processes.Stop(app, _store.AppRoot(app));
        }
        catch (Exception ex) { Publish(app, new AppStatus(false, ex.Message, true)); }
        return Task.CompletedTask;
    }

    public async Task SynchronizeAsync(AppSpec app)
    {
        var slot = _slots[app.Id];
        CancellationTokenSource cancellation;
        lock (slot.Gate)
        {
            if (_disposed || !IsEnabled(app)) return;
            slot.Cancellation?.Cancel();
            cancellation = new CancellationTokenSource();
            slot.Cancellation = cancellation;
        }
        var token = cancellation.Token;
        await slot.UpdateLock.WaitAsync();
        string? stage = null;
        InstalledPackage? previous = null;
        try
        {
            token.ThrowIfCancellationRequested();
            previous = _store.ReadPackage(app);
            _startup.RemoveUtility(app);
            Publish(app, new AppStatus(true, "Checking for updates", Version: previous?.Version));
            ManagedRelease release = await _releases.ResolveAsync(app, token);
            if (previous is not null && previous.Version == release.Version && previous.Sha256.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                StartIfEnabled(app, previous, cancellation, "Running · up to date");
                return;
            }
            stage = Path.Combine(_store.AppRoot(app), ".staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            string archive = Path.Combine(stage, "package.zip");
            var progress = new DirectProgress(value =>
            {
                if (!token.IsCancellationRequested) Publish(app, new AppStatus(true, $"Downloading · {value}%", Version: previous?.Version));
            });
            await _releases.DownloadAsync(release.Asset, archive, progress, token);
            Publish(app, new AppStatus(true, "Verifying download", Version: previous?.Version));
            string extracted = Path.Combine(stage, "files");
            await PackageExtractor.ExtractAsync(archive, extracted, app.Executable, release.Sha256, token);
            lock (slot.Gate)
            {
                token.ThrowIfCancellationRequested();
                if (!IsEnabled(app) || slot.Cancellation != cancellation) return;
                _processes.Stop(app, _store.AppRoot(app));
                string versionDirectory = Path.Combine(_store.AppRoot(app), "versions", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(versionDirectory)!);
                Directory.Move(extracted, versionDirectory);
                var installed = new InstalledPackage(release.Version, release.Asset.Name, release.Sha256, Path.Combine(versionDirectory, app.Executable));
                try
                {
                    _processes.Start(app, installed.ExecutablePath);
                    _store.WritePackage(app, installed);
                }
                catch
                {
                    _processes.Stop(app, _store.AppRoot(app));
                    if (previous is not null) _processes.Start(app, previous.ExecutablePath);
                    throw;
                }
                Publish(app, new AppStatus(true, "Running · up to date", Version: release.Version));
                CleanupOldVersions(app, installed, previous);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error(app.Name, ex);
            lock (slot.Gate)
            {
                if (IsEnabled(app) && slot.Cancellation == cancellation && !_disposed)
                {
                    string message = ex.Message;
                    if (previous is not null)
                    {
                        try { _processes.Start(app, previous.ExecutablePath); message = "Running installed version · " + message; }
                        catch (Exception startError) { message += " " + startError.Message; }
                    }
                    Publish(app, new AppStatus(true, message, true, previous?.Version));
                }
            }
        }
        finally
        {
            if (stage is not null)
            {
                try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            lock (slot.Gate) if (slot.Cancellation == cancellation) slot.Cancellation = null;
            cancellation.Dispose();
            slot.UpdateLock.Release();
        }
    }

    private void StartIfEnabled(AppSpec app, InstalledPackage package, CancellationTokenSource cancellation, string message)
    {
        var slot = _slots[app.Id];
        lock (slot.Gate)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || !IsEnabled(app) || slot.Cancellation != cancellation) return;
            _processes.Start(app, package.ExecutablePath);
            Publish(app, new AppStatus(true, message, Version: package.Version));
        }
    }

    private void CleanupOldVersions(AppSpec app, InstalledPackage current, InstalledPackage? previous)
    {
        string versions = Path.Combine(_store.AppRoot(app), "versions");
        foreach (string directory in Directory.GetDirectories(versions))
        {
            if (StateStore.IsInside(current.ExecutablePath, directory) || previous is not null && StateStore.IsInside(previous.ExecutablePath, directory)) continue;
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Publish(AppSpec app, AppStatus status)
    {
        lock (_slots[app.Id].Gate)
        {
            if (_disposed || status.Enabled != IsEnabled(app)) return;
            _slots[app.Id].Status = status;
            StatusChanged?.Invoke(app, status);
        }
    }
    public void Dispose()
    {
        foreach (var slot in _slots.Values)
        {
            lock (slot.Gate) { _disposed = true; slot.Cancellation?.Cancel(); }
        }
    }
    private sealed class Slot
    {
        public object Gate { get; } = new();
        public SemaphoreSlim UpdateLock { get; } = new(1, 1);
        public CancellationTokenSource? Cancellation;
        public AppStatus Status = new(false, "Disabled");
    }
    private sealed class DirectProgress(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }
}

internal static class Log
{
    internal static string Root { get; set; } = Bootstrap.Root;
    public static void Error(string operation, Exception exception)
    {
        try { Directory.CreateDirectory(Root); File.AppendAllText(Path.Combine(Root, "winkit.log"), $"{DateTimeOffset.UtcNow:O} {operation}: {exception}\n"); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
