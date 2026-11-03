using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace WinKit;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private EventWaitHandle? _show;
    private RegisteredWaitHandle? _showRegistration;
    private Manager? _manager;
    private GitHubReleases? _releases;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;
    private Icon? _icon;
    private readonly Dictionary<string, Forms.ToolStripMenuItem> _utilityItems = new();
    private Forms.ToolStripMenuItem? _startupItem;
    private Forms.ToolStripSeparator? _utilitiesEnd;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--preview")
        {
            _ = CreatePreviewAsync(e.Args);
            return;
        }
        try
        {
            bool background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            // Detect existing manager before trying to overwrite its installed executable.
            _instance = new Mutex(true, @"Local\WinKit.Manager.Instance", out bool created);
            if (!created)
            {
                if (!background)
                {
                    try { using var show = EventWaitHandle.OpenExisting(@"Local\WinKit.Manager.Show"); show.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                }
                Shutdown();
                return;
            }
            if (Bootstrap.InstallAndRelaunch(e.Args, () =>
            {
                _instance.ReleaseMutex();
                _instance.Dispose();
                _instance = null;
            }))
            {
                Shutdown();
                return;
            }
            var store = new StateStore(Bootstrap.Root);
            bool firstRun = !store.Exists;
            _releases = new GitHubReleases();
            _manager = new Manager(store, _releases, _releases, new ProcessControl(), new StartupControl(Environment.ProcessPath!));
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WinKit.Manager.Show");
            _showRegistration = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettings)), null, Timeout.Infinite, false);
            CreateTray();
            _manager.StatusChanged += (app, status) => Dispatcher.BeginInvoke(() =>
            {
                _window?.Update(app, status);
                RefreshTray();
            });
            _manager.CatalogChanged += () => Dispatcher.BeginInvoke(() =>
            {
                RebuildUtilityItems();
                _window?.SyncRows();
            });
            if (!background) ShowSettings();
            if (!firstRun) _ = RunManagerActionAsync(_manager.SynchronizeAllAsync, showErrors: false);
        }
        catch (Exception ex)
        {
            Log.Error("Startup", ex);
            System.Windows.MessageBox.Show(ex.Message, "WinKit could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void CreateTray()
    {
        _icon = CreateIcon();
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "WinKit", Visible = true };
        _tray.DoubleClick += (_, _) => ShowSettings();
        if (_manager is null) return;
        var menu = new Forms.ContextMenuStrip { Font = new Font("Segoe UI", 9) };
        menu.Opening += (_, _) => RefreshTray();
        menu.Items.Add("Open WinKit", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _utilitiesEnd = new Forms.ToolStripSeparator();
        menu.Items.Add(_utilitiesEnd);
        menu.Items.Add("Check for updates", null, async (_, _) => await RunManagerActionAsync(_manager.SynchronizeAllAsync));
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = _manager.StartWithWindows };
        _startupItem = startup;
        startup.Click += (_, _) =>
        {
            try { _manager.SetStartWithWindows(!_manager.StartWithWindows); _window?.RefreshSelection(); RefreshTray(); }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        menu.Items.Add(startup);
        menu.Items.Add("Exit WinKit", null, (_, _) => ExitManager());
        _tray.ContextMenuStrip = menu;
        RebuildUtilityItems();
    }

    /// <summary>Recreates one tray toggle per catalog utility, above the utilities separator.</summary>
    private void RebuildUtilityItems()
    {
        if (_manager is null || _tray?.ContextMenuStrip is not { } menu || _utilitiesEnd is null || _exiting) return;
        foreach (var old in _utilityItems.Values) { menu.Items.Remove(old); old.Dispose(); }
        _utilityItems.Clear();
        int index = menu.Items.IndexOf(_utilitiesEnd);
        foreach (var app in _manager.Apps)
        {
            var item = new Forms.ToolStripMenuItem(app.Name) { CheckOnClick = false };
            item.Click += async (_, _) =>
            {
                bool enabled = !_manager.IsEnabled(app);
                await RunManagerActionAsync(() => _manager.SetEnabledAsync(app, enabled));
                _window?.RefreshSelection();
                RefreshTray();
            };
            _utilityItems[app.Id] = item;
            menu.Items.Insert(index++, item);
        }
        RefreshTray();
    }

    private void RefreshTray()
    {
        if (_manager is null || _exiting) return;
        foreach (var app in _manager.Apps)
        {
            if (!_utilityItems.TryGetValue(app.Id, out var item)) continue;
            item.Checked = _manager.IsEnabled(app);
            item.ToolTipText = _manager.GetStatus(app).Message;
        }
        if (_startupItem is not null) _startupItem.Checked = _manager.StartWithWindows;
    }

    private async Task RunManagerActionAsync(Func<Task> action, bool showErrors = true)
    {
        try { await Task.Run(action); }
        catch (Exception ex)
        {
            Log.Error("Manager action", ex);
            if (showErrors && !_exiting) System.Windows.MessageBox.Show(ex.Message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Error);
            else if (!_exiting && _tray is not null) _tray.ShowBalloonTip(5000, "WinKit", ex.Message, Forms.ToolTipIcon.Error);
        }
    }

    /// <summary>Shows the window and checks for new utilities and updates every time it opens.</summary>
    private void ShowSettings()
    {
        if (_manager is null) return;
        if (_window is null)
        {
            _window = new MainWindow(_manager);
            _window.Closing += (_, args) => { if (!_exiting) { args.Cancel = true; _window.Hide(); } };
        }
        else if (!_window.IsVisible)
        {
            _window.SyncRows();
            _window.RefreshSelection();
            foreach (var app in _manager.Apps) _window.Update(app, _manager.GetStatus(app));
        }
        bool opening = !_window.IsVisible;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        if (opening) _ = _window.RefreshAsync();
    }

    private void ExitManager() { _exiting = true; _manager?.Dispose(); _window?.Close(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _manager?.Dispose();
        _showRegistration?.Unregister(null);
        _show?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
        _icon?.Dispose();
        _releases?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        using var brush = new SolidBrush(System.Drawing.Color.FromArgb(35, 105, 220));
        graphics.FillRectangle(brush, 3, 3, 11, 11);
        graphics.FillRectangle(brush, 18, 3, 11, 11);
        graphics.FillRectangle(brush, 3, 18, 11, 11);
        graphics.FillRectangle(brush, 18, 18, 11, 11);
        nint handle = bitmap.GetHicon();
        try { using var original = Icon.FromHandle(handle); return (Icon)original.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);

    private async Task CreatePreviewAsync(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "WinKitPreview-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var releases = new GitHubReleases();
            using var manager = new Manager(new StateStore(root), releases, releases, new PreviewProcesses(), new PreviewStartup());
            var window = new MainWindow(manager);
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Left = -10000;
            window.Top = -10000;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Show();
            // The preview shows what a first launch shows: the downloaded catalog and latest versions.
            await window.RefreshAsync();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(args.Length > 1 ? args[1] : "preview.png");
            encoder.Save(stream);
            window.Close();
            Shutdown();
        }
        catch (Exception ex) { File.WriteAllText(args.Length > 1 ? args[1] + ".error.txt" : "preview.error.txt", ex.ToString()); Shutdown(1); }
    }
    private sealed class PreviewProcesses : IProcessControl
    {
        public bool IsRunning(AppSpec app, string directory) => false;
        public void Start(AppSpec app, string executable) { }
        public void Stop(AppSpec app, string directory) { }
    }
    private sealed class PreviewStartup : IStartupControl
    {
        public void SetManagerEnabled(bool enabled) { }
        public void RemoveUtility(AppSpec app) { }
    }
}
