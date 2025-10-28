using Stripe;
using Stripe.Checkout;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
StripeConfiguration.ApiKey = "sk_test_51SL2gARssIVs1btqjLpWHEhPSNq7y2RJO7XaE4koRBOG8410SNuPWoImtgXQyqqWIP6jvuyNJAc4Hk2Y9nnmKBew009XjIpe8X";

var app = builder.Build();

// Tillad at statiske filer (HTML, CSS, JS, billeder osv.) kan hentes direkte
app.UseDefaultFiles(); // gør at index.html vises automatisk
app.UseStaticFiles();  // gør at stole.html, style.css, images osv. bliver fundet

app.UseHttpsRedirection();

app.MapPost("/api/create-checkout-session", async (HttpContext ctx) =>
{
    try
    {
        // Robust læsning af request body (undgår dobbelt-læsning problemer)
        ctx.Request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(ctx.Request.Body, leaveOpen: true))
        {
            ctx.Request.Body.Position = 0;
            rawBody = await reader.ReadToEndAsync();
            ctx.Request.Body.Position = 0; // rewind så andre kan læse hvis nødvendigt
        }

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("Empty body.");
            Console.WriteLine("⚠️ Tom body modtaget ved /api/create-checkout-session");
            return;
        }

        Console.WriteLine("Raw JSON from frontend:");
        Console.WriteLine(rawBody);

        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("productData", out var productsEl) || productsEl.ValueKind != JsonValueKind.Array)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("Invalid payload: 'productData' array is required.");
            return;
        }

        var lineItems = new List<SessionLineItemOptions>();

        foreach (var p in productsEl.EnumerateArray())
        {
            string productName = p.TryGetProperty("productName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()!
                : null;

            if (string.IsNullOrWhiteSpace(productName))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("Each product must include a non-empty 'productName'.");
                return;
            }

            int quantity = 1;
            if (p.TryGetProperty("amount", out var qtyEl))
            {
                if (qtyEl.ValueKind == JsonValueKind.Number && qtyEl.TryGetInt32(out var q))
                    quantity = Math.Max(1, q);
                else if (qtyEl.ValueKind == JsonValueKind.String && int.TryParse(qtyEl.GetString(), out var qs))
                    quantity = Math.Max(1, qs);
                else
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync($"Invalid 'amount' for product '{productName}'.");
                    return;
                }
            }
            else
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync($"Missing 'amount' (quantity) for product '{productName}'.");
                return;
            }

            long unitAmountInOere;
            if (p.TryGetProperty("price", out var priceEl))
            {
                if (priceEl.ValueKind == JsonValueKind.Number && priceEl.TryGetDouble(out var priceDouble))
                {
                    unitAmountInOere = (long)Math.Round(priceDouble * 100.0);
                }
                else if (priceEl.ValueKind == JsonValueKind.String && double.TryParse(priceEl.GetString(), out var priceParsed))
                {
                    unitAmountInOere = (long)Math.Round(priceParsed * 100.0);
                }
                else
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync($"Invalid 'price' for product '{productName}'.");
                    return;
                }
            }
            else
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync($"Missing 'price' for product '{productName}'.");
                return;
            }

            var currency = p.TryGetProperty("currency", out var curEl) && curEl.ValueKind == JsonValueKind.String
                ? curEl.GetString()!.ToLowerInvariant()
                : "dkk";

            var li = new SessionLineItemOptions
            {
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = currency,
                    UnitAmount = unitAmountInOere,
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = productName
                    }
                },
                Quantity = quantity
            };

            lineItems.Add(li);
        }

        if (lineItems.Count == 0)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("No products found in 'productData'.");
            return;
        }

        // Byg absolut URL til success/cancel baseret på request (virker i lokal og produktion)
        var hostUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

        var options = new SessionCreateOptions
        {
            PaymentMethodTypes = new List<string> { "card" },
            Mode = "payment",
            LineItems = lineItems,
            SuccessUrl = $"{hostUrl}/success?session_id={{CHECKOUT_SESSION_ID}}",
            CancelUrl = $"{hostUrl}/cancel"
        };

        Console.WriteLine("Opretter Stripe session med options:");
        Console.WriteLine($"SuccessUrl: {options.SuccessUrl}");
        Console.WriteLine($"CancelUrl: {options.CancelUrl}");
        Console.WriteLine($"LineItems count: {lineItems.Count}");

        var service = new SessionService(); // will use StripeConfiguration.ApiKey
        var session = await service.CreateAsync(options);

        Console.WriteLine($"Stripe session oprettet: {session.Id}");

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { sessionId = session.Id }));
    }
    catch (JsonException jex)
    {
        ctx.Response.StatusCode = 400;
        Console.Error.WriteLine(jex);
        await ctx.Response.WriteAsync("Invalid JSON payload.");
    }
    catch (StripeException sex)
    {
        ctx.Response.StatusCode = 500;
        Console.Error.WriteLine(sex);
        await ctx.Response.WriteAsync("Stripe error: " + sex.Message);
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 500;
        Console.Error.WriteLine(ex);
        await ctx.Response.WriteAsync("Server error.");
    }
});

app.MapGet("/", async context =>
{
    var filePath = Path.Combine(app.Environment.ContentRootPath, "index.html");
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(filePath);
});

app.MapGet("/success", async context =>
{
    var filePath = Path.Combine(app.Environment.ContentRootPath, "sucess.html");
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(filePath);
});

app.MapGet("/cancel", async context =>
{
    var filePath = Path.Combine(app.Environment.ContentRootPath, "cancel.html");
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(filePath);
});

app.MapGet("/basket", async context =>
{
    var filePath = Path.Combine(app.Environment.ContentRootPath, "basket.html");
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(filePath);
});

app.MapGet("/stole", async context =>
{
    var filePath = Path.Combine(app.Environment.ContentRootPath, "stole.html");
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(filePath);
});
app.Run();
