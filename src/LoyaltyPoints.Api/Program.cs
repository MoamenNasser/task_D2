using System.Text.Json.Serialization;
using LoyaltyPoints.Api.Http;
using LoyaltyPoints.Api.Wallets;

var builder = WebApplication.CreateBuilder(args);

// Error bodies omit "details" when there are none.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

// State lives only in this process; restarting it resets to the initial wallets.
builder.Services.AddSingleton(_ => new WalletService(WalletService.DefaultWallets()));

var app = builder.Build();
app.MapLoyaltyApi();
app.Run();

/// <summary>Entry point; declared partial so the integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program { }
