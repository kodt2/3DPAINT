using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Xunit;

namespace Auth.Tests;

/// <summary>
/// In-process host for the Auth service (<c>WebApplicationFactory</c>, ADR 0018 —
/// compose-stand strategy). The app reads its configuration (the Postgres
/// connection string, the JWT key paths) from the environment variables set by
/// the test stand (infra/compose.auth-test.yml), so the only overrides here are
/// forcing <c>Development</c>: the RSA signing keys are then generated into a
/// temp directory on first start, and the in-memory server runs over the
/// already-migrated test database; and pointing the host's <see cref="ILoggerFactory"/>
/// at a capturing sink so tests can assert on what the app logs.
/// </summary>
public sealed class AuthAppFactory : WebApplicationFactory<Program>
{
    private readonly CapturingLogSink _logSink = new();

    /// <summary>
    /// Log events written through the host's <see cref="ILoggerFactory"/> since the
    /// host started (step 4.3b pre-check: sensitive values must never reach the
    /// logs). Note: the app's own static <c>Log.Information</c> calls (startup,
    /// cleanup worker) bypass the host's factory and are not captured.
    /// </summary>
    public CapturingLogSink LogSink => _logSink;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Program.cs configures the Serilog static logger (console sink) and
        // UseSerilog() registers the host's ILoggerFactory from it. Replace that
        // registration with a factory over a logger that writes to the capturing
        // sink, so tests can assert on what the app logs (DI-injected loggers,
        // UseSerilogRequestLogging, EF/Identity) without changing the application
        // code. Console output of the app is not needed during tests.
        builder.ConfigureServices(services =>
        {
            var existing = services
                .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(ILoggerFactory));
            if (existing is not null)
            {
                services.Remove(existing);
            }

            var capturingLogger = new LoggerConfiguration()
                .WriteTo.Sink(_logSink)
                .CreateLogger();
            services.AddSingleton<ILoggerFactory>(
                new SerilogLoggerFactory(capturingLogger, dispose: false));
        });
    }
}

/// <summary>
/// One shared app instance — and therefore one RSA key and one migrated schema —
/// for the whole suite (xUnit collection fixture).
/// </summary>
[CollectionDefinition(nameof(AuthAppCollection))]
public sealed class AuthAppCollection : ICollectionFixture<AuthAppFactory>
{
    // No code: its purpose is to carry [CollectionDefinition] so every
    // [Collection(nameof(AuthAppCollection))] test shares a single factory.
}
