using LoginAndReg.Models.Options;
using LoginAndReg.OpenApi;
using LoginAndReg.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Minio;
using Npgsql;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("DefaultConnection is not configured.");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 524_288_000;
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("JWT configuration is missing.");
if (string.IsNullOrWhiteSpace(jwtOptions.Key))
{
    throw new InvalidOperationException("JWT key is not configured!");
}

builder.Services.AddSingleton(new JwtService(jwtOptions.Key, jwtOptions.ExpiryMinutes));
builder.Services.AddScoped<IPasswordService, PasswordService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IFileStorageService, FileStorageService>();

var minioOptions = builder.Configuration.GetSection(MinioOptions.SectionName).Get<MinioOptions>()
    ?? throw new InvalidOperationException("Minio configuration is missing.");
if (string.IsNullOrWhiteSpace(minioOptions.Endpoint)
    || string.IsNullOrWhiteSpace(minioOptions.AccessKey)
    || string.IsNullOrWhiteSpace(minioOptions.SecretKey))
{
    throw new InvalidOperationException("Minio configuration is invalid.");
}

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
    options.MultipartBodyLengthLimit = 524_288_000;
    options.MemoryBufferThreshold = int.MaxValue;
});
builder.Services.AddSingleton<IMinioClient>(_ =>
{
    var client = new MinioClient()
        .WithEndpoint(minioOptions.Endpoint)
        .WithCredentials(minioOptions.AccessKey, minioOptions.SecretKey)
        .WithSSL(minioOptions.UseSsl)
        .WithHttpClient(new HttpClient(new HttpClientHandler()) { DefaultRequestVersion = System.Net.HttpVersion.Version11 });

    // Region is optional: not every S3-compatible storage requires it (ADR 0009).
    if (!string.IsNullOrWhiteSpace(minioOptions.Region))
    {
        client = client.WithRegion(minioOptions.Region);
    }

    return client.Build();
});

builder.Services.AddControllersWithViews();
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

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            if (context.Request.Cookies.ContainsKey("jwt"))
            {
                context.Token = context.Request.Cookies["jwt"];
            }

            return Task.CompletedTask;
        }
    };
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key))
    };
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

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
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
var provider = new FileExtensionContentTypeProvider();
provider.Mappings[".glb"] = "model/gltf-binary";

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider
});

// OpenAPI document for the SPA client (`npm run gen:api` in frontend/).
// Enabled by default in Development and via OpenApi:Enabled=true (dev compose
// stack: the monolith is only reachable through the gateway). The Swagger UI
// stays dev-only.
if (app.Configuration.GetValue("OpenApi:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    if (app.Environment.IsDevelopment())
    {
        app.UseSwaggerUI();
    }
}

app.UseRouting();

app.UseAuthentication();

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
    if (
        context.Request.Path.StartsWithSegments("/api")
        // Only endpoints that actually require authentication ([AllowAnonymous]
        // probes such as /api/health are left alone).
        && RequiresAuthorization(context))
    {
        var authResult = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
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

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Auth}/{id?}");

app.Run();
