using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;

namespace AcsTool.Simulator.Session;

/// <summary>
/// Events waiting to be delivered in an Inform (plan §3.7.2): single-type events are de-duplicated, "M ..." events keep each
/// CommandKey; order = numeric ascending, then M events, then X vendor events (insertion order within a group).
/// </summary>
public sealed class PendingEventQueue
{
    private readonly object _lock = new();
    private readonly List<EventStruct> _events = [];

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _events.Count;
            }
        }
    }

    public void Add(IEnumerable<EventStruct> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_lock)
        {
            foreach (var e in events)
            {
                if (EventCodes.IsSingle(e.EventCode))
                {
                    if (!_events.Any(x => x.EventCode == e.EventCode))
                    {
                        _events.Add(e with { CommandKey = e.CommandKey ?? "" });
                    }
                }
                else if (!_events.Contains(e))
                {
                    _events.Add(e);
                }
            }
        }
    }

    public IReadOnlyList<EventStruct> Snapshot()
    {
        lock (_lock)
        {
            return _events.Select((e, i) => (e, i)).OrderBy(x => EventCodes.OrderKey(x.e.EventCode)).ThenBy(x => x.i).Select(x => x.e).ToList();
        }
    }

    public bool Contains(string eventCode)
    {
        lock (_lock)
        {
            return _events.Any(e => e.EventCode == eventCode);
        }
    }

    /// <summary>Removes delivered events. With <paramref name="sessionCompleted"/> false, 0 BOOTSTRAP is kept (spec).</summary>
    public void Remove(IEnumerable<EventStruct> delivered, bool sessionCompleted)
    {
        ArgumentNullException.ThrowIfNull(delivered);
        lock (_lock)
        {
            foreach (var e in delivered)
            {
                if (!sessionCompleted && e.EventCode == EventCodes.Bootstrap)
                {
                    continue;
                }
                _events.Remove(e);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }
}
