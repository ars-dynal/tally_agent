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

    /// <summary>The encoding named in an XML declaration, e.g. encoding="UTF-16".</summary>
    [GeneratedRegex(@"encoding\s*=\s*[""']([A-Za-z0-9._\-]+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex XmlDeclEncoding();

    /// <summary>Decode + scrub a raw Tally response into parseable XML text.
    /// <paramref name="httpCharset"/> is the HTTP Content-Type charset, when the
    /// transport supplied one.</summary>
    public static string Sanitize(byte[] raw, string? httpCharset = null)
    {
        var text = Decode(raw, httpCharset);

        // A decoded BOM survives as U+FEFF, and XDocument.Parse rejects it as
        // "Data at the root level is invalid". Tally's UI exports are UTF-16LE
        // WITH a BOM, so this is the difference between reading a real export
        // and failing on byte one.
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        text = BadDecEntity().Replace(text, "");
        text = BadHexEntity().Replace(text, "");
        text = IllegalChars().Replace(text, "");
        text = DeclareUndeclaredPrefixes(text);
        return text;
    }

    /// <summary>
    /// Decode the response bytes as the encoding the RESPONSE ITSELF declares,
    /// in the order the declarations can be trusted:
    ///
    ///   1. a byte-order mark — unambiguous, and what Tally's UI exports carry;
    ///   2. the XML declaration's encoding= attribute, sniffed from the ASCII-safe
    ///      prefix (a UTF-16 declaration is readable either way once the nulls
    ///      are stripped);
    ///   3. the HTTP Content-Type charset, when the transport gave one;
    ///   4. strict UTF-8 — and if the bytes are NOT valid UTF-8, Latin-1.
    ///
    /// Step 4 is the fix for mangled item names. Decoding single-byte text as
    /// UTF-8 turns every byte above 0x7F into U+FFFD, so "SS M8×40MM" (× is
    /// 0xD7) arrived as "SS M8�40MM" — silently, since U+FFFD is a
    /// perfectly valid character and nothing downstream could tell it from real
    /// data. Decoding strictly and falling back on failure keeps the byte.
    /// </summary>
    internal static string Decode(byte[] raw, string? httpCharset = null)
    {
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            return Encoding.Unicode.GetString(raw);            // UTF-16 LE + BOM
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(raw);   // UTF-16 BE + BOM
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return new UTF8Encoding(false).GetString(raw, 3, raw.Length - 3);

        // BOM-less UTF-16 still declares itself: every other byte is 0x00.
        if (raw.Length >= 4 && raw[1] == 0x00 && raw[3] == 0x00)
            return Encoding.Unicode.GetString(raw);
        if (raw.Length >= 4 && raw[0] == 0x00 && raw[2] == 0x00)
            return Encoding.BigEndianUnicode.GetString(raw);

        var declared = DeclaredEncodingName(raw) ?? httpCharset;
        if (declared is not null && TryGetEncoding(declared) is { } enc)
        {
            try { return enc.GetString(raw); }
            catch (DecoderFallbackException) { /* fall through to sniffing */ }
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8. Latin-1 maps every byte to a character, so nothing is
            // lost and 0xD7 becomes the multiplication sign it always was.
            return Encoding.Latin1.GetString(raw);
        }
    }

    /// <summary>encoding="..." from the XML declaration, read from the first
    /// bytes with any UTF-16 padding nulls removed.</summary>
    private static string? DeclaredEncodingName(byte[] raw)
    {
        var take = Math.Min(raw.Length, 200);
        var sb = new StringBuilder(take);
        for (var i = 0; i < take; i++)
            if (raw[i] is not 0 and < 0x80) sb.Append((char)raw[i]);
        var prefix = sb.ToString();
        if (!prefix.Contains("<?xml", StringComparison.OrdinalIgnoreCase)) return null;
        var m = XmlDeclEncoding().Match(prefix);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static Encoding? TryGetEncoding(string name)
    {
        try
        {
            // Throw on bad bytes so a wrong declaration falls through to
            // sniffing rather than silently producing replacement characters.
            var enc = Encoding.GetEncoding(name, EncoderFallback.ReplacementFallback,
                DecoderFallback.ExceptionFallback);
            // A declared UTF-16 with no BOM was already handled above; treating
            // it as 8-bit here would corrupt the whole document.
            return enc;
        }
        catch (ArgumentException) { return null; }
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

    public static XDocument Parse(byte[] raw, string? httpCharset = null)
    {
        var text = Sanitize(raw, httpCharset);
        return XDocument.Parse(text, LoadOptions.None);
    }

    /// <summary>
    /// The error text when Tally REFUSED the request, or null when it answered.
    ///
    /// This is the most dangerous shape Tally produces, because it is not an
    /// error by any mechanism the agent was watching:
    ///
    ///     &lt;RESPONSE&gt;Unknown Request, cannot be processed&lt;/RESPONSE&gt;
    ///
    /// HTTP 200. Well-formed XML. Parses cleanly. Contains zero data rows. Every
    /// extractor that says <c>if (rows.Count == 0) → fall back</c> therefore
    /// treats a REFUSAL as an EMPTY REPORT, quietly runs a different code path,
    /// and returns a plausible number that nothing marks as suspect. The
    /// zero-row guard does not fire either, because the fallback produced rows.
    ///
    /// Same disease as the silently-ignored FETCH entry in CLAUDE.md: a valid
    /// response that means "no" and reads as "nothing". Detected HERE, once, for
    /// every caller — never special-cased per dataset.
    ///
    /// Deliberately conservative, so a real data response can never be mistaken
    /// for a refusal:
    ///   • any LINEERROR element — Tally's TDL error channel;
    ///   • a root RESPONSE element carrying text and NO child elements.
    /// A RESPONSE element with children is left alone: import acknowledgements
    /// use that shape and are not errors.
    /// </summary>
    public static string? FindRequestError(XDocument doc)
    {
        var lineError = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("LINEERROR", StringComparison.OrdinalIgnoreCase));
        if (lineError is not null)
        {
            var text = lineError.Value.Trim();
            if (text.Length > 0) return text;
        }

        var root = doc.Root;
        if (root is not null &&
            root.Name.LocalName.Equals("RESPONSE", StringComparison.OrdinalIgnoreCase) &&
            !root.Elements().Any())
        {
            var text = root.Value.Trim();
            if (text.Length > 0) return text;
        }

        return null;
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

    // "d-MMM-yy" and "dd-MMM-yy" are what Tally's REPORT exports use ("1-Nov-21",
    // "29-May-22"). Without them every bill date parsed to null while the rest of
    // the record looked perfectly fine.
    private static readonly string[] DateFormats =
    [
        "yyyyMMdd", "yyyy-MM-dd", "d-MMM-yyyy", "dd-MMM-yyyy", "dd/MM/yyyy",
        "d-MMM-yy", "dd-MMM-yy", "d/M/yyyy",
    ];

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
