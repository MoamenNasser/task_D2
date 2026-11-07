using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LoyaltyPoints.Tests;

/// <summary>
/// Concurrent HTTP requests against the app. All requests wait on one gate and are released together,
/// so they really overlap inside the server. The assertions are exact counts, not "at least one".
/// </summary>
public class ConcurrencyTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public ConcurrencyTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static StringContent AwardContent(string source, string txn, string wallet, int points) =>
        new(JsonSerializer.Serialize(new { sourceId = source, externalTransactionId = txn, walletId = wallet, points }),
            Encoding.UTF8, "application/json");

    private async Task<long> BalanceAsync(string wallet)
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync($"/wallets/{wallet}"));
        return doc.RootElement.GetProperty("balance").GetInt64();
    }

    // Queues every request on the thread pool behind one gate, then opens the gate so they all start together.
    private static async Task<T[]> RunTogetherAsync<T>(int count, Func<int, Task<T>> request)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await request(i);
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsProduceOne201()
    {
        const int clients = 32;
        var replies = await RunTogetherAsync(clients, async _ =>
        {
            var response = await _client.PostAsync("/awards", AwardContent("POS_A", "T1", "W100", 25));
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (response.StatusCode, AwardId: doc.RootElement.GetProperty("awardId").GetString());
        });

        Assert.Equal(1, replies.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(clients - 1, replies.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Single(replies.Select(r => r.AwardId).Distinct());
        Assert.Equal(125, await BalanceAsync("W100"));
    }

    [Fact]
    public async Task ConcurrentDistinctAwardsAllReflectedInBalance()
    {
        const int clients = 32;
        var statuses = await RunTogetherAsync(clients, async i =>
            (await _client.PostAsync("/awards", AwardContent("POS_A", $"T{i}", "W200", 7))).StatusCode);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Created, s));
        Assert.Equal(50 + 7 * clients, await BalanceAsync("W200"));
    }

    [Fact]
    public async Task ConcurrentSameIdentityDifferentPointsGivesOneAcceptAndOneConflict()
    {
        const int rounds = 25;
        long expectedBalance = 100;
        for (var round = 0; round < rounds; round++)
        {
            var txn = $"RACE-{round}";
            var statuses = await RunTogetherAsync(2, async i =>
                (await _client.PostAsync("/awards", AwardContent("POS_A", txn, "W100", i == 0 ? 10 : 20))).StatusCode);

            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, statuses.OrderBy(s => s).ToArray());
            expectedBalance += statuses[0] == HttpStatusCode.Created ? 10 : 20;
        }
        Assert.Equal(expectedBalance, await BalanceAsync("W100"));
    }
}
