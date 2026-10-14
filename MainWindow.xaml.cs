using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace WinKit;

public partial class MainWindow : Window
{
    private readonly Manager _manager;
    internal ObservableCollection<AppRow> Rows { get; } = new();
    internal MainWindow(Manager manager, bool firstRun)
    {
        _manager = manager;
        foreach (var app in AppSpec.All) Rows.Add(new AppRow(app, firstRun || manager.IsEnabled(app), firstRun ? new AppStatus(false, "Ready to install") : manager.GetStatus(app)));
        InitializeComponent();
        MaxHeight = Math.Max(1, SystemParameters.WorkArea.Height - 32);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Height = Math.Min(340 + Rows.Count * 105, MaxHeight);
        DataContext = new { Rows };
        StartupCheck.IsChecked = manager.StartWithWindows;
    }
    internal void Update(AppSpec app, AppStatus status)
    {
        var row = Rows.Single(row => row.Spec.Id == app.Id);
        row.Detail = status.Version is null ? status.Message : $"{status.Version} · {status.Message}";
    }
    internal void RefreshSelection()
    {
        foreach (var row in Rows) row.Selected = _manager.IsEnabled(row.Spec);
        StartupCheck.IsChecked = _manager.StartWithWindows;
    }
    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        bool startup = StartupCheck.IsChecked == true;
        var changes = Rows.Select(row => (row.Spec, row.Selected)).ToArray();
        try
        {
            await Task.Run(async () =>
            {
                _manager.SetStartWithWindows(startup);
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
    public AppSpec Spec { get; } = spec;
    public string Name => Spec.Name;
    public string Description => Spec.Description;
    private bool _selected = selected;
    private string _detail = status.Version is null ? status.Message : $"{status.Version} · {status.Message}";
    public bool Selected { get => _selected; set { _selected = value; Changed(); } }
    public string Detail { get => _detail; set { _detail = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
