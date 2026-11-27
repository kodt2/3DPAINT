using LoginAndReg.Models.Options;
using LoginAndReg.OpenApi;
using LoginAndReg.Security;
using LoginAndReg.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Minio;
using Npgsql;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("DefaultConnection is not configured.");
}

// Single upload limit (ADR 0015): the same MAX_UPLOAD_BYTES variable the
// Gateway reads, so both edges reject the same sizes. Body-level limits get
// a small slack for the multipart envelope; the exact file limit is
// enforced in FileStorageService with a readable 413.
var maxUploadBytes = builder.Configuration.GetValue("MaxUploadBytes", UploadOptions.DefaultMaxBytes);
if (maxUploadBytes <= 0)
{
    throw new InvalidOperationException("MaxUploadBytes must be a positive number of bytes.");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes + UploadOptions.BodySlack;
});

// Accepted upload extensions (step 4.0): a single source of truth shared by
// the server-side validation (FileStorageService) and the public /api/config,
// so the SPA and the server can never disagree about which files are allowed.
var allowedUploadExtensions = ParseAllowedExtensions(builder.Configuration["AllowedUploadExtensions"]);

builder.Services.Configure<UploadOptions>(options =>
{
    options.MaxBytes = maxUploadBytes;
    options.AllowedExtensions = allowedUploadExtensions;
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// One-shot migration mode: `dotnet LoginAndReg.dll migrate` applies the
// migrations and exits (AGENTS.md §5: migrations are a separate command,
// never implicit web startup). It runs in the dedicated monolith-migrate
// container, which carries only the connection string — so the command is
// handled here, before the JWT/JWKS/Minio validation below that the web
// process needs but a migration does not.
if (args.Length > 0 && args[0] == "migrate")
{
    var migrationHost = builder.Build();
    using var scope = migrationHost.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    Console.WriteLine("Monolith database migrated.");
    // A bare return exits the top-level Main with code 0 before the HTTP
    // pipeline is ever built.
    return;
}

// JWT / JWKS / Minio options are read lazily (see the Read* helpers below):
// the integration test stand (WebApplicationFactory) overrides the
// configuration in memory at host-build time, so values captured eagerly in
// this top-level scope would bypass the overrides. Startup still fails fast:
// the same helpers run as validation right after the build, before any
// request is served.
//
// RS256 tokens from the Auth service (step 4.3a, ADR 0018): JWKS is fetched
// lazily (on the first RS256 token), so a down Auth service does not block
// monolith startup or the /api/health probe.
builder.Services.AddSingleton(sp =>
{
    var jwt = ReadJwtOptions(sp.GetRequiredService<IConfiguration>());
    return new JwtService(jwt.Key, jwt.ExpiryMinutes, jwt.Issuer, jwt.Audience);
});
builder.Services.AddSingleton(sp =>
    ReadAuthJwksOptions(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<JwksKeyProvider>();
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IFileStorageService, FileStorageService>();

builder.Services.Configure<MinioOptions>(builder.Configuration.GetSection(MinioOptions.SectionName));
// Presigned URLs must be reachable from the browser (TD-3): sign them with the
// public S3 endpoint when configured. The env name is top-level on purpose so
// the value stays one plain variable in .env (ADR 0009).
var publicS3Endpoint = builder.Configuration["PUBLIC_S3_ENDPOINT"] ?? string.Empty;
if (!string.IsNullOrWhiteSpace(publicS3Endpoint))
{
    builder.Services.Configure<MinioOptions>(options => options.PublicEndpoint = publicS3Endpoint);
}
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBodyLengthLimit = maxUploadBytes + UploadOptions.BodySlack;
    options.MemoryBufferThreshold = int.MaxValue;
});
builder.Services.AddSingleton<IMinioClient>(sp =>
{
    var minio = ReadMinioOptions(sp.GetRequiredService<IConfiguration>());
    var client = new MinioClient()
        .WithEndpoint(minio.Endpoint)
        .WithCredentials(minio.AccessKey, minio.SecretKey)
        .WithSSL(minio.UseSsl)
        .WithHttpClient(new HttpClient(new HttpClientHandler()) { DefaultRequestVersion = System.Net.HttpVersion.Version11 });

    // Region is optional: not every S3-compatible storage requires it (ADR 0009).
    if (!string.IsNullOrWhiteSpace(minio.Region))
    {
        client = client.WithRegion(minio.Region);
    }

    return client.Build();
});

// API-only MVC: no Razor view engine (step 3d.2 removed the Razor UI).
// All endpoints are attribute-routed [ApiController] under /api/**.
builder.Services.AddControllers();
builder.Services.AddAuthorization();

// OpenAPI for the /api surface only (the SPA client is generated from it;
// see ApiOnlyDocumentFilter). Page routes stay out of the document.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "3DPAINT monolith API",
        Version = "v1",
        Description = "JSON API of the legacy monolith consumed by the SPA (frontend/).",
    });
    options.DocumentFilter<ApiOnlyDocumentFilter>();
    // Swashbuckle 7.x cannot model [FromForm] IFormFile parameters; the upload
    // contract is added to the document manually in a later phase (TECH_DEBT).
    options.DocInclusionPredicate((_, apiDescription) =>
    {
        if (apiDescription.ActionDescriptor is not ControllerActionDescriptor action)
        {
            return true;
        }
        return !(action.ControllerName == "UserFiles" && action.ActionName == "Upload");
    });
});

// Two JWT schemes (step 4.3a, ADR 0018): the legacy HS256 (monolith-issued)
// and the RS256 from the Auth service (JWKS-validated). The 401 middleware
// below picks the scheme per request from the JOSE <c>alg</c> header.
// Neither scheme is weakened: signature, issuer, audience and lifetime are
// validated for both; ClockSkew is the same for both.
Task MessageReceivedReadJwtCookie(MessageReceivedContext context)
{
    if (context.Request.Cookies.TryGetValue(AuthCookie.Name, out var token) && !string.IsNullOrWhiteSpace(token))
    {
        context.Token = token;
    }

    return Task.CompletedTask;
}

// The JwtBearer configure actions run lazily — on the first options
// resolution (the first authentication request, i.e. after Build()) — so
// the JwksKeyProvider is captured through this local and assigned right
// after the app is built below. The configuration is read inside the
// configure actions for the same reason: only from the build on are the
// in-memory overrides of the test stand visible.
JwksKeyProvider? jwksProvider = null;
JwksKeyProvider RequireJwksProvider() => jwksProvider
    ?? throw new InvalidOperationException("JwksKeyProvider is not initialized yet.");
var configuration = builder.Configuration;

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
    var jwt = ReadJwtOptions(configuration);
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.Events = new JwtBearerEvents { OnMessageReceived = MessageReceivedReadJwtCookie };
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwt.Issuer,
        ValidateAudience = true,
        ValidAudience = jwt.Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key))
    };
})
.AddJwtBearer(JwtSchemes.Rs256, options =>
{
    // Keys come from our own lazy JwksKeyProvider (no built-in metadata
    // download), so RequireHttpsMetadata is moot; the http-allowance for the
    // internal Auth address is the explicit AuthJwks:RequireHttps config.
    var jwks = RequireJwksProvider();
    var jwt = ReadJwtOptions(configuration);
    var authJwks = ReadAuthJwksOptions(configuration);

    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = async context =>
        {
            await MessageReceivedReadJwtCookie(context);
            if (string.IsNullOrWhiteSpace(context.Token))
            {
                return;
            }

            // Prefetch the signing key before validation: the JWT key
            // resolver hook is synchronous, so the async (throttled) JWKS
            // fetch happens here. Any miss becomes a clean 401 (Fail),
            // never a 500.
            if (!JwtTokenHeader.TryParse(context.Token, out var alg, out var kid))
            {
                context.Fail("Malformed JWT header.");
                return;
            }

            if (string.IsNullOrWhiteSpace(kid))
            {
                context.Fail("RS256 token without a kid.");
                return;
            }

            var result = await jwks.EnsureLoadedAsync(kid!, context.HttpContext.RequestAborted);
            if (result is not JwksLoadResult.Found)
            {
                context.Fail($"Signing key '{kid}' is not available (JWKS: {result}).");
            }
        }
    };
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = authJwks.Issuer,
        ValidateAudience = true,
        ValidAudience = authJwks.Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
        // Note: inbound claim mapping (sub -> ClaimTypes.NameIdentifier) is
        // handler-level, not configurable through TokenValidationParameters;
        // the owner is therefore read explicitly from "sub" with the mapped
        // NameIdentifier as the documented fallback (CurrentOwner in
        // ModelsApiController). A test pins the sub-based resolution.
        // The kid arrives from the JWS header (third resolver parameter) —
        // no dependency on the concrete SecurityToken type, which differs
        // between the IdentityModel 7.x (middleware) and 8.x (app) lines.
        IssuerSigningKeyResolver = (token, securityToken, kid, parameters) =>
            kid is not null
            && jwks.Resolve(kid) is SecurityKey key
                ? new List<SecurityKey> { key }
                : new List<SecurityKey>()
    };
});

var app = builder.Build();

// Fail-fast validation of the options the web process needs. It runs right
// after the build (not before) on purpose: that is the first moment the
// WebApplicationFactory test stand's in-memory overrides are visible to the
// app configuration. In production this still fails fast — before any
// request is served.
ReadJwtOptions(builder.Configuration);
ReadAuthJwksOptions(builder.Configuration);
ReadMinioOptions(builder.Configuration);

// See RequireJwksProvider above: the JwtBearer options actions (and with
// them the JWKS-validated scheme) only run on the first authentication
// request, so assigning the provider here is always in time.
jwksProvider = app.Services.GetRequiredService<JwksKeyProvider>();

try
{
    await using var testConn = new NpgsqlConnection(connectionString);
    await testConn.OpenAsync();
    Console.WriteLine("EF Test Connection OK");
}
catch (Exception ex)
{
    Console.WriteLine("Ошибка подключения к БД: " + ex.Message);
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// OpenAPI document for the SPA client (`npm run gen:api` in frontend/).
// Served only when ASPNETCORE_ENVIRONMENT=Development (local dev stacks set it
// via infra/.env); production-like stacks answer 404 for /swagger/**.
// The Swagger UI is Development-only as well.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();

// JSON API hardening (step 3b.0, ADR 0011 «CSRF-защита»): a state-changing
// /api/** request that carries a body must send application/json, unless the
// endpoint accepts a form (multipart upload). Browsers cannot submit JSON from
// a cross-site <form>, so this blocks forged cross-site form POSTs in addition
// to the gateway Origin check. Bodyless mutations (logout, delete-by-id) and
// non-/api routes keep working as before.
app.Use(async (context, next) =>
{
    if (!RequiresJsonContentType(context))
    {
        await next();
        return;
    }

    await WriteProblemDetailsAsync(
        context,
        StatusCodes.Status415UnsupportedMediaType,
        "Unsupported Media Type",
        "JSON API endpoints accept only 'Content-Type: application/json'.");
});

app.UseAuthentication();

// True for state-changing /api/** requests with a body that target a JSON
// endpoint and do not send application/json (see the middleware above).
static bool RequiresJsonContentType(HttpContext context)
{
    var request = context.Request;
    if (!request.Path.StartsWithSegments("/api"))
    {
        return false;
    }

    var method = request.Method;
    if (!HttpMethods.IsPost(method) && !HttpMethods.IsPut(method)
        && !HttpMethods.IsPatch(method) && !HttpMethods.IsDelete(method))
    {
        return false;
    }

    if (!HasBody(request))
    {
        return false;
    }

    if (AcceptsForm(context.GetEndpoint()))
    {
        return false;
    }

    return !HasApplicationJsonContentType(request);
}

// RFC 7807 JSON with the canonical lower-case field names.
// WriteAsync (not WriteAsJsonAsync): the latter replaces the Content-Type
// with application/json and would drop the application/problem+json media type.
static async Task WriteProblemDetailsAsync(HttpContext context, int statusCode, string title, string detail)
{
    context.Response.Clear();
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "application/problem+json; charset=utf-8";

    var problem = new
    {
        type = "https://httpwg.org/specs/rfc7807#about-blank",
        title,
        status = statusCode,
        detail,
    };
    var json = JsonSerializer.Serialize(problem, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await context.Response.WriteAsync(json, Encoding.UTF8);
}

static bool HasBody(HttpRequest request)
    => request.ContentLength > 0
       || request.Headers.TransferEncoding.Any(v => string.Equals(v, "chunked", StringComparison.OrdinalIgnoreCase));

static bool AcceptsForm(Endpoint? endpoint)
{
    if (endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action)
    {
        return false;
    }

    // The binding source is the stable signal for form (multipart) parameters.
    // In .NET 8 BindingSource is a class of singleton instances, so compare
    // with Equals rather than an `is` constant pattern.
    foreach (var parameter in action.Parameters)
    {
        var bindingSource = parameter.BindingInfo?.BindingSource;
        if (bindingSource is not null
            && (bindingSource.Equals(BindingSource.Form)
                || bindingSource.Equals(BindingSource.FormFile)))
        {
            return true;
        }
    }

    return false;
}

static bool HasApplicationJsonContentType(HttpRequest request)
{
    var contentType = request.ContentType;
    if (string.IsNullOrWhiteSpace(contentType))
    {
        return false;
    }

    var mediaRange = contentType.Split(';', 2)[0].Trim();
    return mediaRange.Equals("application/json", StringComparison.OrdinalIgnoreCase);
}

// True when the endpoint carries [Authorize] without an [AllowAnonymous] override.
static bool RequiresAuthorization(HttpContext context)
{
    var endpoint = context.GetEndpoint();
    if (endpoint is null)
    {
        return false;
    }

    return endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null
           && endpoint.Metadata.GetMetadata<AllowAnonymousAttribute>() is null;
}

// /api/** must answer 401 with a JSON body (the SPA parses it) instead of the
// default empty challenge. Done here — before authorization — so the JWT
// handler never touches the response it would otherwise start for us.
// Non-/api paths keep the legacy challenge behavior untouched.
app.Use(async (context, next) =>
{
    // Only endpoints that actually require authentication ([AllowAnonymous]
    // probes such as /api/health are left alone).
    if (context.Request.Path.StartsWithSegments("/api") && RequiresAuthorization(context))
    {
        // Step 4.3a: pick the scheme from the token's JOSE header — the
        // legacy HS256 (default) or the RS256 from the Auth service. A
        // malformed token falls back to the legacy scheme and fails there.
        var scheme = JwtSchemes.ForToken(context.Request.Cookies[AuthCookie.Name]);
        var authResult = await context.AuthenticateAsync(scheme);

        // .NET 8's AuthenticateAsync(scheme) does not assign context.User;
        // without it UseAuthorization would challenge even on success.
        if (authResult.Succeeded && authResult.Principal is not null)
        {
            context.User = authResult.Principal;
        }

        if (!authResult.Succeeded)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsync(
                "{\"code\":\"unauthorized\",\"message\":\"Authentication required\"}");
            return;
        }
    }

    await next();
});

app.UseAuthorization();

// Attribute-routed API controllers ([ApiController] under /api/**). Step 3d.2
// removed the Razor controllers and the conventional default route, so the
// conventional MapControllerRoute is no longer needed; MapControllers maps
// the remaining attribute-routed API surface.
app.MapControllers();

app.Run();

/// <summary>
/// Parses the comma-separated ALLOWED_UPLOAD_EXTENSIONS variable into a
/// normalized list (leading dot, trimmed, case-insensitive-unique). Falls
/// back to the default (".glb") when unset or empty. Extensions may be given
/// with or without a leading dot; the stored form always has one.
/// </summary>
static IReadOnlyList<string> ParseAllowedExtensions(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return UploadOptions.DefaultAllowedExtensions.Split(',');
    }

    return raw
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(e => e.StartsWith('.') ? e : "." + e)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

/// <summary>
/// Reads and validates the HS256 options. Single source of truth for what a
/// valid configuration is: called by the DI registration, the JwtBearer
/// configure actions and the post-build startup validation.
/// </summary>
static JwtOptions ReadJwtOptions(IConfiguration configuration)
{
    var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
        ?? throw new InvalidOperationException("JWT configuration is missing.");
    if (string.IsNullOrWhiteSpace(jwt.Key))
    {
        throw new InvalidOperationException("JWT key is not configured!");
    }
    if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
    {
        // Step 4.3a: the HS256 tokens carry iss/aud and both are validated on
        // read, so the values must exist (they were in the config before, unused).
        throw new InvalidOperationException("JWT issuer and audience are not configured!");
    }
    if (jwt.ClockSkewSeconds < 0)
    {
        throw new InvalidOperationException("Jwt:ClockSkewSeconds must be >= 0.");
    }

    return jwt;
}

static AuthJwksOptions ReadAuthJwksOptions(IConfiguration configuration)
{
    var authJwks = configuration.GetSection(AuthJwksOptions.SectionName).Get<AuthJwksOptions>()
        ?? new AuthJwksOptions();
    if (string.IsNullOrWhiteSpace(authJwks.Url))
    {
        throw new InvalidOperationException("AuthJwks:Url is not configured.");
    }
    if (authJwks.RequireHttps
        && !authJwks.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("AuthJwks:RequireHttps is true but AuthJwks:Url is not https.");
    }
    if (string.IsNullOrWhiteSpace(authJwks.Issuer) || string.IsNullOrWhiteSpace(authJwks.Audience))
    {
        throw new InvalidOperationException("AuthJwks issuer and audience are not configured.");
    }
    if (authJwks.RefreshThrottleSeconds < 0)
    {
        throw new InvalidOperationException("AuthJwks:RefreshThrottleSeconds must be >= 0.");
    }

    return authJwks;
}

static MinioOptions ReadMinioOptions(IConfiguration configuration)
{
    var minio = configuration.GetSection(MinioOptions.SectionName).Get<MinioOptions>()
        ?? throw new InvalidOperationException("Minio configuration is missing.");
    if (string.IsNullOrWhiteSpace(minio.Endpoint)
        || string.IsNullOrWhiteSpace(minio.AccessKey)
        || string.IsNullOrWhiteSpace(minio.SecretKey))
    {
        throw new InvalidOperationException("Minio configuration is invalid.");
    }

    return minio;
}

// Entry-point marker for WebApplicationFactory (integration tests).
public partial class Program;
