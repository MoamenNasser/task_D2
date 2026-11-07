using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LoyaltyPoints.Tests;

/// <summary>
/// End-to-end tests over HTTP against the real app pipeline (WebApplicationFactory / TestServer).
/// xUnit creates a new instance per test, so every test gets a fresh app and fresh wallets.
/// </summary>
public class ApiTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public ApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private sealed record Reply(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response);

    private static string AwardBody(string source, string txn, string wallet, int points) =>
        JsonSerializer.Serialize(new { sourceId = source, externalTransactionId = txn, walletId = wallet, points });

    private static async Task<Reply> ToReplyAsync(HttpResponseMessage response)
    {
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString());
        var text = await response.Content.ReadAsStringAsync();
        return new Reply(response.StatusCode, JsonDocument.Parse(text).RootElement.Clone(), response);
    }

    private async Task<Reply> PostAsync(string body) =>
        await ToReplyAsync(await _client.PostAsync("/awards", new StringContent(body, Encoding.UTF8, "application/json")));

    private async Task<Reply> GetAsync(string path) => await ToReplyAsync(await _client.GetAsync(path));

    private async Task<long> BalanceAsync(string wallet) => (await GetAsync($"/wallets/{wallet}")).Body.GetProperty("balance").GetInt64();

    private static void AssertErrorBody(Reply reply)
    {
        Assert.Equal(JsonValueKind.Object, reply.Body.ValueKind);
        Assert.True(reply.Body.TryGetProperty("error", out _));
        Assert.False(string.IsNullOrEmpty(reply.Body.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task InitialState()
    {
        var w100 = await GetAsync("/wallets/W100");
        Assert.Equal(HttpStatusCode.OK, w100.Status);
        Assert.Equal("W100", w100.Body.GetProperty("walletId").GetString());
        Assert.Equal(100, w100.Body.GetProperty("balance").GetInt64());
        Assert.Equal(50, await BalanceAsync("W200"));
    }

    [Theory]
    [InlineData("/wallets/W999")]
    [InlineData("/wallets/w100")]
    [InlineData("/wallets/bad.id")]
    [InlineData("/wallets/%FF")]
    public async Task UnknownWalletLookupIs404(string path)
    {
        var reply = await GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, reply.Status);
        AssertErrorBody(reply);
    }

    [Fact]
    public async Task NewAwardReturns201AndCreditsWallet()
    {
        var reply = await PostAsync(AwardBody("POS_A", "T1", "W100", 25));
        Assert.Equal(HttpStatusCode.Created, reply.Status);
        Assert.False(string.IsNullOrEmpty(reply.Body.GetProperty("awardId").GetString()));
        Assert.Equal("POS_A", reply.Body.GetProperty("sourceId").GetString());
        Assert.Equal("T1", reply.Body.GetProperty("externalTransactionId").GetString());
        Assert.Equal("W100", reply.Body.GetProperty("walletId").GetString());
        Assert.Equal(25, reply.Body.GetProperty("points").GetInt32());
        Assert.Equal(125, await BalanceAsync("W100"));
    }

    [Fact]
    public async Task MatchingRetryReturns200WithOriginalRecord()
    {
        var first = await PostAsync(AwardBody("POS_A", "T1", "W100", 25));
        var retry = await PostAsync(AwardBody("POS_A", "T1", "W100", 25));
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.Equal(first.Body.GetRawText(), retry.Body.GetRawText());
        Assert.Equal(125, await BalanceAsync("W100"));
    }

    [Fact]
    public async Task SameTransactionIdDifferentSourceIsNewAward()
    {
        var a = await PostAsync(AwardBody("POS_A", "T1", "W100", 25));
        var b = await PostAsync(AwardBody("POS_B", "T1", "W100", 25));
        Assert.Equal(HttpStatusCode.Created, b.Status);
        Assert.NotEqual(a.Body.GetProperty("awardId").GetString(), b.Body.GetProperty("awardId").GetString());
        Assert.Equal(150, await BalanceAsync("W100"));
    }

    [Fact]
    public async Task ConflictingRetryReturns409AndChangesNothing()
    {
        var first = await PostAsync(AwardBody("POS_A", "T1", "W100", 25));

        foreach (var body in new[] { AwardBody("POS_A", "T1", "W100", 30), AwardBody("POS_A", "T1", "W200", 25), AwardBody("POS_A", "T1", "W999", 25) })
        {
            var reply = await PostAsync(body);
            Assert.Equal(HttpStatusCode.Conflict, reply.Status);
            AssertErrorBody(reply);
        }

        Assert.Equal(125, await BalanceAsync("W100"));
        Assert.Equal(50, await BalanceAsync("W200"));
        var again = await PostAsync(AwardBody("POS_A", "T1", "W100", 25));
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(first.Body.GetRawText(), again.Body.GetRawText());
    }

    [Fact]
    public async Task UnknownWalletReturns404AndIdentityStaysUsable()
    {
        var reply = await PostAsync(AwardBody("POS_A", "T1", "W999", 25));
        Assert.Equal(HttpStatusCode.NotFound, reply.Status);
        AssertErrorBody(reply);
        Assert.Equal(100, await BalanceAsync("W100"));

        var corrected = await PostAsync(AwardBody("POS_A", "T1", "W100", 25));
        Assert.Equal(HttpStatusCode.Created, corrected.Status);
        Assert.Equal(125, await BalanceAsync("W100"));
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("\"x\"")]
    [InlineData("null")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":25.0}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":"25"}""")]
    [InlineData("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":0}""")]
    [InlineData("""{"sourceId":"POS A","externalTransactionId":"T1","walletId":"W100","points":5}""")]
    [InlineData("""{"externalTransactionId":"T1","walletId":"W100","points":5}""")]
    public async Task InvalidBodiesReturn400WithJsonError(string body)
    {
        var reply = await PostAsync(body);
        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        AssertErrorBody(reply);
        Assert.Equal(100, await BalanceAsync("W100"));
    }

    [Fact]
    public async Task InvalidUtf8BodyReturns400Not500()
    {
        var bytes = Encoding.UTF8.GetBytes(AwardBody("X", "T1", "W100", 5));
        bytes[13] = 0xFF; // replaces the "X" inside the sourceId string
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        var reply = await ToReplyAsync(await _client.PostAsync("/awards", content));
        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        AssertErrorBody(reply);
        Assert.Equal(100, await BalanceAsync("W100"));
    }

    [Fact]
    public async Task ValidationRunsBeforeIdentityAndWalletChecks()
    {
        await PostAsync(AwardBody("POS_A", "T1", "W100", 25));
        // Existing identity, but invalid points: must be 400, not 200 or 409.
        var reply = await PostAsync("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":5000}""");
        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal("points", reply.Body.GetProperty("details")[0].GetProperty("field").GetString());
        // Unknown wallet and invalid points: 400, not 404.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("""{"sourceId":"POS_A","externalTransactionId":"T2","walletId":"W999","points":0}""")).Status);
    }

    [Fact]
    public async Task DuplicateKeysReturn400()
    {
        var reply = await PostAsync("""{"sourceId":"POS_A","externalTransactionId":"T1","walletId":"W100","points":5,"points":500}""");
        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        AssertErrorBody(reply);
        Assert.Equal(100, await BalanceAsync("W100"));
    }

    [Fact]
    public async Task OversizedBodyReturns413()
    {
        var reply = await PostAsync(new string('a', 70_000));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, reply.Status);
        AssertErrorBody(reply);
    }

    [Fact]
    public async Task WrongMethodReturns405WithAllowHeader()
    {
        var putAwards = await ToReplyAsync(await _client.PutAsync("/awards", new StringContent("{}", Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, putAwards.Status);
        Assert.Equal("POST", string.Join(",", putAwards.Response.Content.Headers.Allow));
        AssertErrorBody(putAwards);

        var getAwards = await ToReplyAsync(await _client.GetAsync("/awards"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getAwards.Status);

        var postWallet = await ToReplyAsync(await _client.PostAsync("/wallets/W100", new StringContent("{}", Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postWallet.Status);
        Assert.Equal("GET", string.Join(",", postWallet.Response.Content.Headers.Allow));
        Assert.Equal(100, await BalanceAsync("W100"));
    }

    [Fact]
    public async Task UnknownRouteReturnsJson404()
    {
        var reply = await GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, reply.Status);
        AssertErrorBody(reply);
    }
}
