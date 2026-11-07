using System.Text;
using LoyaltyPoints.Api.Validation;

namespace LoyaltyPoints.Tests;

public class AwardRequestValidatorTests
{
    private const string ValidBody =
        """{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":25}""";

    private static ValidationResult Parse(string body) => AwardRequestValidator.Parse(Encoding.UTF8.GetBytes(body));

    // Replaces one field's raw JSON value in the valid body.
    private static string BodyWith(string field, string rawJsonValue)
    {
        var needle = $"\"{field}\":";
        var start = ValidBody.IndexOf(needle, StringComparison.Ordinal) + needle.Length;
        var end = ValidBody.IndexOfAny(new[] { ',', '}' }, start);
        return ValidBody[..start] + rawJsonValue + ValidBody[end..];
    }

    private static bool HasFieldError(ValidationResult result, string field) =>
        result.FieldErrors.Any(e => e.Field == field);

    [Fact]
    public void AcceptsValidRequest()
    {
        var result = Parse(ValidBody);
        Assert.True(result.IsValid);
        Assert.Equal(new AwardRequest("POS_A", "T1", "W100", 25), result.Request);
    }

    [Fact]
    public void IgnoresUnknownFields()
    {
        Assert.True(Parse("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":25,"note":"x"}""").IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{bad json")]
    [InlineData("""{"sourceId":"A",}""")]
    [InlineData("{} trailing")]
    public void RejectsMalformedJson(string body)
    {
        var result = Parse(body);
        Assert.False(result.IsValid);
        Assert.Empty(result.FieldErrors);
        Assert.Equal("Request body is not valid JSON.", result.Message);
    }

    [Fact]
    public void RejectsInvalidUtf8()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"sourceId":"X","externalTransactionId":"T1","walletId":"W100","points":5}""");
        bytes[13] = 0xFF; // replaces the "X" inside the sourceId string
        Assert.Equal("Request body is not valid JSON.", AwardRequestValidator.Parse(bytes).Message);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    public void RejectsNonObjectBodies(string body)
    {
        var result = Parse(body);
        Assert.False(result.IsValid);
        Assert.Equal("Request body must be a JSON object.", result.Message);
    }

    [Fact]
    public void RejectsMissingFields()
    {
        var result = Parse("{}");
        Assert.False(result.IsValid);
        Assert.Equal(4, result.FieldErrors.Count);
    }

    [Fact]
    public void IdentifierBoundaries()
    {
        Assert.True(AwardRequestValidator.IsValidIdentifier("a"));
        Assert.True(AwardRequestValidator.IsValidIdentifier(new string('x', 64)));
        Assert.True(AwardRequestValidator.IsValidIdentifier("Az09_-"));
        Assert.False(AwardRequestValidator.IsValidIdentifier(""));
        Assert.False(AwardRequestValidator.IsValidIdentifier(null));
        Assert.False(AwardRequestValidator.IsValidIdentifier(new string('x', 65)));
        foreach (var bad in new[] { "has space", "dot.id", "slash/id", "tab\t", "new\nline", "café", "a+b", "٣" })
        {
            Assert.False(AwardRequestValidator.IsValidIdentifier(bad), bad);
        }
    }

    public static IEnumerable<object[]> BadIdentifierCases()
    {
        var badValues = new[] { "\"\"", "\"a b\"", $"\"{new string('x', 65)}\"", "123", "null", "true", """["W100"]""", """{"id":"W100"}""" };
        foreach (var field in new[] { "sourceId", "externalTransactionId", "walletId" })
        {
            foreach (var value in badValues)
            {
                yield return new object[] { field, value };
            }
        }
    }

    [Theory]
    [MemberData(nameof(BadIdentifierCases))]
    public void RejectsBadIdentifierValues(string field, string rawValue)
    {
        var result = Parse(BodyWith(field, rawValue));
        Assert.False(result.IsValid);
        Assert.True(HasFieldError(result, field));
    }

    [Fact]
    public void IdentifiersAreCaseSensitiveAndKeptAsIs()
    {
        var result = Parse("""{"sourceId":"pos_a","externalTransactionId":"t1","walletId":"w100","points":1}""");
        Assert.True(result.IsValid);
        Assert.Equal("pos_a", result.Request!.SourceId);
        Assert.Equal("w100", result.Request.WalletId);
    }

    [Fact]
    public void FieldNamesAreCaseSensitive()
    {
        var result = Parse("""{"SourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":1}""");
        Assert.False(result.IsValid);
        Assert.True(HasFieldError(result, "sourceId"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1000")]
    public void PointsRangeAcceptsBoundaries(string raw) => Assert.True(Parse(BodyWith("points", raw)).IsValid);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0")]
    [InlineData("1001")]
    [InlineData("18446744073709551615")]
    [InlineData("-9223372036854775808")]
    [InlineData("99999999999999999999999999")]
    public void PointsRangeRejectsOutOfRange(string raw)
    {
        var result = Parse(BodyWith("points", raw));
        Assert.False(result.IsValid);
        Assert.Contains(result.FieldErrors, e => e.Field == "points" && e.Message == "must be between 1 and 1000");
    }

    [Theory]
    [InlineData("25.0")]
    [InlineData("25.5")]
    [InlineData("1e2")]
    [InlineData("1E2")]
    [InlineData("\"25\"")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("[25]")]
    [InlineData("""{"v":25}""")]
    public void PointsMustBeAJsonInteger(string raw)
    {
        var result = Parse(BodyWith("points", raw));
        Assert.False(result.IsValid);
        Assert.Contains(result.FieldErrors, e => e.Field == "points" && e.Message == "must be an integer");
    }

    [Fact]
    public void ReportsEveryInvalidField()
    {
        var result = Parse("""{"sourceId":"","externalTransactionId":5,"walletId":"W 1","points":"x"}""");
        Assert.False(result.IsValid);
        Assert.Equal(4, result.FieldErrors.Count);
    }

    [Fact]
    public void RejectsDuplicateKeys()
    {
        var result = Parse("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":5,"points":500}""");
        Assert.False(result.IsValid);
        var error = Assert.Single(result.FieldErrors);
        Assert.Equal("points", error.Field);

        // Nested objects track their own keys; the same name at different levels is fine.
        Assert.True(Parse("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":5,"meta":{"points":1}}""").IsValid);
        Assert.False(Parse("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":5,"meta":{"a":1,"a":2}}""").IsValid);
    }
}
