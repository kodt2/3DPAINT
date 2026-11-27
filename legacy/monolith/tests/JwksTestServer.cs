using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace LoginAndReg.Tests;

/// <summary>
/// Minimal in-process JWKS endpoint for the monolith's lazy
/// <c>JwksKeyProvider</c>: a swappable JWKS document and a fetch hit counter
/// (the throttle test asserts the exact number of fetches). Binds loopback
/// inside the test container; the app runs in the same process host.
/// </summary>
public sealed class JwksTestServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private volatile string _jwksJson;
    private int _hits;

    public string Url { get; }

    /// <summary>How many times the app fetched the JWKS document.</summary>
    public int HitCount => Volatile.Read(ref _hits);

    private JwksTestServer(HttpListener listener, string url, string jwksJson)
    {
        _listener = listener;
        _jwksJson = jwksJson;
        Url = url;
        _listener.Start();
        _ = LoopAsync();
    }

    /// <summary>
    /// Starts the server on a random free loopback port (retried on collision).
    /// </summary>
    public static JwksTestServer Start(string jwksJson)
    {
        var random = new Random();
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var port = random.Next(20000, 60000);
            var url = $"http://127.0.0.1:{port}/.well-known/jwks.json";
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                return new JwksTestServer(listener, url, jwksJson);
            }
            catch (HttpListenerException)
            {
                // Port taken — try another.
            }
        }

        throw new InvalidOperationException("Could not bind a loopback port for the JWKS test server.");
    }

    /// <summary>Replaces the served JWKS document (e.g. to rotate keys).</summary>
    public void SetDocument(string jwksJson) => _jwksJson = jwksJson;

    /// <summary>
    /// Builds a JWKS document for the given RSA key in the exact shape the
    /// Auth service publishes (kty/use/alg/kid/n/e, ADR 0018).
    /// </summary>
    public static string BuildJwks(RsaSecurityKey key, string kid)
    {
        var rsa = key.Rsa ?? throw new InvalidOperationException("RsaSecurityKey has no RSA key.");
        var p = rsa.ExportParameters(false);

        static string B64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=');

        return $"{{\"keys\":[{{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"{kid}\","
               + $"\"n\":\"{B64Url(p.Modulus!)}\",\"e\":\"{B64Url(p.Exponent!)}\"}}]}}";
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                break; // listener stopped
            }

            Interlocked.Increment(ref _hits);
            try
            {
                var payload = Encoding.UTF8.GetBytes(_jwksJson);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream
                    .WriteAsync(payload, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Client went away; nothing to do.
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        await Task.CompletedTask;
    }
}
