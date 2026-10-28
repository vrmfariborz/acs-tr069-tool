using System.Text.RegularExpressions;
using AcsTool.Core.Cwmp;

namespace AcsTool.Simulator.Profiles;

public sealed record ValidationError(string Field, string Message)
{
    public override string ToString() => $"{Field}: {Message}";
}

public static partial class ProfileValidator
{
    [GeneratedRegex("^[0-9A-F]{6}$")]
    private static partial Regex OuiRegex();

    public static IReadOnlyList<ValidationError> Validate(CpeProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(p.Name))
        {
            errors.Add(new("Name", "Name is required"));
        }
        if (!OuiRegex().IsMatch(p.DeviceId.OUI ?? ""))
        {
            errors.Add(new("DeviceId.OUI", "OUI must be 6 upper-case hex digits (e.g. 00D09E)"));
        }
        if (string.IsNullOrWhiteSpace(p.DeviceId.SerialNumber))
        {
            errors.Add(new("DeviceId.SerialNumber", "Serial number is required"));
        }
        if (string.IsNullOrWhiteSpace(p.DeviceId.ProductClass) && string.IsNullOrWhiteSpace(p.DeviceId.Manufacturer))
        {
            errors.Add(new("DeviceId.Manufacturer", "Manufacturer is required"));
        }
        if (!Uri.TryCreate(p.AcsUrl, UriKind.Absolute, out var acs) || (acs.Scheme != Uri.UriSchemeHttp && acs.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add(new("AcsUrl", "ACS URL must be an absolute http:// or https:// URL"));
        }
        if (p.Version == CwmpVersion.Unknown)
        {
            errors.Add(new("Version", "CWMP version is required"));
        }
        if (p.RequestTimeoutSeconds is < 1 or > 600)
        {
            errors.Add(new("RequestTimeoutSeconds", "Request timeout must be between 1 and 600 seconds"));
        }
        if (p.Instances.Count != 1)
        {
            errors.Add(new("Instances.Count", "Only one instance per profile is supported in this version (multi-CPE is milestone M8)"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (param, i) in p.Parameters.Select((x, i) => (x, i)))
        {
            var field = $"Parameters[{i}]";
            if (string.IsNullOrWhiteSpace(param.Name) || param.Name.EndsWith('.') || param.Name.Contains("..", StringComparison.Ordinal))
            {
                errors.Add(new(field, $"Invalid parameter name '{param.Name}'"));
                continue;
            }
            if (!param.Name.StartsWith(p.RootPrefix, StringComparison.Ordinal))
            {
                errors.Add(new(field, $"'{param.Name}' is not under the profile root {p.RootPrefix}"));
            }
            if (!seen.Add(param.Name))
            {
                errors.Add(new(field, $"Duplicate parameter name '{param.Name}'"));
            }
            if (!XsdTypes.IsKnown(param.XsdType))
            {
                errors.Add(new(field, $"Unknown type '{param.XsdType}' for {param.Name}"));
            }
            else if (!XsdTypes.IsValidValue(param.XsdType, param.Value))
            {
                errors.Add(new(field, $"Value '{param.Value}' is not a valid {XsdTypes.Normalize(param.XsdType)} for {param.Name}"));
            }
            if (param.Notification is < 0 or > 2)
            {
                errors.Add(new(field, $"Notification must be 0, 1 or 2 for {param.Name}"));
            }
        }
        foreach (var ev in p.DefaultEvents)
        {
            if (string.IsNullOrWhiteSpace(ev.EventCode))
            {
                errors.Add(new("DefaultEvents", "Event code must not be empty"));
            }
        }
        foreach (var (method, b) in p.RpcBehaviors)
        {
            if (b.Kind == RpcBehaviorKind.Fault && (b.FaultCode is < 9000 or > 9899))
            {
                errors.Add(new($"RpcBehaviors[{method}]", "CPE fault codes must be in 9000-9899"));
            }
        }
        return errors;
    }
}
