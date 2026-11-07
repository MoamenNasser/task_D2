using LoyaltyPoints.Api.Validation;
using LoyaltyPoints.Api.Wallets;
using Microsoft.AspNetCore.WebUtilities;

namespace LoyaltyPoints.Api.Http;

/// <summary>Body of every error response.</summary>
/// <param name="Error">Stable machine-readable code, e.g. <c>invalid_request</c>.</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="Details">Per-field problems for validation errors; omitted otherwise.</param>
public sealed record ErrorBody(string Error, string Message, IReadOnlyList<FieldError>? Details = null);

/// <summary>Response body of <c>GET /wallets/{walletId}</c>.</summary>
/// <param name="WalletId">Wallet identifier.</param>
/// <param name="Balance">Current balance in points.</param>
public sealed record WalletResponse(string WalletId, long Balance);

/// <summary>HTTP layer: routes, status-code mapping and JSON error bodies.</summary>
public static class ApiEndpoints
{
    /// <summary>Largest accepted request body; larger bodies get 413.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// Registers <c>POST /awards</c> and <c>GET /wallets/{walletId}</c>, plus JSON bodies for errors the
    /// handlers do not produce themselves (unknown route 404, wrong method 405, unhandled exception 500).
    /// </summary>
    /// <param name="app">The application to configure.</param>
    public static void MapLoyaltyApi(this WebApplication app)
    {
        app.UseExceptionHandler(errorApp => errorApp.Run(context =>
            WriteErrorAsync(context.Response, StatusCodes.Status500InternalServerError, "internal_error",
                "Unexpected server error.")));

        // Routing answers an unknown route with an empty 404 and a wrong method with an empty 405 (+ Allow header).
        // This fills in a JSON body for those; responses that already have a body are left alone.
        app.UseStatusCodePages(context =>
        {
            var response = context.HttpContext.Response;
            return response.StatusCode switch
            {
                StatusCodes.Status404NotFound => WriteErrorAsync(response, 404, "not_found", "Route not found."),
                StatusCodes.Status405MethodNotAllowed => WriteErrorAsync(response, 405, "method_not_allowed",
                    $"Use {response.Headers.Allow} for this path."),
                var status => WriteErrorAsync(response, status, "http_error", ReasonPhrases.GetReasonPhrase(status)),
            };
        });

        app.MapPost("/awards", PostAwardAsync);
        app.MapGet("/wallets/{walletId}", GetWallet);
    }

    private static async Task<IResult> PostAwardAsync(HttpRequest request, WalletService service)
    {
        var body = await ReadBodyAsync(request);
        if (body is null)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"Request body must be at most {MaxBodyBytes} bytes.");
        }

        var parsed = AwardRequestValidator.Parse(body);
        if (!parsed.IsValid)
        {
            var details = parsed.FieldErrors.Count > 0 ? parsed.FieldErrors : null;
            return Results.Json(new ErrorBody("invalid_request", parsed.Message, details),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var awardRequest = parsed.Request!;
        var outcome = service.AwardPoints(awardRequest);
        return outcome.Status switch
        {
            AwardStatus.Created => Results.Json(outcome.Award, statusCode: StatusCodes.Status201Created),
            AwardStatus.Replayed => Results.Json(outcome.Award, statusCode: StatusCodes.Status200OK),
            AwardStatus.Conflict => Error(StatusCodes.Status409Conflict, "award_conflict",
                $"An award for sourceId '{awardRequest.SourceId}' and externalTransactionId " +
                $"'{awardRequest.ExternalTransactionId}' already exists with a different walletId or points."),
            _ => Error(StatusCodes.Status404NotFound, "wallet_not_found",
                $"Wallet '{awardRequest.WalletId}' does not exist."),
        };
    }

    private static IResult GetWallet(string walletId, WalletService service)
    {
        // A malformed id can never name an existing wallet, so it is reported as not found.
        var wallet = AwardRequestValidator.IsValidIdentifier(walletId) ? service.GetWallet(walletId) : null;
        return wallet is null
            ? Error(StatusCodes.Status404NotFound, "wallet_not_found", $"Wallet '{walletId}' does not exist.")
            : Results.Json(new WalletResponse(wallet.WalletId, wallet.Balance));
    }

    /// <summary>Reads the body, or returns null when it is larger than <see cref="MaxBodyBytes"/>.</summary>
    /// <remarks>Enforced here rather than via Kestrel limits so it also applies under the in-memory test server.</remarks>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorBody(code, message), statusCode: status);

    private static Task WriteErrorAsync(HttpResponse response, int status, string code, string message)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new ErrorBody(code, message));
    }
}
