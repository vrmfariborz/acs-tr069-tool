namespace AcsTool.Core.Model;

public sealed record CwmpHeader(string? Id, bool IdMustUnderstand = true, bool? HoldRequests = null,
    uint? SessionTimeout = null, bool? NoMoreRequests = null)
{
    public static CwmpHeader None { get; } = new(Id: null);
}
