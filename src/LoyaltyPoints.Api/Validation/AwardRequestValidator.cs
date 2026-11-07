using System.Text.Json;
using System.Text.Unicode;

namespace LoyaltyPoints.Api.Validation;

/// <summary>A fully validated award request. Only produced by <see cref="AwardRequestValidator.Parse"/>.</summary>
/// <param name="SourceId">Partner/system that issued the transaction.</param>
/// <param name="ExternalTransactionId">Partner's transaction id; unique per source.</param>
/// <param name="WalletId">Wallet to credit.</param>
/// <param name="Points">Points to add, between <see cref="AwardRequestValidator.MinPoints"/> and <see cref="AwardRequestValidator.MaxPoints"/>.</param>
public sealed record AwardRequest(string SourceId, string ExternalTransactionId, string WalletId, int Points);

/// <summary>One problem with one field, e.g. <c>("points", "must be an integer")</c>.</summary>
/// <param name="Field">JSON field name as sent by the client.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record FieldError(string Field, string Message);

/// <summary>Outcome of <see cref="AwardRequestValidator.Parse"/>: either a request or a description of what is wrong.</summary>
public sealed class ValidationResult
{
    /// <summary>Set only when the body is fully valid.</summary>
    public AwardRequest? Request { get; init; }

    /// <summary>Summary of what is wrong (empty when valid).</summary>
    public string Message { get; init; } = "";

    /// <summary>Per-field problems; empty for body-level errors such as malformed JSON.</summary>
    public IReadOnlyList<FieldError> FieldErrors { get; init; } = Array.Empty<FieldError>();

    /// <summary>True when the body was valid and <see cref="Request"/> is set.</summary>
    public bool IsValid => Request is not null;
}

/// <summary>
/// Parses and validates <c>POST /awards</c> bodies. Pure: never reads or changes wallet/award state,
/// which is what lets the endpoint validate before checking existing awards or wallets.
/// </summary>
/// <remarks>
/// The body is parsed by hand with <see cref="JsonDocument"/> instead of ASP.NET model binding, because the
/// web defaults would accept <c>"points": "25"</c> (numbers from strings) and match property names case-insensitively.
/// </remarks>
public static class AwardRequestValidator
{
    /// <summary>Smallest accepted <c>points</c> value.</summary>
    public const int MinPoints = 1;

    /// <summary>Largest accepted <c>points</c> value.</summary>
    public const int MaxPoints = 1000;

    /// <summary>Maximum identifier length in characters.</summary>
    public const int MaxIdLength = 64;

    /// <summary>Checks the identifier format shared by sourceId, externalTransactionId and walletId.</summary>
    /// <param name="value">Candidate identifier.</param>
    /// <returns>True if the value is 1-64 characters, each an ASCII letter, digit, <c>_</c> or <c>-</c>.</returns>
    public static bool IsValidIdentifier(string? value) =>
        value is { Length: >= 1 and <= MaxIdLength } && value.All(IsIdChar);

    /// <summary>
    /// Validates a raw request body. Checks, in order: JSON syntax, top-level object, no duplicate keys,
    /// then every field (all field errors are collected, not just the first). Types are strict:
    /// <c>25.0</c>, <c>1e2</c>, <c>"25"</c> and <c>true</c> are not integers. Unknown extra fields are ignored.
    /// </summary>
    /// <param name="body">Raw UTF-8 request body.</param>
    /// <returns>A result with <see cref="ValidationResult.Request"/> set on success, or an error description.</returns>
    public static ValidationResult Parse(ReadOnlyMemory<byte> body)
    {
        // JsonDocument accepts invalid UTF-8 inside strings and only fails later, in GetString(), with an
        // InvalidOperationException. Checking the whole body first turns that into a plain 400.
        if (!Utf8.IsValid(body.Span))
        {
            return new ValidationResult { Message = "Request body is not valid JSON." };
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new ValidationResult { Message = "Request body is not valid JSON." };
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ValidationResult { Message = "Request body must be a JSON object." };
            }

            // JsonDocument keeps every repeated key; {"points":5,"points":500} is ambiguous (parsers disagree on
            // which one wins), so reject the body instead of picking one.
            if (FindDuplicateKey(root) is { } duplicate)
            {
                return new ValidationResult
                {
                    Message = "Request body contains a duplicate field.",
                    FieldErrors = new[] { new FieldError(duplicate, "is specified more than once") },
                };
            }

            var errors = new List<FieldError>();
            var sourceId = ReadIdentifier(root, "sourceId", errors);
            var externalTransactionId = ReadIdentifier(root, "externalTransactionId", errors);
            var walletId = ReadIdentifier(root, "walletId", errors);
            var points = ReadPoints(root, errors);

            if (errors.Count > 0)
            {
                return new ValidationResult { Message = "One or more fields are invalid.", FieldErrors = errors };
            }

            return new ValidationResult
            {
                Request = new AwardRequest(sourceId!, externalTransactionId!, walletId!, points!.Value),
            };
        }
    }

    private static bool IsIdChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';

    private static string? ReadIdentifier(JsonElement root, string field, List<FieldError> errors)
    {
        // TryGetProperty is an ordinal (case-sensitive) match, so "sourceid" counts as missing.
        if (!root.TryGetProperty(field, out var value))
        {
            errors.Add(new FieldError(field, "is required"));
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new FieldError(field, "must be a string"));
            return null;
        }
        var text = value.GetString();
        if (!IsValidIdentifier(text))
        {
            errors.Add(new FieldError(field, "must be 1-64 characters, using only ASCII letters, digits, '_' or '-'"));
            return null;
        }
        return text;
    }

    private static int? ReadPoints(JsonElement root, List<FieldError> errors)
    {
        if (!root.TryGetProperty("points", out var value))
        {
            errors.Add(new FieldError("points", "is required"));
            return null;
        }
        // Only an integer literal counts: 25.0 and 1e2 are numbers but not integers, "25" and true are not numbers.
        var raw = value.GetRawText();
        if (value.ValueKind != JsonValueKind.Number || raw.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0)
        {
            errors.Add(new FieldError("points", "must be an integer"));
            return null;
        }
        // Integers too large for long fail TryGetInt64; they are out of range either way.
        if (!value.TryGetInt64(out var number) || number < MinPoints || number > MaxPoints)
        {
            errors.Add(new FieldError("points", $"must be between {MinPoints} and {MaxPoints}"));
            return null;
        }
        return (int)number;
    }

    /// <summary>Returns the first key that appears twice within the same object, searching nested values too.</summary>
    private static string? FindDuplicateKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        return property.Name;
                    }
                    if (FindDuplicateKey(property.Value) is { } nested)
                    {
                        return nested;
                    }
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindDuplicateKey(item) is { } nested)
                    {
                        return nested;
                    }
                }
                return null;
            default:
                return null;
        }
    }
}
