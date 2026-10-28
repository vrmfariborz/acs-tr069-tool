using System.Globalization;
using AcsTool.Core.Model;

namespace AcsTool.Core.Building;

public interface IInformBuilder
{
    InformMessage Create(DeviceIdStruct id, IEnumerable<EventStruct> events, IEnumerable<ParameterValue> parameters,
        uint retryCount, DateTimeOffset now, uint maxEnvelopes = 1);
}

public sealed class InformBuilder : IInformBuilder
{
    public static string FormatTime(DateTimeOffset t) => t.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    public InformMessage Create(DeviceIdStruct id, IEnumerable<EventStruct> events, IEnumerable<ParameterValue> parameters,
        uint retryCount, DateTimeOffset now, uint maxEnvelopes = 1)
    {
        var truncated = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Offset);
        return new InformMessage(id, events.ToList(), maxEnvelopes, FormatTime(truncated), retryCount, parameters.ToList())
        {
            CurrentTimeParsed = truncated,
        };
    }
}
