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

    /// <summary>A namespace-prefixed element or attribute name: the "UDF" in
    /// &lt;UDF:FIELD&gt; or in UDF:attr="x".</summary>
    [GeneratedRegex(@"[<\s]/?([A-Za-z_][\w.\-]*):[A-Za-z_]")]
    private static partial Regex PrefixedName();

    /// <summary>The document's first real element start tag, skipping the XML
    /// declaration, comments and processing instructions.</summary>
    [GeneratedRegex(@"<[A-Za-z_][\w.\-]*(?:\s[^>]*)?>")]
    private static partial Regex RootStartTag();

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
        text = DeclareUndeclaredPrefixes(text);
        return text;
    }

    /// <summary>
    /// Tally serialises user-defined fields with a namespace prefix - e.g.
    /// &lt;UDF:MYFIELD&gt; - but never declares the namespace. That makes the
    /// ENTIRE response invalid XML, so one custom field on one voucher costs a
    /// whole extraction window (observed: March 2026 vouchers, "'UDF' is an
    /// undeclared prefix"). Declaring every used-but-undeclared prefix on the
    /// root element makes the document parseable while leaving every element
    /// name the extractors read completely untouched - they look up unprefixed
    /// names, which are unaffected. Over-declaring a prefix is harmless.
    /// </summary>
    internal static string DeclareUndeclaredPrefixes(string text)
    {
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in PrefixedName().Matches(text))
        {
            var prefix = m.Groups[1].Value;
            if (prefix is "xml" or "xmlns") continue;
            if (text.Contains($"xmlns:{prefix}=", StringComparison.Ordinal)) continue;
            prefixes.Add(prefix);
        }
        if (prefixes.Count == 0) return text;

        var root = RootStartTag().Match(text);
        if (!root.Success) return text;

        var declarations = new StringBuilder();
        foreach (var prefix in prefixes)
            declarations.Append(" xmlns:").Append(prefix)
                        .Append("=\"urn:tally:udf:").Append(prefix).Append('"');

        // Insert before the tag's closing ">" (or "/>" for an empty root).
        var closing = root.Value.EndsWith("/>", StringComparison.Ordinal) ? 2 : 1;
        return text.Insert(root.Index + root.Length - closing, declarations.ToString());
    }

    public static XDocument Parse(byte[] raw)
    {
        var text = Sanitize(raw);
        return XDocument.Parse(text, LoadOptions.None);
    }

    // ── field readers (direct children first; attributes as a Tally master fallback) ──

    /// <summary>
    /// Read a direct child field. Tally master exports frequently emit NAME (and
    /// occasionally other scalar fields) as an attribute on the master element
    /// instead of a child node. Falling back to a same-named attribute prevents
    /// valid Group/StockItem masters from being silently discarded as unnamed.
    /// </summary>
    public static string Text(XElement el, string tag, string fallback = "")
    {
        var child = el.Element(tag)?.Value.Trim();
        if (!string.IsNullOrEmpty(child)) return child;

        var attr = el.Attribute(tag)?.Value.Trim();
        if (!string.IsNullOrEmpty(attr)) return attr;

        // Be tolerant of case differences introduced by some Tally builds/TDL.
        attr = el.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals(tag, StringComparison.OrdinalIgnoreCase))?
            .Value.Trim();
        return !string.IsNullOrEmpty(attr) ? attr : fallback;
    }

    public static double Num(XElement el, string tag)
    {
        var raw = Text(el, tag, "0").Replace(",", "").Trim();
        raw = NonNumeric().Replace(raw, "");
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0;
    }

    public static long Int(XElement el, string tag) => (long)Num(el, tag);

    public static bool Bool(XElement el, string tag)
    {
        var v = Text(el, tag).Trim().ToUpperInvariant();
        return v is "YES" or "TRUE" or "1";
    }

    private static readonly string[] DateFormats = ["yyyyMMdd", "yyyy-MM-dd", "d-MMM-yyyy", "dd-MMM-yyyy", "dd/MM/yyyy"];

    /// <summary>ISO yyyy-MM-dd or null.</summary>
    public static string? Date(XElement el, string tag)
    {
        var raw = Text(el, tag).Trim();
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
