using CrmAiPoc.Api.Crm.FetchXml;

namespace CrmAiPoc.Tests;

public sealed class FetchXmlArgumentParserTests
{
    [Fact]
    public void Accepts_Complete_FetchXml()
    {
        var parsed = FetchXmlArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["fetchXml"] = """<fetch top="5"><entity name="account"><attribute name="name" /></entity></fetch>""",
            ["resultMode"] = "complete"
        });

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(FetchXmlResultMode.Complete, parsed.Request!.ResultMode);
        Assert.Null(parsed.Request.Limitation);
    }

    [Fact]
    public void Accepts_Partial_With_Limitation()
    {
        var parsed = FetchXmlArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["fetchXml"] = """<fetch aggregate="true"><entity name="salesorder"><attribute name="totalamount" alias="revenue" aggregate="sum" /></entity></fetch>""",
            ["resultMode"] = "partial",
            ["limitation"] = "Comparing monthly revenue totals against the annual monthly average is not currently supported."
        });

        Assert.True(parsed.Success, parsed.Error);
        Assert.Equal(FetchXmlResultMode.Partial, parsed.Request!.ResultMode);
        Assert.Contains("annual monthly average", parsed.Request.Limitation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_Partial_Without_Limitation()
    {
        var parsed = FetchXmlArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["fetchXml"] = "<fetch><entity name=\"account\" /></fetch>",
            ["resultMode"] = "partial"
        });

        Assert.False(parsed.Success);
        Assert.Contains("limitation", parsed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_Complete_With_Limitation()
    {
        var parsed = FetchXmlArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["fetchXml"] = "<fetch><entity name=\"account\" /></fetch>",
            ["resultMode"] = "complete",
            ["limitation"] = "Something was not completed."
        });

        Assert.False(parsed.Success);
    }

    [Fact]
    public void Rejects_Claimed_Execution_In_Limitation()
    {
        var parsed = FetchXmlArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["fetchXml"] = "<fetch><entity name=\"account\" /></fetch>",
            ["resultMode"] = "partial",
            ["limitation"] = "The CRM data has already been retrieved but averages are unsupported."
        });

        Assert.False(parsed.Success);
        Assert.Contains("already retrieved", parsed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_Legacy_Query_Plan_Arguments()
    {
        var parsed = FetchXmlArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["fetchXml"] = "<fetch><entity name=\"account\" /></fetch>",
            ["entity"] = "account",
            ["operation"] = "list"
        });

        Assert.False(parsed.Success);
        Assert.Contains("Legacy", parsed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_Missing_FetchXml()
    {
        var parsed = FetchXmlArgumentParser.ParseFromToolArguments(new Dictionary<string, object?>
        {
            ["resultMode"] = "complete"
        });

        Assert.False(parsed.Success);
        Assert.Equal("MALFORMED_TOOL_ARGUMENTS", parsed.ErrorCode);
    }
}
