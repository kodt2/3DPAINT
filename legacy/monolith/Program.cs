using LoginAndReg.Models.Options;
using LoginAndReg.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Auth}/{id?}");

app.Run();
