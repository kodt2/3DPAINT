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
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBodyLengthLimit = 524_288_000;
    options.MemoryBufferThreshold = int.MaxValue;
});
builder.Services.AddSingleton<IMinioClient>(_ =>
{
    return new MinioClient()
        .WithEndpoint(builder.Configuration["Minio:Endpoint"])
        .WithCredentials(builder.Configuration["Minio:AccessKey"], builder.Configuration["Minio:SecretKey"])
        .WithSSL(builder.Configuration.GetValue<bool>("Minio:UseSsl"))
        .WithHttpClient(new HttpClient(new HttpClientHandler()) { DefaultRequestVersion = System.Net.HttpVersion.Version11 })
        .Build();
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
