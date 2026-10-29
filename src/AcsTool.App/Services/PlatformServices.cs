using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AcsTool.Core.Settings;
using AcsTool.Presentation.Abstractions;
using Microsoft.Win32;

namespace AcsTool.App.Services;

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;

    public void Post(Action action) => dispatcher.BeginInvoke(action, DispatcherPriority.Background);
}

public sealed class WpfDialogService : IDialogService
{
    public Task ShowErrorAsync(string title, string message)
    {
        MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message) =>
        Task.FromResult(MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
}

public sealed class WpfFileDialogService : IFileDialogService
{
    public string? SaveFile(string title, string filter, string? suggestedName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = suggestedName ?? "" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? OpenFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}

public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        try
        {
            Clipboard.SetText(text ?? "");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // clipboard busy (another process holds it); ignore like most apps do
        }
    }
}

/// <summary>DPAPI (CurrentUser) secrets: "dpapi:&lt;base64&gt;". Reads legacy "plain:" values too.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AcsTool.v1");

    public string Protect(string plain) =>
        "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain ?? ""), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.StartsWith("dpapi:", StringComparison.Ordinal))
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Protected by another Windows user/machine, or corrupted: surface it instead of silently returning "" (review m8).
                throw new SecretUnprotectException("The stored password could not be decrypted for this Windows user; re-enter it.", ex);
            }
        }
        return stored.StartsWith("plain:", StringComparison.Ordinal) ? stored[6..] : stored;
    }
}

/// <summary>%APPDATA%\AcsTool (settings, profiles), %LOCALAPPDATA%\AcsTool\logs, Documents\AcsTool\exports.</summary>
public sealed class WindowsAppPaths : IAppPaths
{
    public string ConfigDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcsTool");

    public string ProfilesDir => Path.Combine(ConfigDir, "profiles");

    public string LogsDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AcsTool", "logs");

    public string DefaultExportDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AcsTool", "exports");
}
