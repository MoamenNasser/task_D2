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

    /// <summary>Handles <c>POST /awards</c>: read the body, validate it, apply the award, map the outcome to HTTP.</summary>
    /// <remarks>Validation runs before the service is called, so an invalid body never reaches the award or wallet checks.</remarks>
    /// <param name="request">The incoming request; only its body is read.</param>
    /// <param name="service">The singleton wallet service, injected by DI.</param>
    /// <returns>201 or 200 with the award record; 400, 404, 409 or 413 with an <see cref="ErrorBody"/>.</returns>
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

    /// <summary>Handles <c>GET /wallets/{walletId}</c>.</summary>
    /// <param name="walletId">Wallet id from the route, compared case-sensitively.</param>
    /// <param name="service">The singleton wallet service, injected by DI.</param>
    /// <returns>200 with a <see cref="WalletResponse"/>, or 404 with an <see cref="ErrorBody"/>.</returns>
    private static IResult GetWallet(string walletId, WalletService service)
    {
        // A malformed id can never name an existing wallet, so it is reported as not found.
        var wallet = AwardRequestValidator.IsValidIdentifier(walletId) ? service.GetWallet(walletId) : null;
        return wallet is null
            ? Error(StatusCodes.Status404NotFound, "wallet_not_found", $"Wallet '{walletId}' does not exist.")
            : Results.Json(new WalletResponse(wallet.WalletId, wallet.Balance));
    }

    /// <summary>Reads the body, or returns null when it is larger than <see cref="MaxBodyBytes"/>.</summary>
    /// <remarks>Enforced here rather than via Kestrel limits so it also applies under the in-memory test server.
    /// Checks <c>Content-Length</c> first, then caps the bytes actually read (for chunked bodies with no length).</remarks>
    /// <param name="request">The incoming request.</param>
    /// <returns>The body bytes, or null if the body is over the limit.</returns>
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

    /// <summary>Builds an error result for a handler to return.</summary>
    /// <param name="status">HTTP status code.</param>
    /// <param name="code">Machine-readable error code, e.g. <c>wallet_not_found</c>.</param>
    /// <param name="message">Human-readable explanation.</param>
    /// <returns>A JSON <see cref="ErrorBody"/> result with the given status.</returns>
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorBody(code, message), statusCode: status);

    /// <summary>Writes an error body straight to the response, for middleware that has no handler result to return.</summary>
    /// <remarks>Used by the status-code-pages and exception handlers.</remarks>
    /// <param name="response">Response to write to.</param>
    /// <param name="status">HTTP status code.</param>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Human-readable explanation.</param>
    /// <returns>A task that completes when the body is written.</returns>
    private static Task WriteErrorAsync(HttpResponse response, int status, string code, string message)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new ErrorBody(code, message));
    }
}
