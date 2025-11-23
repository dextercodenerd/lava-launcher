using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;

namespace GenericLauncher.Auth.Jwt;

[JsonSerializable(typeof(OpenIdConfiguration))]
[JsonSerializable(typeof(JwksResponse))]
[JsonSerializable(typeof(JwkKey))]
public partial class AuthJsonContext : JsonSerializerContext;

public record OpenIdConfiguration(
    [property: JsonPropertyName("jwks_uri")]
    string JwksUri
);

public record JwksResponse(
    [property: JsonPropertyName("keys")] JwkKey[] Keys
);

public record JwkKey(
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("kty")] string Kty,
    [property: JsonPropertyName("use")] string Use,
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("e")] string E
);

public sealed class MicrosoftJwtVerifier : IDisposable
{
    private readonly string _clientId;
    private readonly HttpClient _httpClient;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly Dictionary<string, SecurityKey> _keyCache;
    private readonly ReaderWriterLockSlim _cacheLock;
    private bool _disposed;

    public MicrosoftJwtVerifier(string clientId, HttpClient httpClient)
    {
        _clientId = clientId;
        _httpClient = httpClient;
        _tokenHandler = new JwtSecurityTokenHandler();
        _keyCache = new Dictionary<string, SecurityKey>();
        _cacheLock = new ReaderWriterLockSlim();
    }

    public async Task<(string tid, string sub)> VerifyMicrosoftTokenAsync(
        string jwtToken,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_tokenHandler.CanReadToken(jwtToken))
        {
            throw new AuthenticationException("Problem reading JWT token");
        }

        // Parse without validation first to get kid and claims
        var unvalidatedToken = _tokenHandler.ReadJwtToken(jwtToken);
        var kid = unvalidatedToken.Header.Kid;
        var tid = unvalidatedToken.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;
        var sub = unvalidatedToken.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
        var iss = unvalidatedToken.Claims.FirstOrDefault(c => c.Type == "iss")?.Value;

        if (string.IsNullOrWhiteSpace(kid))
        {
            throw new AuthenticationException("Invalid JWT token 'kid' header");
        }

        if (string.IsNullOrWhiteSpace(iss))
        {
            throw new AuthenticationException("Missing JWT token 'iss' claim");
        }

        if (string.IsNullOrWhiteSpace(tid))
        {
            throw new AuthenticationException("Missing JWT token 'tid claim");
        }

        if (string.IsNullOrWhiteSpace(sub))
        {
            throw new AuthenticationException("Missing JWT token 'sub' claim");
        }

        // Get the signing key
        var signingKey = await GetSigningKeyAsync(kid, iss, cancellationToken);
        if (signingKey is null)
        {
            throw new AuthenticationException("Problem loading JWT signing keys");
        }

        // Validate the token
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidIssuers = [],
            ValidateAudience = true,
            ValidAudience = _clientId,
            ValidateLifetime = true,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(5),
        };

        var principal = _tokenHandler.ValidateToken(jwtToken, validationParameters, out _);
        return (tid, sub);
    }

    private async Task<SecurityKey?> GetSigningKeyAsync(string kid, string issuer, CancellationToken cancellationToken)
    {
        // Check cache first (read lock)
        _cacheLock.EnterReadLock();
        try
        {
            if (_keyCache.TryGetValue(kid, out var cachedKey))
            {
                return cachedKey;
            }
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }

        // Fetch key from Microsoft (outside the lock)
        var newKey = await FetchKeyFromMicrosoftAsync(kid, issuer, cancellationToken);
        if (newKey is null)
        {
            return null;
        }

        // Update cache (write lock)
        _cacheLock.EnterWriteLock();
        try
        {
            // Double-check after acquiring write lock
            if (!_keyCache.TryGetValue(kid, out var existingKey))
            {
                _keyCache[kid] = newKey;
                return newKey;
            }

            return existingKey;
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
    }

    private async Task<SecurityKey?> FetchKeyFromMicrosoftAsync(string kid,
        string issuer,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get OpenID configuration
            var openIdConfigUrl = GetOpenIdConfigUrl(issuer);
            using var configResponse = await _httpClient.GetAsync(openIdConfigUrl, cancellationToken);
            configResponse.EnsureSuccessStatusCode();

            var config = await JsonSerializer.DeserializeAsync(
                await configResponse.Content.ReadAsStreamAsync(cancellationToken),
                AuthJsonContext.Default.OpenIdConfiguration,
                cancellationToken) ?? throw new InvalidOperationException("Failed to parse OpenID configuration");

            // Get JWKS
            using var jwksResponse = await _httpClient.GetAsync(config.JwksUri, cancellationToken);
            jwksResponse.EnsureSuccessStatusCode();

            var jwks = await JsonSerializer.DeserializeAsync(
                await jwksResponse.Content.ReadAsStreamAsync(cancellationToken),
                AuthJsonContext.Default.JwksResponse,
                cancellationToken) ?? throw new InvalidOperationException("Failed to parse JWKS");

            // Find the specific key
            var key = jwks.Keys.FirstOrDefault(k => k.Kid == kid && k.Use == "sig");
            if (key is null || key.Kty != "RSA")
            {
                return null;
            }

            // Convert to SecurityKey
            return CreateRsaSecurityKey(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log the exception if you have logging
            return null;
        }
    }

    private static RsaSecurityKey CreateRsaSecurityKey(JwkKey key)
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Base64UrlDecode(key.N),
            Exponent = Base64UrlDecode(key.E),
        });
        return new RsaSecurityKey(rsa);
    }

    private static string GetOpenIdConfigUrl(string issuer) =>
        issuer.EndsWith("/v2.0")
            ? $"{issuer}/.well-known/openid-configuration"
            : $"{issuer}/v2.0/.well-known/openid-configuration";

    private static byte[] Base64UrlDecode(string input)
    {
        var output = input.Replace('-', '+').Replace('_', '/');

        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }

        return Convert.FromBase64String(output);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _cacheLock.Dispose();
        _disposed = true;
    }
}
