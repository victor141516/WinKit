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
            _manager = new Manager(store, _releases, new ProcessControl(), new StartupControl(Environment.ProcessPath!));
            _show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WinKit.Manager.Show");
            _showRegistration = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettings)), null, Timeout.Infinite, false);
            CreateTray();
            _manager.StatusChanged += (app, status) => Dispatcher.BeginInvoke(() =>
            {
                _window?.Update(app, status);
                RefreshTray();
            });
            if (!background) ShowSettings(firstRun);
            if (!firstRun) _ = Task.Run(_manager.SynchronizeAllAsync);
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
        RefreshTray();
    }

    private void RefreshTray()
    {
        if (_tray is null || _manager is null || _exiting) return;
        var menu = new Forms.ContextMenuStrip { Font = new Font("Segoe UI", 9) };
        menu.Items.Add("Open WinKit", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var app in AppSpec.All)
        {
            var status = _manager.GetStatus(app);
            var item = new Forms.ToolStripMenuItem(app.Name)
            {
                Checked = _manager.IsEnabled(app),
                ToolTipText = status.Message,
                CheckOnClick = false
            };
            item.Click += async (_, _) =>
            {
                bool enabled = !_manager.IsEnabled(app);
                try { await Task.Run(() => _manager.SetEnabledAsync(app, enabled)); }
                catch (Exception ex) { Log.Error("Toggle " + app.Name, ex); System.Windows.MessageBox.Show(ex.Message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Error); }
                _window?.RefreshSelection();
                RefreshTray();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Check for updates", null, async (_, _) => await Task.Run(_manager.SynchronizeAllAsync));
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = _manager.StartWithWindows };
        startup.Click += (_, _) =>
        {
            try { _manager.SetStartWithWindows(!_manager.StartWithWindows); _window?.RefreshSelection(); RefreshTray(); }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        menu.Items.Add(startup);
        menu.Items.Add("Exit WinKit", null, (_, _) => ExitManager());
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        if (old is not null) Dispatcher.BeginInvoke(old.Dispose);
    }

    private void ShowSettings() => ShowSettings(false);
    private void ShowSettings(bool firstRun)
    {
        if (_manager is null) return;
        if (_window is null)
        {
            _window = new MainWindow(_manager, firstRun);
            _window.Closing += (_, args) => { if (!_exiting) { args.Cancel = true; _window.Hide(); } };
        }
        else if (!firstRun) _window.RefreshSelection();
        if (!firstRun) foreach (var app in AppSpec.All) _window.Update(app, _manager.GetStatus(app));
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitManager() { _exiting = true; _manager?.Dispose(); _window?.Close(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _manager?.Dispose();
        _showRegistration?.Unregister(null);
        _show?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
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
            using var manager = new Manager(new StateStore(root), new GitHubReleases(), new PreviewProcesses(), new PreviewStartup());
            var window = new MainWindow(manager, true);
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Left = -10000;
            window.Top = -10000;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Show();
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
