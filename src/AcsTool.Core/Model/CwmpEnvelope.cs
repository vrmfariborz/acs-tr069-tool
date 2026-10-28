using System.Xml.Linq;
using AcsTool.Core.Cwmp;

namespace AcsTool.Core.Model;

public sealed record CwmpEnvelope(CwmpVersion Version, string? CwmpNamespace, CwmpHeader Header,
    CwmpMessageKind Kind, string MethodLocalName, CwmpMessage? Message, XElement? BodyElement);
