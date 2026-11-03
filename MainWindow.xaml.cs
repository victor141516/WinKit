using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace WinKit;

public partial class MainWindow : Window
{
    private const double BaseHeight = 340;
    private const double RowHeight = 105;
    private readonly Manager _manager;
    private bool _refreshing;
    internal ObservableCollection<AppRow> Rows { get; } = new();

    internal MainWindow(Manager manager)
    {
        _manager = manager;
        InitializeComponent();
        MaxHeight = Math.Max(1, SystemParameters.WorkArea.Height - 32);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        DataContext = new { Rows };
        StartupCheck.IsChecked = manager.StartWithWindows;
        SyncRows();
    }

    /// <summary>
    /// Mirrors the manager's utility list. New rows start selected when the utility is enabled or
    /// has never been offered; existing rows keep the user's pending choice.
    /// </summary>
    internal void SyncRows()
    {
        var apps = _manager.Apps;
        foreach (var stale in Rows.Where(row => apps.All(app => app.Id != row.Spec.Id)).ToArray()) Rows.Remove(stale);
        for (int index = 0; index < apps.Count; index++)
        {
            var app = apps[index];
            int existing = Rows.ToList().FindIndex(row => row.Spec.Id == app.Id);
            if (existing < 0)
            {
                Rows.Insert(index, new AppRow(app, _manager.IsEnabled(app) || _manager.IsNew(app), _manager.GetStatus(app)));
                continue;
            }
            if (existing != index) Rows.Move(existing, index);
            Rows[index].Spec = app;
        }
        EmptyText.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Height = Math.Clamp(BaseHeight + Math.Max(1, Rows.Count) * RowHeight, MinHeight, MaxHeight);
    }

    internal void Update(AppSpec app, AppStatus status) => Rows.FirstOrDefault(row => row.Spec.Id == app.Id)?.SetStatus(status);

    internal void RefreshSelection()
    {
        foreach (var row in Rows) row.Selected = _manager.IsEnabled(row.Spec) || _manager.IsNew(row.Spec);
        StartupCheck.IsChecked = _manager.StartWithWindows;
    }

    /// <summary>Downloads the catalog and checks every utility for updates without installing anything.</summary>
    internal async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        CheckStatus.Text = "Checking for updates…";
        try
        {
            await Task.Run(() => _manager.CheckAllAsync());
            CheckStatus.Text = $"Checked at {DateTime.Now:t}";
        }
        catch (Exception ex)
        {
            Log.Error("Check for updates", ex);
            CheckStatus.Text = ex.Message;
        }
        finally
        {
            SyncRows();
            RefreshButton.IsEnabled = true;
            _refreshing = false;
        }
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        bool startup = StartupCheck.IsChecked == true;
        var changes = Rows.Select(row => (row.Spec, row.Selected)).ToArray();
        try
        {
            await Task.Run(async () =>
            {
                _manager.SetStartWithWindows(startup);
                // Enabled utilities are synchronized too, which installs any available update.
                await Task.WhenAll(changes.Select(change => _manager.SetEnabledAsync(change.Spec, change.Selected)));
            });
        }
        catch (Exception ex)
        {
            Log.Error("Apply selection", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

internal sealed class AppRow(AppSpec spec, bool selected, AppStatus status) : INotifyPropertyChanged
{
    private AppSpec _spec = spec;
    private bool _selected = selected;
    private string _detail = Format(status);
    public AppSpec Spec
    {
        get => _spec;
        set
        {
            if (_spec == value) return;
            _spec = value;
            Changed(nameof(Name));
            Changed(nameof(Description));
        }
    }
    public string Name => Spec.Name;
    public string Description => Spec.Description;
    public bool Selected { get => _selected; set { _selected = value; Changed(); } }
    public string Detail { get => _detail; private set { _detail = value; Changed(); } }
    public void SetStatus(AppStatus status) => Detail = Format(status);
    private static string Format(AppStatus status) => status.Version is null ? status.Message : $"{status.Version} · {status.Message}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
