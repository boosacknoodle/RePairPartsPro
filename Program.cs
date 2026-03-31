using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using RepairPartsPro.Models;
using RepairPartsPro.Services;
using Stripe;
using Stripe.Checkout;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MarketplaceApiOptions>(builder.Configuration.GetSection("MarketplaceApis"));
builder.Services.Configure<PriceSyncOptions>(builder.Configuration.GetSection("PriceSync"));
builder.Services.Configure<CertificationPolicyOptions>(builder.Configuration.GetSection("CertificationPolicy"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<AuthProtectionOptions>(builder.Configuration.GetSection("Security:AuthProtection"));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
	options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
	options.KnownIPNetworks.Clear();
	options.KnownProxies.Clear();
});

var adminEmails = (builder.Configuration.GetSection("Security:AdminEmails").Get<string[]>() ?? Array.Empty<string>())
	.Select(x => x.Trim())
	.Where(x => !string.IsNullOrWhiteSpace(x))
	.ToHashSet(StringComparer.OrdinalIgnoreCase);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<VendorDataStore>();
builder.Services.AddSingleton<PriceCertificationEngine>();
builder.Services.AddHttpClient<IMarketplaceQuoteFetcher, MarketplaceQuoteFetcher>(client =>
{
	client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<MarketplacePricingResolverService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<LoginAttemptProtector>();
builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>(client =>
{
	client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHostedService<MarketplacePriceSyncWorker>();

var jwtKey = builder.Configuration["Jwt:Key"]
	?? throw new InvalidOperationException("Jwt:Key is required. Configure it via user secrets or environment variable.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
	.AddJwtBearer(options =>
	{
		options.TokenValidationParameters = new TokenValidationParameters
		{
			ValidateIssuer = true,
			ValidateAudience = true,
			ValidateLifetime = true,
			ValidateIssuerSigningKey = true,
			ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "RepairPartsPro",
			ValidAudience = builder.Configuration["Jwt:Audience"] ?? "RepairPartsProUsers",
			IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
			ClockSkew = TimeSpan.FromMinutes(1)
		};
	});

builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
	options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

	options.AddPolicy("auth-login", context =>
	{
		var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
		return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
		{
			PermitLimit = 10,
			Window = TimeSpan.FromMinutes(1),
			QueueLimit = 0,
			AutoReplenishment = true
		});
	});

	options.AddPolicy("auth-forgot", context =>
	{
		var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
		return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
		{
			PermitLimit = 5,
			Window = TimeSpan.FromMinutes(5),
			QueueLimit = 0,
			AutoReplenishment = true
		});
	});
});

var app = builder.Build();

// Configure Stripe secret key
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"] ?? string.Empty;

using (var scope = app.Services.CreateScope())
{
	var store = scope.ServiceProvider.GetRequiredService<VendorDataStore>();
	await store.InitializeAsync();
	if (app.Environment.IsDevelopment())
	{
		await store.SeedIfEmptyAsync();
		await store.EnsureMarketplaceCoverageAsync();
	}
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler(errorApp =>
	{
		errorApp.Run(context => Results.Problem("An unexpected server error occurred.").ExecuteAsync(context));
	});
	app.UseHsts();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "RepairPartsPro", timestampUtc = DateTime.UtcNow }));

app.MapPost("/api/auth/register", async (RegisterRequest request, VendorDataStore store, AuthService auth, CancellationToken cancellationToken) =>
{
	var (valid, error) = AuthService.ValidateRegistrationInput(request.Email, request.Password);
	if (!valid) return Results.BadRequest(new { error });

	var existing = await store.FindUserByEmailAsync(request.Email, cancellationToken);
	if (existing is not null) return Results.Conflict(new { error = "An account with that email already exists." });

	var hash = auth.HashPassword(request.Password);
	var user = await store.CreateUserAsync(request.Email, hash, cancellationToken);
	var token = auth.GenerateToken(user, request.RememberMe);

	return Results.Ok(new AuthResponse { Token = token, Email = user.Email });
});

app.MapPost("/api/auth/login", async (LoginRequest request, VendorDataStore store, AuthService auth, LoginAttemptProtector attemptProtector, CancellationToken cancellationToken) =>
{
	if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
		return Results.BadRequest(new { error = "Email and password are required." });

	var gate = attemptProtector.CanAttempt(request.Email);
	if (!gate.Allowed)
		return Results.Json(new { error = gate.Message, retryAfterSeconds = gate.RetryAfterSeconds }, statusCode: 429);

	var user = await store.FindUserByEmailAsync(request.Email, cancellationToken);
	if (user is null || !auth.VerifyPassword(request.Password, user.PasswordHash))
	{
		attemptProtector.RecordFailure(request.Email);
		var postFailureGate = attemptProtector.CanAttempt(request.Email);
		if (!postFailureGate.Allowed)
			return Results.Json(new { error = postFailureGate.Message, retryAfterSeconds = postFailureGate.RetryAfterSeconds }, statusCode: 429);
		return Results.Unauthorized();
	}

	attemptProtector.RecordSuccess(request.Email);

	var token = auth.GenerateToken(user, request.RememberMe);
	return Results.Ok(new AuthResponse { Token = token, Email = user.Email });
}).RequireRateLimiting("auth-login");

app.MapPost("/api/auth/forgot-password", async (ForgotPasswordRequest request, VendorDataStore store, IEmailSender emailSender, IConfiguration config, IWebHostEnvironment env, CancellationToken cancellationToken) =>
{
	if (string.IsNullOrWhiteSpace(request.Email))
	{
		return Results.Ok(new { message = "If that email exists, password reset instructions were sent." });
	}

	var resetToken = await store.CreatePasswordResetTokenAsync(request.Email, cancellationToken);
	if (!string.IsNullOrWhiteSpace(resetToken))
	{
		var baseUrl = config["Email:AppBaseUrl"] ?? $"{(env.IsDevelopment() ? "http" : "https")}://localhost:5002";
		var normalizedBase = baseUrl.TrimEnd('/');
		var encodedToken = Uri.EscapeDataString(resetToken);
		var resetUrl = $"{normalizedBase}/reset-password.html?token={encodedToken}";
		await emailSender.SendPasswordResetAsync(request.Email.Trim(), resetUrl, cancellationToken);
	}

	if (!env.IsDevelopment() || string.IsNullOrWhiteSpace(resetToken))
	{
		return Results.Ok(new { message = "If that email exists, password reset instructions were sent." });
	}

	return Results.Ok(new
	{
		message = "If that email exists, password reset instructions were sent.",
		developerResetUrl = $"/reset-password.html?token={resetToken}"
	});
}).RequireRateLimiting("auth-forgot");

app.MapPost("/api/auth/reset-password", async (ResetPasswordRequest request, VendorDataStore store, AuthService auth, CancellationToken cancellationToken) =>
{
	if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
		return Results.BadRequest(new { error = "Token and new password are required." });

	if (request.NewPassword.Length < 8)
		return Results.BadRequest(new { error = "Password must be at least 8 characters." });

	var hash = auth.HashPassword(request.NewPassword);
	var ok = await store.ResetPasswordWithTokenAsync(request.Token, hash, cancellationToken);
	if (!ok)
		return Results.BadRequest(new { error = "Invalid or expired reset token." });

	return Results.Ok(new { message = "Password reset complete." });
});

app.MapPost("/api/parts/search", async (PartSearchRequest request, HttpContext context, VendorDataStore store, MarketplacePricingResolverService resolver, CancellationToken cancellationToken) =>
{
	var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
	if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out var userId))
		return Results.Unauthorized();

	var profile = await store.GetUserProfileAsync(userId, cancellationToken);
	if (profile is null || !profile.IsSubscriptionActive || profile.SubscriptionTier == "None")
		return Results.Json(new { error = "No active subscription. Please purchase a plan to search.", code = "subscription_required" }, statusCode: 402);

	var result = await resolver.SearchAsync(request, cancellationToken);
	var queryText = string.Join(" ", new[] { request.Brand, request.Model, request.PartType }.Where(x => !string.IsNullOrWhiteSpace(x)));
	await store.TrackSearchAnalyticsAsync(userId, string.IsNullOrWhiteSpace(queryText) ? "(blank)" : queryText, result.Listings.Count > 0, cancellationToken);
	return Results.Ok(result);
}).RequireAuthorization();

app.MapPost("/api/stripe/checkout", async (HttpContext context, VendorDataStore store, IConfiguration config, CancellationToken cancellationToken) =>
{
	var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
	var emailClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email);
	if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out var userId))
		return Results.Unauthorized();

	var body = await context.Request.ReadFromJsonAsync<StripeCheckoutRequest>(cancellationToken);
	if (body is null || string.IsNullOrWhiteSpace(body.TierName))
		return Results.BadRequest(new { error = "TierName is required." });

	var priceId = body.TierName == "Pro"
		? config["Stripe:ProPriceId"]
		: config["Stripe:BasicPriceId"];

	if (string.IsNullOrWhiteSpace(priceId))
		return Results.Problem("Stripe price ID not configured for that plan.");

	await store.TrackPlanClickAsync(userId, body.TierName, cancellationToken);

	var existingCustomerId = await store.GetStripeCustomerIdAsync(userId, cancellationToken);
	var origin = $"{context.Request.Scheme}://{context.Request.Host}";

	var options = new SessionCreateOptions
	{
		Mode = "subscription",
		LineItems = new List<SessionLineItemOptions>
		{
			new SessionLineItemOptions { Price = priceId, Quantity = 1 }
		},
		SuccessUrl = $"{origin}/profile.html?checkout=success",
		CancelUrl = $"{origin}/profile.html?checkout=cancelled",
		Metadata = new Dictionary<string, string>
		{
			["userId"] = userId.ToString(CultureInfo.InvariantCulture),
			["tierName"] = body.TierName
		},
		CustomerEmail = string.IsNullOrWhiteSpace(existingCustomerId) ? emailClaim?.Value : null,
		Customer = string.IsNullOrWhiteSpace(existingCustomerId) ? null : existingCustomerId,
	};

	var service = new SessionService();
	var session = await service.CreateAsync(options, cancellationToken: cancellationToken);

	return Results.Ok(new { url = session.Url });
}).RequireAuthorization();

app.MapPost("/api/stripe/webhook", async (HttpContext context, VendorDataStore store, IConfiguration config) =>
{
	var webhookSecret = config["Stripe:WebhookSecret"] ?? string.Empty;
	string json;
	using (var reader = new System.IO.StreamReader(context.Request.Body))
		json = await reader.ReadToEndAsync();

	Event stripeEvent;
	try
	{
		stripeEvent = EventUtility.ConstructEvent(json, context.Request.Headers["Stripe-Signature"], webhookSecret);
	}
	catch (StripeException)
	{
		return Results.BadRequest();
	}

	if (stripeEvent.Type == EventTypes.CheckoutSessionCompleted)
	{
		var session = stripeEvent.Data.Object as Session;
		if (session is null) return Results.Ok();

		var customerId = session.CustomerId;
		var tierName = session.Metadata.TryGetValue("tierName", out var t) ? t : "Basic";
		var userIdStr = session.Metadata.TryGetValue("userId", out var uid) ? uid : null;

		if (!string.IsNullOrWhiteSpace(customerId) && !string.IsNullOrWhiteSpace(userIdStr)
			&& int.TryParse(userIdStr, out var webhookUserId))
		{
			var existing = await store.GetStripeCustomerIdAsync(webhookUserId);
			if (string.IsNullOrWhiteSpace(existing))
				await store.SetStripeCustomerIdAsync(webhookUserId, customerId);
		}

		if (!string.IsNullOrWhiteSpace(customerId))
			await store.ActivateSubscriptionFromStripeAsync(customerId, tierName);
	}
	else if (stripeEvent.Type == EventTypes.CustomerSubscriptionDeleted)
	{
		var subscription = stripeEvent.Data.Object as Stripe.Subscription;
		if (subscription?.CustomerId is not null)
			await store.CancelSubscriptionAsync_ByStripeCustomer(subscription.CustomerId);
	}

	return Results.Ok();
});

app.MapGet("/api/analytics/summary", async (HttpContext context, VendorDataStore store, int? days, CancellationToken cancellationToken) =>
{
	var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
	var emailClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? string.Empty;
	if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out _) || !adminEmails.Contains(emailClaim))
		return Results.Unauthorized();

	var summary = await store.GetAnalyticsSummaryAsync(days ?? 30, cancellationToken);
	return Results.Ok(summary);
}).RequireAuthorization();

app.MapGet("/api/intel/hard-parts", async (VendorDataStore store, CancellationToken cancellationToken) =>
{
	var items = await store.GetHardPartIntelAsync(12, cancellationToken);
	return Results.Ok(new
	{
		mission = "Hard-to-find repair parts, sourced from recurring shop pain patterns and screened for scam risk.",
		count = items.Count,
		items
	});
}).RequireAuthorization();

app.MapGet("/api/profile", async (HttpContext context, VendorDataStore store, CancellationToken cancellationToken) =>
{
	var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
	if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out var userId))
		return Results.Unauthorized();

	var profile = await store.GetUserProfileAsync(userId, cancellationToken);
	if (profile is null) return Results.NotFound();

	return Results.Ok(profile);
}).RequireAuthorization();

app.MapPost("/api/profile/password", async (UpdatePasswordRequest request, HttpContext context, VendorDataStore store, AuthService auth, CancellationToken cancellationToken) =>
{
	var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
	if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out var userId))
		return Results.Unauthorized();

	if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
		return Results.BadRequest(new { error = "Current and new password are required." });

	if (request.NewPassword.Length < 8)
		return Results.BadRequest(new { error = "New password must be at least 8 characters." });

	var user = await store.FindUserByEmailAsync(context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "", cancellationToken);
	if (user is null || !auth.VerifyPassword(request.CurrentPassword, user.PasswordHash))
		return Results.Unauthorized();

	var newHash = auth.HashPassword(request.NewPassword);
	await store.UpdateUserPasswordAsync(userId, newHash, cancellationToken);

	return Results.Ok(new { message = "Password updated successfully." });
}).RequireAuthorization();

app.MapPost("/api/profile/delete", async (DeleteAccountRequest request, HttpContext context, VendorDataStore store, AuthService auth, CancellationToken cancellationToken) =>
{
	var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
	var emailClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? string.Empty;
	if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out var userId))
		return Results.Unauthorized();

	if (string.IsNullOrWhiteSpace(request.CurrentPassword))
		return Results.BadRequest(new { error = "Current password is required to delete account." });

	var user = await store.FindUserByEmailAsync(emailClaim, cancellationToken);
	if (user is null || !auth.VerifyPassword(request.CurrentPassword, user.PasswordHash))
		return Results.Unauthorized();

	await store.DeleteUserAccountAsync(userId, cancellationToken);
	return Results.Ok(new { message = "Account deleted." });
}).RequireAuthorization();

app.MapPost("/api/profile/subscription/upgrade", async (UpgradeSubscriptionRequest request, HttpContext context, VendorDataStore store, CancellationToken cancellationToken) =>
{
	var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
	if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out var userId))
		return Results.Unauthorized();

	if (string.IsNullOrWhiteSpace(request.TierName))
		return Results.BadRequest(new { error = "Subscription tier name is required." });

	var validTiers = new[] { "Basic", "Pro" };
	if (!validTiers.Contains(request.TierName))
		return Results.BadRequest(new { error = "Invalid subscription tier." });

	await store.UpgradeSubscriptionAsync(userId, request.TierName, daysValid: 30, cancellationToken);
	var profile = await store.GetUserProfileAsync(userId, cancellationToken);

	return Results.Ok(profile);
}).RequireAuthorization();

app.MapPost("/api/profile/subscription/cancel", async (HttpContext context, VendorDataStore store, CancellationToken cancellationToken) =>
{
	var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
	if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out var userId))
		return Results.Unauthorized();

	await store.CancelSubscriptionAsync(userId, cancellationToken);
	var profile = await store.GetUserProfileAsync(userId, cancellationToken);

	return Results.Ok(profile);
}).RequireAuthorization();

app.Run();

record StripeCheckoutRequest(string TierName);
