using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Simulator.Profiles;

namespace AcsTool.Simulator.Parameters;

public sealed record StoredParameter(string Name, string XsdType, string Value, bool Writable, bool IncludeInInform,
    int Notification, IReadOnlyList<string> AccessList);

/// <summary>Either a value or a CWMP fault (plan: LookupResult / NamesResult / SpvResult collapsed into one generic result).</summary>
#pragma warning disable CA1000 // factory helpers on the result type read best at call sites
public sealed record FaultOr<T>(T? Value, CwmpFault? Fault)
{
    public bool IsFault => Fault is not null;

    public static FaultOr<T> Ok(T value) => new(value, null);

    public static FaultOr<T> Fail(uint code, string? text = null, IReadOnlyList<SetParameterValuesFaultItem>? items = null) =>
        new(default, CwmpMessageBuilder.CreateFault(code, text, items));
}
#pragma warning restore CA1000

/// <summary>
/// Per-CPE parameter tree (plan §3.7.1). Leaves are stored in insertion order; object nodes ("X.Y.") are synthesized
/// from leaf names. Writes happen on the CPE loop; reads may come from the UI, so all access is under one lock.
/// </summary>
public sealed class ParameterStore
{
    private readonly object _lock = new();
    private readonly List<StoredParameter> _ordered = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changedByLocalEdit = new(StringComparer.Ordinal);

    public ParameterStore(IEnumerable<ParameterDefinition> seed) => Reseed(seed);

    public void Reseed(IEnumerable<ParameterDefinition> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        lock (_lock)
        {
            _ordered.Clear();
            _index.Clear();
            _changedByLocalEdit.Clear();
            foreach (var d in seed)
            {
                if (_index.ContainsKey(d.Name))
                {
                    continue;
                }
                _index[d.Name] = _ordered.Count;
                _ordered.Add(new StoredParameter(d.Name, XsdTypes.Normalize(d.XsdType), d.Value, d.Writable, d.IncludeInInform, d.Notification, []));
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _ordered.Count;
            }
        }
    }

    public IReadOnlyList<StoredParameter> Snapshot()
    {
        lock (_lock)
        {
            return [.. _ordered];
        }
    }

    public StoredParameter? Get(string name)
    {
        lock (_lock)
        {
            return _index.TryGetValue(name, out var i) ? _ordered[i] : null;
        }
    }

    public string? RootObject
    {
        get
        {
            lock (_lock)
            {
                return _ordered.Count == 0 ? null : _ordered[0].Name[..(_ordered[0].Name.IndexOf('.', StringComparison.Ordinal) + 1)];
            }
        }
    }

    /// <summary>GetParameterValues: exact names, partial paths ending in '.', or "" (whole tree). 9005 on any unknown name.</summary>
    public FaultOr<IReadOnlyList<ParameterValue>> GetValues(IReadOnlyList<string> namesOrPartialPaths)
    {
        ArgumentNullException.ThrowIfNull(namesOrPartialPaths);
        lock (_lock)
        {
            var result = new List<ParameterValue>();
            foreach (var raw in namesOrPartialPaths)
            {
                var name = raw?.Trim() ?? "";
                if (name.Length == 0 || name.EndsWith('.'))
                {
                    var matches = _ordered.Where(p => p.Name.StartsWith(name, StringComparison.Ordinal)).ToList();
                    if (matches.Count == 0)
                    {
                        return FaultOr<IReadOnlyList<ParameterValue>>.Fail(FaultCodes.InvalidParameterName, $"Invalid parameter name: {name}");
                    }
                    result.AddRange(matches.Select(ToValue));
                }
                else if (_index.TryGetValue(name, out var i))
                {
                    result.Add(ToValue(_ordered[i]));
                }
                else
                {
                    return FaultOr<IReadOnlyList<ParameterValue>>.Fail(FaultCodes.InvalidParameterName, $"Invalid parameter name: {name}");
                }
            }
            return FaultOr<IReadOnlyList<ParameterValue>>.Ok(result);
        }
    }

    /// <summary>
    /// GetParameterNames. "" → the root object (NextLevel=true) or the whole tree. An object path → its direct children
    /// (NextLevel=true) or itself plus all descendants. A leaf → itself (NextLevel=false) or 9003 (NextLevel=true).
    /// </summary>
    public FaultOr<IReadOnlyList<ParameterInfo>> GetNames(string path, bool nextLevel)
    {
        path ??= "";
        lock (_lock)
        {
            if (path.Length > 0 && !path.EndsWith('.'))
            {
                if (!_index.TryGetValue(path, out var i))
                {
                    return FaultOr<IReadOnlyList<ParameterInfo>>.Fail(FaultCodes.InvalidParameterName, $"Invalid parameter name: {path}");
                }
                return nextLevel
                    ? FaultOr<IReadOnlyList<ParameterInfo>>.Fail(FaultCodes.InvalidArguments, "NextLevel=true is not valid for a parameter")
                    : FaultOr<IReadOnlyList<ParameterInfo>>.Ok([new ParameterInfo(path, _ordered[i].Writable)]);
            }
            var under = _ordered.Where(p => p.Name.StartsWith(path, StringComparison.Ordinal)).ToList();
            if (under.Count == 0)
            {
                return FaultOr<IReadOnlyList<ParameterInfo>>.Fail(FaultCodes.InvalidParameterName, $"Invalid parameter name: {path}");
            }
            var result = new List<ParameterInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string name, bool writable)
            {
                if (seen.Add(name))
                {
                    result.Add(new ParameterInfo(name, writable));
                }
            }
            if (nextLevel)
            {
                foreach (var p in under)
                {
                    var rest = p.Name[path.Length..];
                    var dot = rest.IndexOf('.', StringComparison.Ordinal);
                    if (dot < 0)
                    {
                        Add(p.Name, p.Writable);
                    }
                    else
                    {
                        Add(path + rest[..(dot + 1)], false);
                    }
                }
            }
            else
            {
                if (path.Length > 0)
                {
                    Add(path, false);
                }
                foreach (var p in under)
                {
                    // every intermediate object node, in first-seen order, then the leaf
                    var name = p.Name;
                    for (var idx = name.IndexOf('.', path.Length); idx >= 0 && idx < name.Length - 1; idx = name.IndexOf('.', idx + 1))
                    {
                        Add(name[..(idx + 1)], false);
                    }
                    Add(name, p.Writable);
                }
            }
            return FaultOr<IReadOnlyList<ParameterInfo>>.Ok(result);
        }
    }

    /// <summary>SetParameterValues: validate every item, then commit all (atomic). Faults: 9003 with per-item 9005/9006/9007/9008.</summary>
    public FaultOr<int> TrySetMany(IReadOnlyList<ParameterValue> values, string parameterKey)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_lock)
        {
            var items = new List<SetParameterValuesFaultItem>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in values)
            {
                if (!_index.TryGetValue(v.Name, out var i))
                {
                    items.Add(Item(v.Name, FaultCodes.InvalidParameterName));
                    continue;
                }
                var p = _ordered[i];
                if (!names.Add(v.Name))
                {
                    items.Add(Item(v.Name, FaultCodes.InvalidArguments, "Parameter appears more than once"));
                }
                else if (!p.Writable)
                {
                    items.Add(Item(v.Name, FaultCodes.NonWritableParameter));
                }
                else if (!TypeCompatible(p.XsdType, v.XsdType))
                {
                    items.Add(Item(v.Name, FaultCodes.InvalidParameterType, $"Invalid parameter type: expected {p.XsdType}, got {XsdTypes.Normalize(v.XsdType)}"));
                }
                else if (!XsdTypes.IsValidValue(p.XsdType, v.Value))
                {
                    items.Add(Item(v.Name, FaultCodes.InvalidParameterValue));
                }
            }
            if (items.Count > 0)
            {
                return FaultOr<int>.Fail(FaultCodes.InvalidArguments, null, items);
            }
            foreach (var v in values)
            {
                var i = _index[v.Name];
                _ordered[i] = _ordered[i] with { Value = NormalizeValue(_ordered[i].XsdType, v.Value) };
                _changedByLocalEdit.Remove(v.Name); // ACS-originated changes are not reported as VALUE CHANGE
            }
            SetParameterKeyLocked(parameterKey);
            return FaultOr<int>.Ok(0);
        }
    }

    /// <summary>A UI edit; returns true when the parameter has active notification (should trigger 4 VALUE CHANGE now).</summary>
    public bool ApplyLocalEdit(string name, string value)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(name, out var i))
            {
                throw new KeyNotFoundException($"Unknown parameter {name}");
            }
            var p = _ordered[i];
            if (p.Value == value)
            {
                return false;
            }
            _ordered[i] = p with { Value = value };
            if (p.Notification > 0)
            {
                _changedByLocalEdit.Add(name);
            }
            return p.Notification == 2;
        }
    }

    /// <summary>Simulator-internal update (UpTime etc.): never reported as a change, no validation.</summary>
    internal void SetInternal(string name, string value)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(name, out var i))
            {
                _ordered[i] = _ordered[i] with { Value = value };
            }
        }
    }

    public bool HasPendingValueChange
    {
        get
        {
            lock (_lock)
            {
                return _changedByLocalEdit.Count > 0;
            }
        }
    }

    public IReadOnlyList<ParameterValue> TakeChangedForValueChange()
    {
        lock (_lock)
        {
            var list = _changedByLocalEdit.Where(_index.ContainsKey).Select(n => ToValue(_ordered[_index[n]])).ToList();
            _changedByLocalEdit.Clear();
            return list;
        }
    }

    private void SetParameterKeyLocked(string parameterKey)
    {
        foreach (var name in new[] { "InternetGatewayDevice.ManagementServer.ParameterKey", "Device.ManagementServer.ParameterKey" })
        {
            if (_index.TryGetValue(name, out var i))
            {
                _ordered[i] = _ordered[i] with { Value = parameterKey ?? "" };
            }
        }
    }

    private static bool TypeCompatible(string declared, string? given)
    {
        // A missing xsi:type is treated as "use the declared type" (many ACSs send xsd:string for everything, but we
        // follow the spec and only accept exact declared types when a type is present).
        if (string.IsNullOrEmpty(given))
        {
            return true;
        }
        return string.Equals(XsdTypes.Normalize(given), declared, StringComparison.Ordinal);
    }

    private static string NormalizeValue(string type, string value) =>
        type == XsdTypes.Boolean ? (value.Trim() is "1" or "true" ? "true" : "false") : value;

    private static SetParameterValuesFaultItem Item(string name, uint code, string? text = null) =>
        new(name, code, text ?? FaultCodes.DefaultString(code));

    private static ParameterValue ToValue(StoredParameter p) => new(p.Name, p.Value, p.XsdType);
}
