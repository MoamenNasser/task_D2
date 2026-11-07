using LoyaltyPoints.Api.Validation;

namespace LoyaltyPoints.Api.Wallets;

/// <summary>An accepted award, as stored and returned to the partner.</summary>
/// <param name="AwardId">Server-assigned, unique per accepted award ("AW-1", ...).</param>
/// <param name="SourceId">Copied from the request.</param>
/// <param name="ExternalTransactionId">Copied from the request.</param>
/// <param name="WalletId">Wallet that was credited.</param>
/// <param name="Points">Points that were credited.</param>
public sealed record Award(string AwardId, string SourceId, string ExternalTransactionId, string WalletId, int Points);

/// <summary>A wallet snapshot returned by <see cref="WalletService.GetWallet"/>.</summary>
/// <param name="WalletId">Wallet identifier.</param>
/// <param name="Balance">Current balance in points.</param>
public sealed record Wallet(string WalletId, long Balance);

/// <summary>What <see cref="WalletService.AwardPoints"/> did with a request.</summary>
public enum AwardStatus
{
    /// <summary>New award accepted, balance increased. HTTP 201.</summary>
    Created,

    /// <summary>Same identity, same wallet and points: the original award is returned. HTTP 200.</summary>
    Replayed,

    /// <summary>Same identity, different wallet or points: nothing changed. HTTP 409.</summary>
    Conflict,

    /// <summary>New identity, unknown wallet: nothing changed, identity not consumed. HTTP 404.</summary>
    WalletNotFound,
}

/// <summary>Result of <see cref="WalletService.AwardPoints"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Award">The accepted award for Created/Replayed, the original one for Conflict, null for WalletNotFound.</param>
public sealed record AwardOutcome(AwardStatus Status, Award? Award);

/// <summary>
/// Thread-safe in-memory store of wallets and awards: the business rules of the service.
/// Knows nothing about HTTP or JSON.
/// </summary>
/// <remarks>
/// A single lock guards both dictionaries, so the whole
/// "look up identity -> compare -> store award -> credit wallet" sequence is atomic. That is what makes
/// concurrent retries of the same award produce exactly one <see cref="AwardStatus.Created"/>.
/// </remarks>
public sealed class WalletService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _balances;

    // (SourceId, ExternalTransactionId). A real tuple, so ("A_", "B") and ("A", "_B") never collide;
    // ValueTuple equality uses ordinal (case-sensitive) string comparison.
    private readonly Dictionary<(string SourceId, string ExternalTransactionId), Award> _awards = new();
    private long _nextAwardNumber = 1;

    /// <summary>Creates the service with the wallets that exist from the start.</summary>
    /// <param name="initialBalances">Wallet id to starting balance.</param>
    public WalletService(IReadOnlyDictionary<string, long> initialBalances)
    {
        _balances = new Dictionary<string, long>(initialBalances, StringComparer.Ordinal);
    }

    /// <summary>Initial state from the brief: W100 = 100, W200 = 50, no awards.</summary>
    public static IReadOnlyDictionary<string, long> DefaultWallets() =>
        new Dictionary<string, long>(StringComparer.Ordinal) { ["W100"] = 100, ["W200"] = 50 };

    /// <summary>
    /// Applies an award idempotently, keyed by (sourceId, externalTransactionId).
    /// An existing identity is compared first (Replayed / Conflict); only a new identity checks the wallet
    /// (WalletNotFound / Created). Only Created changes state.
    /// </summary>
    /// <param name="request">A request that already passed <see cref="AwardRequestValidator.Parse"/>.</param>
    public AwardOutcome AwardPoints(AwardRequest request)
    {
        lock (_gate)
        {
            var key = (request.SourceId, request.ExternalTransactionId);
            if (_awards.TryGetValue(key, out var original))
            {
                var matches = original.WalletId == request.WalletId && original.Points == request.Points;
                return new AwardOutcome(matches ? AwardStatus.Replayed : AwardStatus.Conflict, original);
            }

            if (!_balances.TryGetValue(request.WalletId, out var balance))
            {
                return new AwardOutcome(AwardStatus.WalletNotFound, null);
            }

            var award = new Award($"AW-{_nextAwardNumber++}", request.SourceId, request.ExternalTransactionId,
                request.WalletId, request.Points);
            // Store first: adding to the dictionary can throw (allocation), the addition cannot. If storing
            // fails, the balance is untouched and the partner's retry starts clean.
            _awards.Add(key, award);
            _balances[request.WalletId] = balance + request.Points;
            return new AwardOutcome(AwardStatus.Created, award);
        }
    }

    /// <summary>Reads a wallet's current balance.</summary>
    /// <param name="walletId">Exact (case-sensitive) wallet id.</param>
    /// <returns>The wallet, or null if it does not exist.</returns>
    public Wallet? GetWallet(string walletId)
    {
        lock (_gate)
        {
            return _balances.TryGetValue(walletId, out var balance) ? new Wallet(walletId, balance) : null;
        }
    }
}
