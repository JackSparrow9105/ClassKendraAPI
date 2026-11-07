using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ClassKendraAPI.Common;
using ClassKendraAPI.Data;
using ClassKendraAPI.Features.Auth;
using ClassKendraAPI.Features.Fees;
using ClassKendraAPI.Features.Messages;
using ClassKendraAPI.Features.OnlinePayments;
using ClassKendraAPI.Features.Payments;
using ClassKendraAPI.Features.Subscriptions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------- Database ----------
builder.Services.AddDbContext<ClassKendraContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// ---------- Controllers + JSON (matches the Angular models) ----------
builder.Services.AddControllers(o => o.Filters.Add<PlanGuardFilter>())   // read-only once the plan ends
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));   // enums as "Pending", "Cash"...

// ---------- CORS: let the Angular app call the API ----------
var allowedOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
                     ?? ["http://localhost:4200"];
builder.Services.AddCors(o => o.AddPolicy("Frontend", p =>
    p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

// ---------- Login (JWT) ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;   // keep claim names as "sub", "role", "tenantId"
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)),
            NameClaimType = "sub",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();

// ---------- Rate limits (per IP) for login and the public pay page ----------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, ct) => new ValueTask(ctx.HttpContext.Response.WriteAsJsonAsync(
        new { message = "Too many requests. Please wait a minute and try again." }, ct));

    o.AddPolicy("otp", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("public", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

// Azure App Service sits behind a proxy: use the caller's real IP and https scheme.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
#pragma warning disable ASPDEPR005 // KnownNetworks is marked obsolete in .NET 10 but still works
    o.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
    o.KnownProxies.Clear();
});

// ---------- App services ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<OtpService>();
builder.Services.AddScoped<PinService>();   // app PIN + remembered devices
builder.Services.AddScoped<FeeReadService>();
builder.Services.AddScoped<BillingService>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<MessageService>();
builder.Services.AddScoped<PlanService>();
builder.Services.AddScoped<SubscriptionService>();
builder.Services.AddScoped<OnlinePaymentService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<FirebaseTokenValidator>();   // SMS login via Firebase (off until Firebase:ProjectId is set)
builder.Services.AddHttpClient<RazorpayClient>(c => c.Timeout = TimeSpan.FromSeconds(20));

// WhatsApp: "Meta" sends for real; anything else only writes to the log (development).
if (builder.Configuration["WhatsApp:Provider"] == "Meta")
    builder.Services.AddHttpClient<IWhatsAppSender, MetaWhatsAppSender>(c => c.Timeout = TimeSpan.FromSeconds(20));
else
    builder.Services.AddSingleton<IWhatsAppSender, LogWhatsAppSender>();

// ---------- Background jobs (WhatsApp outbox, daily bills + reminders) ----------
if (builder.Configuration.GetValue("Jobs:Enabled", true))
{
    builder.Services.AddHostedService<OutboxWorker>();
    builder.Services.AddHostedService<DailyJobsWorker>();
}

// ---------- Errors + API docs ----------
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions["message"] = ctx.ProblemDetails.Detail ?? ctx.ProblemDetails.Title);

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "ClassKendra API"));
}
else
{
    app.UseExceptionHandler();   // hide stack traces from real users
}
app.UseStatusCodePages();

app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<CurrentUserMiddleware>();
app.UseAuthorization();
app.MapControllers();

// For Azure health checks: is the app up and can it reach the database?
app.MapGet("/health", async (ClassKendraContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));

app.Run();
