using LoyaltyPoints.Api.Validation;
using LoyaltyPoints.Api.Wallets;

namespace LoyaltyPoints.Tests;

public class WalletServiceTests
{
    private readonly WalletService _service = new(WalletService.DefaultWallets());

    private long Balance(string walletId) => _service.GetWallet(walletId)!.Balance;

    [Fact]
    public void StartsWithInitialWallets()
    {
        Assert.Equal(100, Balance("W100"));
        Assert.Equal(50, Balance("W200"));
        Assert.Null(_service.GetWallet("W300"));
        Assert.Null(_service.GetWallet("w100")); // case-sensitive
    }

    [Fact]
    public void NewAwardCreditsOnce()
    {
        var outcome = _service.AwardPoints(new AwardRequest("POS_A", "T1", "W100", 25));
        Assert.Equal(AwardStatus.Created, outcome.Status);
        Assert.False(string.IsNullOrEmpty(outcome.Award!.AwardId));
        Assert.Equal("W100", outcome.Award.WalletId);
        Assert.Equal(25, outcome.Award.Points);
        Assert.Equal(125, Balance("W100"));
    }

    [Fact]
    public void MatchingRetryReturnsOriginalWithoutCrediting()
    {
        var first = _service.AwardPoints(new AwardRequest("POS_A", "T1", "W100", 25));
        var retry = _service.AwardPoints(new AwardRequest("POS_A", "T1", "W100", 25));
        Assert.Equal(AwardStatus.Replayed, retry.Status);
        Assert.Equal(first.Award, retry.Award);
        Assert.Equal(125, Balance("W100"));
    }

    [Fact]
    public void SameTransactionIdFromOtherSourceIsSeparateAward()
    {
        var a = _service.AwardPoints(new AwardRequest("POS_A", "T1", "W100", 25));
        var b = _service.AwardPoints(new AwardRequest("POS_B", "T1", "W100", 25));
        Assert.Equal(AwardStatus.Created, b.Status);
        Assert.NotEqual(a.Award!.AwardId, b.Award!.AwardId);
        Assert.Equal(150, Balance("W100"));
    }

    [Fact]
    public void ConflictingRetryChangesNothing()
    {
        var first = _service.AwardPoints(new AwardRequest("POS_A", "T1", "W100", 25));

        Assert.Equal(AwardStatus.Conflict, _service.AwardPoints(new AwardRequest("POS_A", "T1", "W100", 30)).Status);
        Assert.Equal(AwardStatus.Conflict, _service.AwardPoints(new AwardRequest("POS_A", "T1", "W200", 25)).Status);
        Assert.Equal(AwardStatus.Conflict, _service.AwardPoints(new AwardRequest("POS_A", "T1", "W999", 25)).Status); // identity wins over 404

        Assert.Equal(125, Balance("W100"));
        Assert.Equal(50, Balance("W200"));
        var again = _service.AwardPoints(new AwardRequest("POS_A", "T1", "W100", 25));
        Assert.Equal(AwardStatus.Replayed, again.Status);
        Assert.Equal(first.Award, again.Award);
    }

    [Fact]
    public void UnknownWalletDoesNotConsumeIdentity()
    {
        Assert.Equal(AwardStatus.WalletNotFound, _service.AwardPoints(new AwardRequest("POS_A", "T1", "W999", 25)).Status);
        Assert.Equal(100, Balance("W100"));

        var corrected = _service.AwardPoints(new AwardRequest("POS_A", "T1", "W100", 40));
        Assert.Equal(AwardStatus.Created, corrected.Status);
        Assert.Equal(140, Balance("W100"));
    }

    [Fact]
    public void ConcurrencyIdenticalRequestsProduceOneAward()
    {
        const int threads = 64;
        var outcomes = RunOnThreadsTogether(threads, _ => _service.AwardPoints(new AwardRequest("POS_A", "T1", "W100", 25)));

        Assert.Equal(1, outcomes.Count(o => o.Status == AwardStatus.Created));
        Assert.Equal(threads - 1, outcomes.Count(o => o.Status == AwardStatus.Replayed));
        Assert.Single(outcomes.Select(o => o.Award!.AwardId).Distinct());
        Assert.Equal(125, Balance("W100"));
    }

    [Fact]
    public void ConcurrencyDistinctAwardsAllApplied()
    {
        const int threads = 64;
        var outcomes = RunOnThreadsTogether(threads, i => _service.AwardPoints(new AwardRequest("POS_A", $"T{i}", "W200", 10)));

        Assert.All(outcomes, o => Assert.Equal(AwardStatus.Created, o.Status));
        Assert.Equal(50 + 10 * threads, Balance("W200"));
    }

    // Starts one dedicated thread per call and releases them all at once with a Barrier.
    private static AwardOutcome[] RunOnThreadsTogether(int count, Func<int, AwardOutcome> action)
    {
        var results = new AwardOutcome[count];
        using var barrier = new Barrier(count);
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            barrier.SignalAndWait();
            results[i] = action(i);
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        return results;
    }
}
