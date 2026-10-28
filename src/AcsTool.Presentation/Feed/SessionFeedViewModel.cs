using System.Collections.ObjectModel;
using AcsTool.Core.Sessions;
using AcsTool.Presentation.Detail;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcsTool.Presentation.Feed;

public enum OutcomeFilter { All, ErrorsOnly, InProgress, Completed }

/// <summary>
/// Session list for one source (plan §3.8 rule 6): inserts new rows (top when NewestOnTop), updates rows in place,
/// trims to MaxFeedItems from the old end, and supports text/outcome filters and pause.
/// </summary>
public sealed partial class SessionFeedViewModel : ObservableObject, IFeedBatchConsumer
{
    private readonly ISessionStore _store;
    private readonly List<SessionItemViewModel> _all = []; // oldest → newest
    private readonly Dictionary<Guid, SessionItemViewModel> _byId = [];
    private readonly List<FeedBatch> _paused = [];

    [ObservableProperty]
    private SessionItemViewModel? _selectedItem;

    [ObservableProperty]
    private string? _filterText;

    [ObservableProperty]
    private OutcomeFilter _outcomeFilter;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _autoScroll = true;

    public SessionFeedViewModel(ISessionStore store, SessionSource? source, int maxItems, bool newestOnTop, SessionDetailViewModel detail)
    {
        _store = store;
        Source = source;
        MaxItems = Math.Max(1, maxItems);
        NewestOnTop = newestOnTop;
        Detail = detail;
    }

    public SessionSource? Source { get; }

    public static IReadOnlyList<OutcomeFilter> OutcomeFilters { get; } = Enum.GetValues<OutcomeFilter>();

    public int MaxItems { get; set; }

    public bool NewestOnTop { get; set; }

    public SessionDetailViewModel Detail { get; }

    /// <summary>Visible rows (filtered), in display order.</summary>
    public ObservableCollection<SessionItemViewModel> Items { get; } = [];

    public int TotalCount => _all.Count;

    public void Apply(FeedBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (IsPaused)
        {
            _paused.Add(batch);
            return;
        }
        ApplyCore(batch);
    }

    private void ApplyCore(FeedBatch batch)
    {
        if (batch.Cleared)
        {
            ClearLocal();
        }
        if (batch.Resync is { } all)
        {
            Resync(all);
        }
        foreach (var c in batch.Changes)
        {
            if (c.Kind == SessionChangeKind.Removed || c.Snapshot is null)
            {
                RemoveLocal(c.Id);
                continue;
            }
            if (Source is { } src && c.Snapshot.Source != src)
            {
                continue;
            }
            if (_byId.TryGetValue(c.Id, out var item))
            {
                var wasVisible = IsVisible(item);
                item.Update(c.Snapshot);
                if (wasVisible != IsVisible(item))
                {
                    Refilter();
                }
                if (ReferenceEquals(SelectedItem, item))
                {
                    Detail.Refresh(c.Snapshot);
                }
            }
            else
            {
                Add(new SessionItemViewModel(c.Snapshot));
            }
        }
        Trim();
        OnPropertyChanged(nameof(TotalCount));
    }

    private void Resync(IReadOnlyList<SessionSnapshot> all)
    {
        var wanted = all.Where(s => Source is null || s.Source == Source).ToList();
        var keep = wanted.Select(s => s.Id).ToHashSet();
        foreach (var id in _byId.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            RemoveLocal(id);
        }
        foreach (var s in wanted)
        {
            if (_byId.TryGetValue(s.Id, out var item))
            {
                item.Update(s);
            }
            else
            {
                Add(new SessionItemViewModel(s));
            }
        }
        Refilter();
    }

    private void Add(SessionItemViewModel item)
    {
        _all.Add(item);
        _byId[item.Id] = item;
        if (IsVisible(item))
        {
            if (NewestOnTop)
            {
                Items.Insert(0, item);
            }
            else
            {
                Items.Add(item);
            }
        }
    }

    private void Trim()
    {
        while (_all.Count > MaxItems)
        {
            var oldest = _all[0];
            RemoveLocal(oldest.Id);
        }
    }

    private void RemoveLocal(Guid id)
    {
        if (!_byId.Remove(id, out var item))
        {
            return;
        }
        _all.Remove(item);
        Items.Remove(item);
        if (ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = null;
        }
    }

    private void ClearLocal()
    {
        _all.Clear();
        _byId.Clear();
        Items.Clear();
        SelectedItem = null;
    }

    private bool IsVisible(SessionItemViewModel item) => item.Matches(FilterText) && OutcomeFilter switch
    {
        OutcomeFilter.ErrorsOnly => item.IsError,
        OutcomeFilter.InProgress => item.Outcome == SessionOutcome.InProgress,
        OutcomeFilter.Completed => item.Outcome == SessionOutcome.Completed,
        _ => true,
    };

    private void Refilter()
    {
        var visible = (NewestOnTop ? Enumerable.Reverse(_all) : _all).Where(IsVisible).ToList();
        if (visible.SequenceEqual(Items))
        {
            return;
        }
        Items.Clear();
        foreach (var v in visible)
        {
            Items.Add(v);
        }
    }

    partial void OnFilterTextChanged(string? value) => Refilter();

    partial void OnOutcomeFilterChanged(OutcomeFilter value) => Refilter();

    partial void OnSelectedItemChanged(SessionItemViewModel? value) => Detail.Load(value?.Snapshot);

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            var batches = _paused.ToList();
            _paused.Clear();
            foreach (var b in batches)
            {
                ApplyCore(b);
            }
        }
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    /// <summary>Clears this source's sessions from the store (and therefore from the feed).</summary>
    [RelayCommand]
    private void Clear()
    {
        if (Source is { } src)
        {
            _store.RemoveWhere(s => s.Source == src);
        }
        else
        {
            _store.Clear();
        }
        foreach (var id in _byId.Keys.ToList())
        {
            RemoveLocal(id);
        }
        OnPropertyChanged(nameof(TotalCount));
    }
}
