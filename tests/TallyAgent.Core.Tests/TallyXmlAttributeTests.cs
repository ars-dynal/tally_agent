using System.Xml.Linq;
using TallyAgent.Core.Tally;
using Xunit;

namespace TallyAgent.Core.Tests;

public sealed class TallyXmlAttributeTests
{
    [Fact]
    public void Text_Falls_Back_To_Master_Name_Attribute()
    {
        var el = XElement.Parse("<STOCKITEM NAME=\"Copper Busbar\"><PARENT>Metals</PARENT></STOCKITEM>");

        Assert.Equal("Copper Busbar", TallyXml.Text(el, "NAME"));
        Assert.Equal("Metals", TallyXml.Text(el, "PARENT"));
    }

    [Fact]
    public void Text_Prefers_Child_Over_Attribute()
    {
        var el = XElement.Parse("<GROUP NAME=\"Attribute Name\"><NAME>Child Name</NAME></GROUP>");

        Assert.Equal("Child Name", TallyXml.Text(el, "NAME"));
    }

    [Fact]
    public void Numeric_Readers_Also_Accept_Attribute_Fallback()
    {
        var el = XElement.Parse("<ITEM ALTERID=\"42\" />");

        Assert.Equal(42, TallyXml.Int(el, "ALTERID"));
    }
}
