using System.Globalization;

namespace AcsTool.Core.Cwmp;

/// <summary>TR-069 event codes (Appendix B.1).</summary>
public static class EventCodes
{
    public const string Bootstrap = "0 BOOTSTRAP";
    public const string Boot = "1 BOOT";
    public const string Periodic = "2 PERIODIC";
    public const string Scheduled = "3 SCHEDULED";
    public const string ValueChange = "4 VALUE CHANGE";
    public const string Kicked = "5 KICKED";
    public const string ConnectionRequest = "6 CONNECTION REQUEST";
    public const string TransferComplete = "7 TRANSFER COMPLETE";
    public const string DiagnosticsComplete = "8 DIAGNOSTICS COMPLETE";
    public const string RequestDownload = "9 REQUEST DOWNLOAD";
    public const string AutonomousTransferComplete = "10 AUTONOMOUS TRANSFER COMPLETE";
    public const string DuStateChangeComplete = "11 DU STATE CHANGE COMPLETE";
    public const string AutonomousDuStateChangeComplete = "12 AUTONOMOUS DU STATE CHANGE COMPLETE";
    public const string Wakeup = "13 WAKEUP";
    public const string Heartbeat = "14 HEARTBEAT";
    public const string MReboot = "M Reboot";
    public const string MScheduleInform = "M ScheduleInform";
    public const string MDownload = "M Download";
    public const string MScheduleDownload = "M ScheduleDownload";
    public const string MUpload = "M Upload";
    public const string MChangeDuState = "M ChangeDUState";

    public static IReadOnlyList<string> Standard { get; } =
    [
        Bootstrap, Boot, Periodic, Scheduled, ValueChange, Kicked, ConnectionRequest, TransferComplete,
        DiagnosticsComplete, RequestDownload, AutonomousTransferComplete, DuStateChangeComplete,
        AutonomousDuStateChangeComplete, Wakeup, Heartbeat,
    ];

    public static IReadOnlyList<string> MethodEvents { get; } =
        [MReboot, MScheduleInform, MDownload, MScheduleDownload, MUpload, MChangeDuState];

    /// <summary>Numeric event code (e.g. 1 for "1 BOOT"), or null for M/X events.</summary>
    public static int? NumericCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var space = code.IndexOf(' ', StringComparison.Ordinal);
        var head = space < 0 ? code : code[..space];
        return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    public static bool IsMethodEvent(string code) => code.StartsWith("M ", StringComparison.Ordinal);

    public static bool IsVendorEvent(string code) => code.StartsWith("X ", StringComparison.Ordinal);

    /// <summary>Single-type events may appear at most once per Inform (0-6 and 8-14). "7 TRANSFER COMPLETE" is also single per spec table.</summary>
    public static bool IsSingle(string code)
    {
        var n = NumericCode(code);
        return n is >= 0 and <= 14;
    }

    /// <summary>Short display form used in feeds: "1 BOOT" → "1", "M Reboot" → "M Reboot".</summary>
    public static string Short(string code) => NumericCode(code) is { } n ? n.ToString(CultureInfo.InvariantCulture) : code;

    /// <summary>Ordering: numeric events ascending, then M events, then X vendor events, then anything else.</summary>
    public static int OrderKey(string code)
    {
        if (NumericCode(code) is { } n)
        {
            return n;
        }
        if (IsMethodEvent(code))
        {
            return 1000;
        }
        return IsVendorEvent(code) ? 2000 : 3000;
    }
}
