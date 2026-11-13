using System.Text.Json;
using Bachs.Clients;
using Bachs.Common;
using Bachs.Extensions;
using Bachs.Models.Checkout;
using Bachs.Models.Common;
using Bachs.Serialization;
using Bachs.WebHooks;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddBachsClient(options =>
{
    options.ApiKey = builder.Configuration["Bachs:ApiKey"]!;
    options.WebhookSecret = builder.Configuration["Bachs:WebhookSecret"]!;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing",
    "Bracing",
    "Chilly",
    "Cool",
    "Mild",
    "Warm",
    "Balmy",
    "Hot",
    "Sweltering",
    "Scorching",
};

app.MapGet(
        "/weatherforecast",
        () =>
        {
            var forecast = Enumerable
                .Range(1, 5)
                .Select(index => new WeatherForecast(
                    DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    Random.Shared.Next(-20, 55),
                    summaries[Random.Shared.Next(summaries.Length)]
                ))
                .ToArray();
            return forecast;
        }
    )
    .WithName("GetWeatherForecast");

var rand = Random.Shared.Next(1, 20);
app.MapPost(
        "/checkout",
        async (HttpRequest request, ICheckoutSessionsClient checkoutClient) =>
        {
            var checkoutRequest = CreateCheckoutSessionRequestBuilder
                .WithPricing(2000, BachsCurrency.NGN)
                .WithCustomer(email: "john.doe@example.com", name: "John Doe")
                .WithReference($"order_12{rand}")
                .WithCancelUrl("https://example.com/cancel")
                .WithSuccessUrl("http://example.com/success")
                .Build();

            var options = new BachsRequestOptions("strinhg766");
            var checkoutSession = await checkoutClient.CreateAsync(checkoutRequest, options);

            return Results.Ok(checkoutSession);
        }
    )
    .WithName("BachsWebhook");

app.MapGet(
        "/checkout",
        () =>
        {
            List<CurrencyResponse> CurrencyOptions =
            [
                .. Enumerable
                    .Range(1, 5)
                    .Select(s => new CurrencyResponse(BachsCurrency.GHS, s * 2.98m)),
            ];

            var sr = BachsJsonSerializer.Serialize(new ToSerialize(CurrencyOptions));

            var bachs = BachsJsonSerializer.Deserialize<ToSerialize>(sr);
            var systemText = JsonSerializer.Deserialize<ToSerialize>(
                sr,
                BachsJsonSerializer.CreateDefaultOptions()
            );

            return Results.Ok(new { bachs, systemText });
        }
    )
    .WithName("GetCheckoutSession");

app.MapPost(
    "/webhooks",
    async (IWebhooksClient webhooksClient, HttpRequest request, ILogger<ToSerialize> logger) =>
    {
        try
        {
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            var rawPayload = ms.ToArray();

            // Prefer V2 signature, fall back to V1
            var signature = request.Headers["X-Bachs-Signature-V2"].ToString();
            if (string.IsNullOrEmpty(signature))
                signature = request.Headers["X-Bachs-Signature"].ToString();

            var timestamp = request.Headers["X-Bachs-Timestamp"].ToString();

            if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp))
            {
                // Handle
                return Results.Problem(statusCode: 400);
            }
            var isSucces = webhooksClient.TryValidateTypedEvent(
                rawPayload,
                signatureHeader: signature,
                out var typedEvent,
                timestampHeader: timestamp
            );
            if (!isSucces || typedEvent is null)
            {
                return Results.Ok();
            }

            object data = typedEvent switch
            {
                CollectionSucceededEvent e => e.Data, // e.Data is strongly-typed Payment
                CollectionFailedEvent e => e.Data,
                CheckoutCompletedEvent e => e.Data, // e.Data is GetCheckoutSessionResponse
                CheckoutExpiredEvent e => e.Data,
                PayoutPaidEvent e => e.Data, // e.Data is Payout
                PayoutCreatedEvent e => e.Data,
                PayoutFailedEvent e => e.Data,
                SubscriptionCreatedEvent e => e.Data, // e.Data is Subscription
                InvoicePaidEvent e => e.Data, // e.Data is InvoiceEventData
                PaymentMethodSavedEvent e => e.Data,
                UnknownBachsEvent e => new { Name = "Amaechi", Type = "unknown" }, // Forward-compatible fallback
                _ => new { Name = "Amaechi", Type = "default" },
            };
            var serializedEvent = BachsJsonSerializer.Serialize(
                new { Type = nameof(data), Event = typedEvent }
            );
            var eventFilePath = Path.Combine(
                app.Environment.ContentRootPath,
                $"{typedEvent.Type}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"
            );
            await File.WriteAllTextAsync(eventFilePath, serializedEvent);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "an error occured. {}", ex.Message);
            var eventFilePath = Path.Combine(
                app.Environment.ContentRootPath,
                $"error-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"
            );
            await File.WriteAllTextAsync(eventFilePath, ex.Message);
        }
        return Results.Ok();
    }
);

app.MapGet(
    "check",
    async (ICheckoutSessionsClient client) =>
    {
        var response = await client.GetAsync("chk_BWg0dslRG2PJ3PJU");
        return Results.Ok(response.Data);
    }
);
app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

record CurrencyResponse(BachsCurrency Currency, BachsPrice Price);

record ToSerialize(List<CurrencyResponse> Data);
