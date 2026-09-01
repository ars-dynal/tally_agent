using System.Text;
using TallyAgent.Core.Tally;
using Xunit;

namespace TallyAgent.Core.Tests;

/// <summary>
/// Tally serialises user-defined fields with a namespace prefix it never
/// declares. Before this was handled, one custom field on one voucher made the
/// whole response invalid XML and lost an entire extraction window.
/// </summary>
public class TallyXmlNamespaceTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void UndeclaredPrefix_DoesNotFailTheWholeResponse()
    {
        var xml = """
            <ENVELOPE>
              <BODY>
                <VOUCHER>
                  <DATE>20260315</DATE>
                  <VOUCHERNUMBER>845</VOUCHERNUMBER>
                  <UDF:CUSTOMFIELD>anything</UDF:CUSTOMFIELD>
                </VOUCHER>
              </BODY>
            </ENVELOPE>
            """;

        var doc = TallyXml.Parse(Utf8(xml));

        Assert.NotNull(doc.Root);
        var voucher = doc.Descendants("VOUCHER").Single();
        // The fields the extractors read are completely unaffected.
        Assert.Equal("20260315", voucher.Element("DATE")!.Value);
        Assert.Equal("845", voucher.Element("VOUCHERNUMBER")!.Value);
    }

    [Fact]
    public void SeveralUndeclaredPrefixes_AreAllDeclared()
    {
        var xml = """
            <ENVELOPE>
              <VOUCHER>
                <UDF:ONE>a</UDF:ONE>
                <EXT:TWO>b</EXT:TWO>
              </VOUCHER>
            </ENVELOPE>
            """;

        var doc = TallyXml.Parse(Utf8(xml));

        Assert.NotNull(doc.Root);
        Assert.Single(doc.Descendants("VOUCHER"));
    }

    [Fact]
    public void PrefixOnAnAttribute_IsAlsoHandled()
    {
        var xml = """
            <ENVELOPE>
              <VOUCHER UDF:TAG="x">
                <DATE>20260315</DATE>
              </VOUCHER>
            </ENVELOPE>
            """;

        var doc = TallyXml.Parse(Utf8(xml));

        Assert.Equal("20260315", doc.Descendants("VOUCHER").Single().Element("DATE")!.Value);
    }

    [Fact]
    public void AlreadyDeclaredPrefix_IsNotRedeclared()
    {
        var xml = """
            <ENVELOPE xmlns:UDF="urn:already:declared">
              <VOUCHER>
                <UDF:ONE>a</UDF:ONE>
              </VOUCHER>
            </ENVELOPE>
            """;

        var sanitized = TallyXml.Sanitize(Utf8(xml));

        // Exactly one declaration, and the original one is preserved.
        Assert.Single(
            sanitized.Split("xmlns:UDF=", StringSplitOptions.None).Skip(1));
        Assert.Contains("urn:already:declared", sanitized);
        Assert.NotNull(TallyXml.Parse(Utf8(xml)).Root);
    }

    [Fact]
    public void OrdinaryResponse_IsLeftUnchanged()
    {
        var xml = """
            <ENVELOPE>
              <VOUCHER>
                <DATE>20260315</DATE>
              </VOUCHER>
            </ENVELOPE>
            """;

        var sanitized = TallyXml.Sanitize(Utf8(xml));

        Assert.DoesNotContain("xmlns:", sanitized);
    }

    [Fact]
    public void XmlDeclaration_IsNotMistakenForTheRootElement()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ENVELOPE>
              <VOUCHER><UDF:ONE>a</UDF:ONE></VOUCHER>
            </ENVELOPE>
            """;

        var doc = TallyXml.Parse(Utf8(xml));

        Assert.Equal("ENVELOPE", doc.Root!.Name.LocalName);
    }
}
