using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TallyAgent.Core.Tally;

/// <summary>
/// Tolerant helpers for Tally's famously non-compliant XML:
/// UTF-16 BOMs, illegal control characters, malformed numeric entities,
/// Indian-format numbers ("1,23,456.78"), multiple date formats.
/// </summary>
public static partial class TallyXml
{
    [GeneratedRegex(@"&#([0-8]|1[0-24-9]|2[0-9]|3[01]);")]
    private static partial Regex BadDecEntity();

    [GeneratedRegex(@"&#x([0-8BbCcEeFf]|1[0-9A-Fa-f]);")]
    private static partial Regex BadHexEntity();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex IllegalChars();

    [GeneratedRegex(@"[^\d.\-]")]
    private static partial Regex NonNumeric();

    /// <summary>Decode + scrub a raw Tally response into parseable XML text.</summary>
    public static string Sanitize(byte[] raw)
    {
        string text;
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            text = Encoding.Unicode.GetString(raw);           // UTF-16 LE
        else if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            text = Encoding.BigEndianUnicode.GetString(raw);  // UTF-16 BE
        else
            text = Encoding.UTF8.GetString(raw);

        text = BadDecEntity().Replace(text, "");
        text = BadHexEntity().Replace(text, "");
        text = IllegalChars().Replace(text, "");
        return text;
    }

    public static XDocument Parse(byte[] raw)
    {
        var text = Sanitize(raw);
        return XDocument.Parse(text, LoadOptions.None);
    }

    // ── field readers (direct children only, mirroring findtext) ──

    public static string Text(XElement el, string tag, string fallback = "") =>
        el.Element(tag)?.Value.Trim() ?? fallback;

    public static double Num(XElement el, string tag)
    {
        var raw = (el.Element(tag)?.Value ?? "0").Replace(",", "").Trim();
        raw = NonNumeric().Replace(raw, "");
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0;
    }

    public static long Int(XElement el, string tag) => (long)Num(el, tag);

    public static bool Bool(XElement el, string tag)
    {
        var v = (el.Element(tag)?.Value ?? "").Trim().ToUpperInvariant();
        return v is "YES" or "TRUE" or "1";
    }

    private static readonly string[] DateFormats = ["yyyyMMdd", "yyyy-MM-dd", "d-MMM-yyyy", "dd-MMM-yyyy", "dd/MM/yyyy"];

    /// <summary>ISO yyyy-MM-dd or null.</summary>
    public static string? Date(XElement el, string tag)
    {
        var raw = (el.Element(tag)?.Value ?? "").Trim();
        if (raw.Length == 0) return null;
        foreach (var fmt in DateFormats)
            if (DateTime.TryParseExact(raw, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d.ToString("yyyy-MM-dd");
        return null;
    }

    /// <summary>All descendants with the given tag name (Tally nests unpredictably).</summary>
    public static IEnumerable<XElement> Descendants(XElement root, string tag) => root.Descendants(tag);

    public static string XmlEscape(string s) => System.Security.SecurityElement.Escape(s) ?? s;
}
