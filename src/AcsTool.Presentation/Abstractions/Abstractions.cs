using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcsTool.Presentation.Abstractions;

/// <summary>UI-thread marshalling (plan §3.8 rule 10). WPF: Dispatcher.InvokeAsync(Background). Tests: <see cref="SynchronousUiDispatcher"/>.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action);

    void Post(Action action);
}

/// <summary>Runs everything inline on the calling thread (tests, headless composition).</summary>
public sealed class SynchronousUiDispatcher : IUiDispatcher
{
    private readonly object _lock = new();

    public int InvokeCount { get; private set; }

    public bool CheckAccess() => true;

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_lock)
        {
            InvokeCount++;
            action();
        }
        return Task.CompletedTask;
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_lock)
        {
            action();
        }
    }
}

public interface IDialogService
{
    Task ShowErrorAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message);
}

public interface IFileDialogService
{
    string? SaveFile(string title, string filter, string? suggestedName);

    string? OpenFile(string title, string filter);
}

public interface IClipboardService
{
    void SetText(string text);
}

public enum BannerKind { Info, Warning, Error, Success }

public sealed partial class BannerViewModel(string key, BannerKind kind, string text, Action<BannerViewModel> dismiss) : ObservableObject
{
    public string Key { get; } = key;

    public BannerKind Kind { get; } = kind;

    public string Text { get; } = text;

    [RelayCommand]
    private void Dismiss() => dismiss(this);
}

public interface IBannerService
{
    ObservableCollection<BannerViewModel> Banners { get; }

    /// <summary>Shows a banner; an existing banner with the same key is replaced.</summary>
    void Show(BannerKind kind, string text, string? key = null);

    void Dismiss(string key);
}

public sealed class BannerService(IUiDispatcher dispatcher) : IBannerService
{
    public ObservableCollection<BannerViewModel> Banners { get; } = [];

    public void Show(BannerKind kind, string text, string? key = null)
    {
        key ??= text;
        OnUi(() =>
        {
            Remove(key);
            Banners.Add(new BannerViewModel(key, kind, text, b => Banners.Remove(b)));
        });
    }

    public void Dismiss(string key) => OnUi(() => Remove(key));

    private void Remove(string key)
    {
        foreach (var b in Banners.Where(b => b.Key == key).ToList())
        {
            Banners.Remove(b);
        }
    }

    private void OnUi(Action a)
    {
        if (dispatcher.CheckAccess())
        {
            a();
        }
        else
        {
            dispatcher.Post(a);
        }
    }
}
