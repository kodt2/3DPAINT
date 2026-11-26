using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Auth.Data;
using Auth.Entities;
using Auth.Options;
using Auth.Security;
using Auth.Services;
using Auth.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Structured logs (AGENTS.md §5). Mirrors the gateway's console layout.
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateLogger();
builder.Host.UseSerilog();

// Options (AGENTS.md §5: configuration via appsettings / environment).
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<RefreshTokenOptions>(builder.Configuration.GetSection(RefreshTokenOptions.SectionName));
builder.Services.Configure<PasswordPolicyOptions>(builder.Configuration.GetSection(PasswordPolicyOptions.SectionName));

// EF Core + Identity on the Auth service's own PostgreSQL database (ADR 0018).
var connectionString = builder.Configuration.GetConnectionString("AuthConnection")
    ?? throw new InvalidOperationException("Connection string 'AuthConnection' is not configured.");
builder.Services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(connectionString));
builder.Services
    .AddIdentity<AuthUser, IdentityRole<int>>(options =>
    {
        // The password policy is enforced by the auth service itself (min length
        // + common-passwords list, ADR 0018); relax Identity's defaults so a
        // policy-valid password is not rejected before we validate it.
        options.Password.RequiredLength = 1;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.User.RequireUniqueEmail = false;
        // Account lockout (step 4.2, ADR 0018): 5 failed attempts lock the
        // account for 15 minutes. The service authenticates with UserManager
        // (not SignInManager), so LoginAsync drives the failure counter and
        // the locked check explicitly; the response body stays the uniform
        // 401 "Неверный логин или пароль" in both cases.
        var lockout = builder.Configuration.GetSection("Lockout");
        options.Lockout.MaxFailedAccessAttempts = lockout.GetValue<int>("MaxFailedAttempts", 5);
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(lockout.GetValue<int>("LockoutMinutes", 15));
        options.Lockout.AllowedForNewUsers = true;
        options.User.AllowedUserNameCharacters =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
    })
    .AddEntityFrameworkStores<AuthDbContext>();

// RS256 signing: the key store is loaded eagerly at startup (fail-fast in
// Production), the token service signs access tokens.
builder.Services.AddSingleton<RsaKeyStore>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<JwksService>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();

// Hourly cleanup of dead refresh-token rows (step 4.2, ADR 0018).
builder.Services.AddHostedService<RefreshTokenCleanupWorker>();

// /health/ready must reflect the database the service depends on.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AuthDbContext>("auth-db");

// JWT Bearer is the default scheme; the token comes from the "jwt" cookie
// (ADR 0011) rather than an Authorization header. The RS256 signing keys are
// wired in the callback below, once the key store is loaded and the service
// provider is available (authServiceProvider, set after Build()).
IServiceProvider? authServiceProvider = null;
builder.Services
    .AddAuthentication(options =>
    {
        // This callback is registered after AddIdentity, so it wins. All three
        // defaults must point at the JWT scheme: .NET 10's AddIdentity sets
        // DefaultAuthenticateScheme to the Identity cookie scheme
        // ("Identity.Application"), and the authentication middleware honors
        // it over DefaultScheme — otherwise the middleware would authenticate
        // with the cookie handler and HttpContext.User would stay anonymous on
        // every [Authorize] endpoint.
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        // Wire the RS256 signing keys (current + previous) from the RsaKeyStore
        // singleton. It is loaded at startup (fail-fast, above); this callback
        // runs when the options are first built — after authServiceProvider is
        // set — so the keys are available here.
        if (authServiceProvider is not null)
        {
            var keys = authServiceProvider.GetRequiredService<RsaKeyStore>();
            var jwtOptions = authServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;
            JwtBearerOptionsConfigurator.WireSigningKeys(keys, jwtOptions, options);
        }

        // The SPA keeps the access token in the http-only "jwt" cookie, not a
        // header — hand it to the handler (ADR 0011).
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.HttpContext.Request.Cookies["jwt"];
                if (!string.IsNullOrEmpty(token))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// Controllers + OpenAPI (Development only; the SPA client is generated from it).
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "3DPAINT Auth Service",
        Version = "v1",
    });
});

var app = builder.Build();

// One-shot migration mode: `dotnet Auth.dll migrate` applies the migrations and
// seeds the default role, then exits. This runs in the dedicated migrate
// container (AGENTS.md §5), never inside the long-running auth service.
if (args.Length > 0 && args[0] == "migrate")
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await db.Database.MigrateAsync();
    await RoleSeeder.SeedAsync(db);
    Log.Information("Auth database migrated and seeded.");
    return 0;
}

// One-shot key generation: `dotnet Auth.dll keygen` creates the current and
// previous RSA keys in the configured paths when missing (dev convenience —
// the keys live in a named volume, never the image or repo, ADR 0018).
if (args.Length > 0 && args[0] == "keygen")
{
    app.Services.GetRequiredService<RsaKeyStore>().GenerateIfMissing();
    Log.Information("RSA signing keys are ready.");
    return 0;
}

// Fail-fast: load the RSA signing keys now. Development generates a missing
// current key (persisted to a named volume); Production throws if it is absent.
var keyStore = app.Services.GetRequiredService<RsaKeyStore>();
keyStore.Load();

// Make the service provider available to the AddJwtBearer callback and force
// the JwtBearerOptions to be built NOW (after the keys are loaded). The
// callback wires the current + previous signing keys into the options; without
// this explicit build the ordering with the key load would only hold by
// accident.
authServiceProvider = app.Services;
_ = app.Services
    .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
    .Get(JwtBearerDefaults.AuthenticationScheme);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

// JWKS for downstream consumers (ADR 0007 / ADR 0018). Excluded from the SPA's
// OpenAPI contract — it is infrastructure, not a client API.
app.MapGet(
        "/.well-known/jwks.json",
        (JwksService jwks) => Results.Json(jwks.BuildJwks()))
    .AllowAnonymous()
    .ExcludeFromDescription();

await app.RunAsync();

return 0;

public partial class Program;
