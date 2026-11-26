using System.Security.Cryptography;
using System.Text;
using Auth.Options;
using Microsoft.Extensions.Options;

namespace Auth.Security;

/// <summary>A loaded RSA signing key: its <c>kid</c> (JWK thumbprint) and key.</summary>
public sealed record SigningKey(string Kid, RSA Rsa);

/// <summary>
/// Provides the current (and optional previous) RSA signing keys for RS256
/// access tokens (ADR 0007 / ADR 0018).
///
/// Key material never lives in the image or in git:
/// <list type="bullet">
///   <item>Development — a missing current key is generated on first start and
///     persisted to <see cref="JwtOptions.CurrentKeyFile"/> (a named volume, so
///     it survives container restarts).</item>
///   <item>Production — a missing current key is a hard startup failure
///     (fail-fast); the operator provides the key file via the environment.</item>
/// </list>
/// </summary>
public sealed class RsaKeyStore
{
    private readonly JwtOptions _options;
    private readonly ILogger<RsaKeyStore> _logger;
    private readonly bool _isDevelopment;

    public SigningKey? Current { get; private set; }
    public SigningKey? Previous { get; private set; }

    public RsaKeyStore(IOptions<JwtOptions> options, ILogger<RsaKeyStore> logger, IHostEnvironment environment)
    {
        _options = options.Value;
        _logger = logger;
        _isDevelopment = environment.EnvironmentName == Environments.Development;
    }

    /// <summary>
    /// Loads the current key (required) and the optional previous key. Throws
    /// when the current key is missing outside Development (fail-fast).
    /// </summary>
    public void Load()
    {
        var currentPath = _options.CurrentKeyFile;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            throw new InvalidOperationException("Jwt:CurrentKeyFile is not configured.");
        }

        if (!File.Exists(currentPath))
        {
            if (_isDevelopment)
            {
                GenerateAndPersist(currentPath);
                _logger.LogInformation(
                    "Development: generated an RSA signing key and persisted it to {Path}.", currentPath);
            }
            else
            {
                throw new InvalidOperationException(
                    $"The current RSA signing key file '{currentPath}' does not exist. " +
                    "Provide it via the environment (ADR 0018).");
            }
        }

        Current = LoadKey(currentPath);
        _logger.LogInformation("Loaded the current RSA signing key (kid={Kid}).", Current.Kid);

        var previousPath = _options.PreviousKeyFile;
        if (!string.IsNullOrWhiteSpace(previousPath))
        {
            if (File.Exists(previousPath))
            {
                Previous = LoadKey(previousPath);
                _logger.LogInformation("Loaded the previous RSA signing key (kid={Kid}).", Previous.Kid);
            }
            else
            {
                _logger.LogWarning(
                    "Previous RSA signing key file '{Path}' not found; tokens issued with it will not validate.",
                    previousPath);
            }
        }
    }

    /// <summary>
    /// Generates the current and previous keys in their configured paths when
    /// missing (idempotent, never overwrites an existing key). Used by the
    /// one-shot <c>keygen</c> command (dev convenience, ADR 0018).
    /// </summary>
    public void GenerateIfMissing()
    {
        var currentPath = _options.CurrentKeyFile;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            throw new InvalidOperationException("Jwt:CurrentKeyFile is not configured.");
        }

        if (File.Exists(currentPath))
        {
            _logger.LogInformation("Current RSA key already exists at {Path}; not regenerating.", currentPath);
        }
        else
        {
            GenerateAndPersist(currentPath);
            _logger.LogInformation("Generated the current RSA key at {Path}.", currentPath);
        }

        var previousPath = _options.PreviousKeyFile;
        if (!string.IsNullOrWhiteSpace(previousPath) && !File.Exists(previousPath))
        {
            GenerateAndPersist(previousPath);
            _logger.LogInformation("Generated the previous RSA key at {Path}.", previousPath);
        }
    }

    private static SigningKey LoadKey(string path)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(path));
        return new SigningKey(ComputeKid(rsa), rsa);
    }

    private void GenerateAndPersist(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var rsa = RSA.Create(_options.RsaKeySize);
        File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Private key: owner read/write only (POSIX file systems).
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// The <c>kid</c> is the JWK Thumbprint (RFC 7638): SHA-256 over the
    /// canonical JSON <c>{"e":…,"kty":"RSA","n":…}</c>, base64url-encoded.
    /// </summary>
    internal static string ComputeKid(RSA rsa)
    {
        var p = rsa.ExportParameters(false);
        var canonical = $"{{\"e\":\"{ToBase64Url(p.Exponent!)}\",\"kty\":\"RSA\",\"n\":\"{ToBase64Url(p.Modulus!)}\"}}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>base64url of the value with leading zero bytes stripped (JWK <c>n</c>/<c>e</c>).</summary>
    internal static string ToBase64Url(byte[] value)
    {
        int i = 0;
        while (i < value.Length - 1 && value[i] == 0)
        {
            i++;
        }

        var trimmed = value[i..];
        return Convert.ToBase64String(trimmed).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
