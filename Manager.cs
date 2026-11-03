namespace WinKit;

internal sealed class Manager : IDisposable
{
    private readonly StateStore _store;
    private readonly IReleaseSource _releases;
    private readonly ICatalogSource _catalog;
    private readonly IProcessControl _processes;
    private readonly IStartupControl _startup;
    private readonly object _settingsLock = new();
    private readonly Preferences _preferences;
    private readonly object _slotsLock = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _appsLock = new();
    private readonly SemaphoreSlim _catalogRefresh = new(1, 1);
    private readonly bool _hadSettings;
    private IReadOnlyList<AppSpec> _apps;
    private volatile bool _disposed;
    public event Action<AppSpec, AppStatus>? StatusChanged;
    /// <summary>Raised (on a worker thread) when the list of utilities changes.</summary>
    public event Action? CatalogChanged;
    public StateStore Store => _store;
    /// <summary>The utilities from the last loaded catalog, in catalog order.</summary>
    public IReadOnlyList<AppSpec> Apps { get { lock (_appsLock) return _apps; } }
    /// <summary>True when WinKit had no settings at startup: every utility is offered for the first time.</summary>
    public bool IsFirstRun => !_hadSettings;
    public bool HasSelection => Apps.Any(IsEnabled);
    public bool StartWithWindows { get { lock (_settingsLock) return _preferences.StartWithWindows; } }
    public bool IsEnabled(AppSpec app) { lock (_settingsLock) return _preferences.IsEnabled(app); }
    /// <summary>A utility the user has not been offered yet; it is preselected in the window.</summary>
    public bool IsNew(AppSpec app) { lock (_settingsLock) return !_preferences.Known!.Contains(app.Id, StringComparer.OrdinalIgnoreCase); }

    public Manager(StateStore store, IReleaseSource releases, ICatalogSource catalog, IProcessControl processes, IStartupControl startup)
    {
        _store = store; _releases = releases; _catalog = catalog; _processes = processes; _startup = startup;
        _hadSettings = store.Exists;
        _preferences = store.Load();
        if (_preferences.Known is null)
        {
            // Settings from versions with a built-in list: every utility the user applied was offered.
            _preferences.Known = _preferences.Enabled.Keys.ToList();
            if (_hadSettings) store.Save(_preferences);
        }
        _apps = store.LoadCatalog();
    }

    public AppStatus GetStatus(AppSpec app) { var slot = GetSlot(app); lock (slot.Gate) return slot.Status; }

    private Slot GetSlot(AppSpec app)
    {
        lock (_slotsLock)
        {
            if (_slots.TryGetValue(app.Id, out var slot)) return slot;
            slot = new Slot { Status = InitialStatus(app) };
            _slots.Add(app.Id, slot);
            return slot;
        }
    }

    private AppStatus InitialStatus(AppSpec app) =>
        IsEnabled(app) ? new AppStatus(true, "Ready to check for updates")
        : !IsNew(app) ? new AppStatus(false, "Disabled")
        : new AppStatus(false, IsFirstRun ? "Ready to install" : "New · ready to install");

    /// <summary>
    /// Downloads the catalog and replaces the utility list. Utilities removed from the catalog stay
    /// while enabled. Returns whether the list changed; throws when the catalog is unavailable.
    /// </summary>
    public async Task<bool> RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        await _catalogRefresh.WaitAsync(cancellationToken);
        try
        {
            var remote = Catalog.Parse(await _catalog.FetchCatalogAsync(cancellationToken));
            IReadOnlyList<AppSpec> merged;
            lock (_appsLock)
            {
                merged = Catalog.Merge(remote, _apps, IsEnabled);
                if (merged.SequenceEqual(_apps)) return false;
                _apps = merged;
            }
            _store.SaveCatalog(merged);
            CatalogChanged?.Invoke();
            return true;
        }
        finally { _catalogRefresh.Release(); }
    }

    /// <summary>
    /// Refreshes the catalog and checks every utility's latest release without downloading it, so
    /// the window can show new utilities and available updates. Applying the selection installs them.
    /// </summary>
    public async Task CheckAllAsync(CancellationToken cancellationToken = default)
    {
        Exception? catalogError = null;
        try { await RefreshCatalogAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { catalogError = ex; Log.Error("Catalog", ex); }
        var apps = Apps;
        if (apps.Count == 0 && catalogError is not null)
            throw new InvalidOperationException("The list of utilities could not be downloaded. " + catalogError.Message, catalogError);
        await Task.WhenAll(apps.Select(app => CheckAsync(app, cancellationToken)));
        if (catalogError is not null)
            throw new InvalidOperationException("The list of utilities could not be refreshed. " + catalogError.Message, catalogError);
    }

    private async Task CheckAsync(AppSpec app, CancellationToken cancellationToken)
    {
        var slot = GetSlot(app);
        // An install or update in progress reports its own status.
        if (!await slot.UpdateLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            var installed = _store.ReadPackage(app);
            bool enabled = IsEnabled(app);
            ManagedRelease release;
            try { release = await _releases.ResolveAsync(app, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(app.Name, ex);
                Publish(app, new AppStatus(enabled, "Could not check for updates · " + ex.Message, true, installed?.Version));
                return;
            }
            bool current = installed is not null && installed.Version == release.Version &&
                           installed.Sha256.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase);
            AppStatus status = enabled
                ? installed is null ? new AppStatus(true, $"{release.Version} ready to install")
                : current ? new AppStatus(true, _processes.IsRunning(app, _store.AppRoot(app)) ? "Running · up to date" : "Up to date", Version: installed.Version)
                : new AppStatus(true, $"Update to {release.Version} available", Version: installed.Version)
                : !IsNew(app) ? new AppStatus(false, $"Disabled · latest {release.Version}")
                : new AppStatus(false, IsFirstRun ? $"{release.Version} ready to install" : $"New · {release.Version} ready to install");
            Publish(app, status);
        }
        finally { slot.UpdateLock.Release(); }
    }
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
        var slot = GetSlot(app);
        // Cancel before waiting for the slot; a blocked HTTP request must not defer disable.
        lock (slot.Gate)
        {
            slot.Cancellation?.Cancel();
            lock (_settingsLock)
            {
                _preferences.Enabled[app.Id] = enabled;
                // Choosing either way means the utility has been offered; it is no longer new.
                var known = _preferences.Known!;
                if (!known.Contains(app.Id, StringComparer.OrdinalIgnoreCase)) known.Add(app.Id);
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
        try { await RefreshCatalogAsync(); }
        catch (Exception ex) { Log.Error("Catalog", ex); }
        lock (_settingsLock) _startup.SetManagerEnabled(_preferences.StartWithWindows);
        // Utilities never offered to the user are left alone: their own startup entries are not WinKit's.
        await Task.WhenAll(Apps.Where(app => IsEnabled(app) || !IsNew(app))
            .Select(app => IsEnabled(app) ? SynchronizeAsync(app) : DisableAtStartupAsync(app)));
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
        var slot = GetSlot(app);
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
        var slot = GetSlot(app);
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
        var slot = GetSlot(app);
        lock (slot.Gate)
        {
            if (_disposed || status.Enabled != IsEnabled(app)) return;
            slot.Status = status;
            StatusChanged?.Invoke(app, status);
        }
    }
    public void Dispose()
    {
        Slot[] slots;
        lock (_slotsLock) slots = _slots.Values.ToArray();
        _disposed = true;
        foreach (var slot in slots)
        {
            lock (slot.Gate) slot.Cancellation?.Cancel();
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
