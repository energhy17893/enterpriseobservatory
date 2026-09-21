using System.Xml;
using System.Xml.Linq;

namespace EnterpriseObservatory.Collectors.Vsphere;

/// <summary>
/// The one way this collector turns a vCenter reply into XML.
/// </summary>
/// <remarks>
/// <para>
/// A reply is text from the far end of a network connection — from vCenter,
/// or, when <see cref="VsphereConnectionOptions.AcceptUntrustedCertificate"/>
/// is on, from anyone who can sit in the path. vim25 SOAP never carries a DTD,
/// so a reply that declares one is not vCenter being unusual; it is someone
/// trying entity expansion or an external fetch. It is refused rather than
/// processed, and there is exactly one place that says so.
/// </para>
/// <para>
/// Everything else matches <see cref="XDocument.Parse(string)"/>, which this
/// replaces: insignificant whitespace is dropped, and a malformed reply is an
/// <see cref="XmlException"/> exactly as before.
/// </para>
/// </remarks>
public static class VsphereXml
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
    };

    /// <summary>Parses a reply, refusing any DTD.</summary>
    /// <exception cref="XmlException">The reply is malformed or declares a DTD.</exception>
    public static XDocument Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, Settings);
        return XDocument.Load(reader);
    }

    /// <summary>
    /// Whether the reply's prolog declares a document type.
    /// </summary>
    /// <remarks>
    /// Only the prolog is read — a DTD cannot appear after the root element —
    /// so this costs nothing on a large reply. A reply whose prolog reads
    /// cleanly has no DTD; one whose prolog fails is judged by whether it
    /// contains a declaration at all.
    /// </remarks>
    public static bool DeclaresDocumentType(string xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return false;
        }

        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, Settings);
            reader.MoveToContent();
            return false;
        }
        catch (XmlException)
        {
            return xml.Contains("<!DOCTYPE", StringComparison.Ordinal);
        }
    }
}
