using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinKit;

internal interface IProcessControl
{
    bool IsRunning(AppSpec app, string directory);
    void Start(AppSpec app, string executable);
    void Stop(AppSpec app, string directory);
}

internal sealed class ProcessControl : IProcessControl
{
    public bool IsRunning(AppSpec app, string directory)
    {
        using var processes = new ProcessCollection(app);
        return processes.Items.Any(process => Matches(process, directory));
    }
    public void Start(AppSpec app, string executable)
    {
        string directory = Path.GetDirectoryName(executable)!;
        using var processes = new ProcessCollection(app);
        foreach (var process in processes.Items)
        {
            if (Matches(process, directory)) return;
            try
            {
                if (process.SessionId == Process.GetCurrentProcess().SessionId)
                    throw new InvalidOperationException($"{app.Name} is already running outside WinKit. Exit that copy and try again.");
            }
            catch (InvalidOperationException) { if (!process.HasExited) throw; }
        }
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--background");
        info.Environment["WINKIT_MANAGED"] = "1";
        using var started = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {app.Name}.");
        if (started.WaitForExit(500)) throw new InvalidOperationException($"{app.Name} exited during startup (code {started.ExitCode}).");
    }
    public void Stop(AppSpec app, string directory)
    {
        using var processes = new ProcessCollection(app);
        foreach (var process in processes.Items)
        {
            if (!Matches(process, directory)) continue;
            try
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000)) throw new IOException($"{app.Name} did not stop.");
            }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
    }
    private static bool Matches(Process process, string directory)
    {
        try { return process.SessionId == Process.GetCurrentProcess().SessionId && process.MainModule?.FileName is string path && StateStore.IsInside(path, directory); }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    private sealed class ProcessCollection(AppSpec app) : IDisposable
    {
        public Process[] Items { get; } = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app.Executable));
        public void Dispose() { foreach (var process in Items) process.Dispose(); }
    }
}

internal interface IStartupControl
{
    void SetManagerEnabled(bool enabled);
    void RemoveUtility(AppSpec app);
}

internal sealed class StartupControl(string managerExecutable) : IStartupControl
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public void SetManagerEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue("WinKit", $"\"{managerExecutable}\" --background");
        else key.DeleteValue("WinKit", false);
    }
    public void RemoveUtility(AppSpec app)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(app.Name, false);
        string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), app.Name + ".lnk");
        if (File.Exists(shortcut)) File.Delete(shortcut);
    }
}

internal static class Bootstrap
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinKit");
    public static bool InstallAndRelaunch(string[] arguments, Action beforeLaunch)
    {
        string current = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
        string installed = Path.Combine(Root, "WinKit.exe");
        if (string.Equals(current, installed, StringComparison.OrdinalIgnoreCase)) return false;
        // Debug and developer launches must not copy an unbundled apphost by itself.
        if (File.Exists(Path.ChangeExtension(current, ".runtimeconfig.json"))) return false;
        Directory.CreateDirectory(Root);
        if (File.Exists(installed))
        {
            string oldHash;
            using (var stream = File.OpenRead(installed)) oldHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            string newHash;
            using (var stream = File.OpenRead(current)) newHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            if (oldHash != newHash) File.Copy(current, installed, true);
        }
        else File.Copy(current, installed);
        var info = new ProcessStartInfo(installed) { UseShellExecute = false, WorkingDirectory = Root };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        beforeLaunch();
        using var launched = Process.Start(info) ?? throw new IOException("Could not open the installed manager.");
        return true;
    }
}
