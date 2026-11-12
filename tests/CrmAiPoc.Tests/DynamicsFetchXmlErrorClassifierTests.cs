using CrmAiPoc.Api.Crm.FetchXml;
using CrmAiPoc.Api.Crm.Generic;

namespace CrmAiPoc.Tests;

public sealed class DynamicsFetchXmlErrorClassifierTests
{
    [Fact]
    public void Aggregate_Order_Code_80041131_Is_Recoverable()
    {
        var c = DynamicsFetchXmlErrorClassifier.Classify(
            400,
            new DynamicsCrmErrorParser.ParsedError
            {
                Code = "80041131",
                Message = "The specified order by attribute does not exist on this entity."
            });

        Assert.True(c.IsRecoverable);
        Assert.Equal("INVALID_SORT", c.ErrorCode);
        Assert.DoesNotContain("password", c.SanitizedFeedback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generic_80040203_Without_Semantic_Message_Is_Not_Recoverable()
    {
        var c = DynamicsFetchXmlErrorClassifier.Classify(
            400,
            new DynamicsCrmErrorParser.ParsedError
            {
                Code = "80040203",
                Message = "Invalid Argument"
            });

        Assert.False(c.IsRecoverable);
    }

    [Fact]
    public void Generic_80040203_With_Attribute_Message_Is_Recoverable()
    {
        var c = DynamicsFetchXmlErrorClassifier.Classify(
            400,
            new DynamicsCrmErrorParser.ParsedError
            {
                Code = "80040203",
                Message = "Attribute 'bogusfield' is not a valid attribute for entity salesorder."
            });

        Assert.True(c.IsRecoverable);
        Assert.Equal("INVALID_FIELD", c.ErrorCode);
    }

    [Fact]
    public void Auth_Failures_Are_Not_Recoverable()
    {
        var c = DynamicsFetchXmlErrorClassifier.Classify(
            401,
            new DynamicsCrmErrorParser.ParsedError { Code = "0x80040217", Message = "Unauthorized" });

        Assert.False(c.IsRecoverable);
        Assert.Equal("AUTHORIZATION_DENIED", c.ErrorCode);
    }

    [Fact]
    public void Server_Failures_Are_Not_Recoverable()
    {
        var c = DynamicsFetchXmlErrorClassifier.Classify(
            503,
            new DynamicsCrmErrorParser.ParsedError { Message = "Service Unavailable" });

        Assert.False(c.IsRecoverable);
        Assert.Equal("CRM_SERVICE_FAILURE", c.ErrorCode);
    }

    [Fact]
    public void Parser_Prefers_Inner_Error_Message()
    {
        var body = """
            {"error":{"code":"0x80040203","message":"Invalid Argument","innererror":{"message":"Attribute 'x' does not exist on entity salesorder."}}}
            """;
        var parsed = DynamicsCrmErrorParser.Parse(body);
        Assert.Equal("80040203", parsed.Code);
        Assert.Contains("Attribute 'x'", parsed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AggregateQueryRecordLimit_Is_Not_InvalidAggregateField_And_Not_GroqRecoverable()
    {
        var parsed = DynamicsCrmErrorParser.Parse(
            """{"error":{"code":"0x8004E023","message":"AggregateQueryRecordLimit exceeded. Cannot perform this operation."}}""");

        var c = DynamicsFetchXmlErrorClassifier.Classify(400, parsed);

        Assert.True(parsed.IsAggregateQueryRecordLimit);
        Assert.False(c.IsRecoverable);
        Assert.Equal("AGGREGATE_RECORD_LIMIT_EXCEEDED", c.ErrorCode);
    }
}
