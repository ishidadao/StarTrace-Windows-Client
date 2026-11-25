using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;
using Duende.IdentityModel.OidcClient.Results;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace StarTrace.Client.Services;

public sealed record IdentityLogin(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string Username);

public sealed class IdentityLoginService
{
    public const string Issuer = "https://id.ishidadao.ltd";
    public const string ClientId = "startrace-windows";
    public const string ApiOrigin = "https://startrace.ishidadao.ltd";

    private static OidcClient CreateClient(string redirectUri, IBrowser? browser = null, string? nonce = null) => new(new OidcClientOptions
    {
        Authority = Issuer, ClientId = ClientId, RedirectUri = redirectUri,
        Scope = "openid profile email offline_access", Browser = browser,
        ClockSkew = TimeSpan.FromSeconds(30), BackchannelTimeout = TimeSpan.FromSeconds(20),
        IdentityTokenValidator = new SignedIdentityTokenValidator(nonce),
        RefreshDiscoveryOnSignatureFailure = true,
    });

    public async Task<IdentityLogin> LoginAsync(CancellationToken cancellationToken = default)
    {
        using var browser = new LoopbackBrowser();
        var nonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var client = CreateClient(browser.RedirectUri, browser, nonce);
        var result = await client.LoginAsync(new LoginRequest
        {
            BrowserTimeout = 300,
            FrontChannelExtraParameters = new Parameters { { "nonce", nonce }, { "prompt", "consent" } },
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.IsError) throw new InvalidOperationException("统一身份登录未完成，请检查浏览器授权、访问组或重新登录。");
        if (string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.RefreshToken))
            throw new InvalidOperationException("身份平台未返回完整令牌，请重新授权 offline_access。");
        return new IdentityLogin(result.AccessToken, result.RefreshToken, result.AccessTokenExpiration.ToUniversalTime(),
            result.User.FindFirst("preferred_username")?.Value ?? result.User.FindFirst("sub")?.Value ?? "Ishidadao ID");
    }

    public async Task<IdentityLogin> RefreshAsync(string refreshToken, string username, CancellationToken cancellationToken = default)
    {
        var result = await CreateClient("http://127.0.0.1/oauth/callback").RefreshTokenAsync(refreshToken, cancellationToken: cancellationToken);
        if (result.IsError) throw new InvalidOperationException("统一认证登录已失效，请退出登录后重新认证。本地记录不会删除。");
        return new IdentityLogin(result.AccessToken, string.IsNullOrEmpty(result.RefreshToken) ? refreshToken : result.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn), username);
    }

    public async Task RevokeAsync(CloudSettings settings, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // Best effort when offline. The caller always clears local tokens and
        // keeps local records. This is not global OIDC single logout.
        foreach (var (token, hint) in new[] { (settings.RefreshToken, "refresh_token"), (settings.AccessToken, "access_token") })
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(settings.RefreshToken)) continue;
            try
            {
                using var response = await http.PostAsync(Issuer + "/token/revocation", new FormUrlEncodedContent(new Dictionary<string,string>
                { ["client_id"] = ClientId, ["token"] = token, ["token_type_hint"] = hint }), cancellationToken);
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { }
        }
    }
}

public sealed class SignedIdentityTokenValidator(string? expectedNonce = null) : IIdentityTokenValidator
{
    public async Task<IdentityTokenValidationResult> ValidateAsync(string identityToken, OidcClientOptions options, CancellationToken cancellationToken = default)
    {
        var keys = options.ProviderInformation.KeySet.Keys
            .Where(k => k.Kty == "RSA" && (string.IsNullOrEmpty(k.Use) || k.Use == "sig") && (string.IsNullOrEmpty(k.Alg) || k.Alg == "RS256"))
            .Select(k => new JsonWebKey { Kty = k.Kty, Kid = k.Kid, N = k.N, E = k.E, Use = k.Use, Alg = k.Alg });
        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var result = await handler.ValidateTokenAsync(identityToken, new TokenValidationParameters
        {
            ValidIssuer = IdentityLoginService.Issuer, ValidAudience = IdentityLoginService.ClientId,
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
            RequireExpirationTime = true, RequireSignedTokens = true, ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys, ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(30),
        });
        if (!result.IsValid || result.ClaimsIdentity is null ||
            (expectedNonce is not null && result.ClaimsIdentity.FindFirst("nonce")?.Value != expectedNonce))
            return new IdentityTokenValidationResult { Error = "invalid_identity_token" };
        return new IdentityTokenValidationResult { SignatureAlgorithm = "RS256", User = new ClaimsPrincipal(result.ClaimsIdentity) };
    }
}

// Bind port 0 once and keep it reserved until the transaction ends. No admin
// HTTP.sys URL reservation, fixed callback port or externally reachable bind.
public sealed class LoopbackBrowser : IBrowser, IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Action<string> _openBrowser;
    public string RedirectUri { get; }
    public LoopbackBrowser(Action<string>? openBrowser = null)
    {
        _openBrowser = openBrowser ?? (url => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); });
        _listener.Start(8);
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/oauth/callback";
    }

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var start = new Uri(options.StartUrl);
        var expectedState = QueryValue(start.Query, "state");
        if (start.GetLeftPart(UriPartial.Authority) != IdentityLoginService.Issuer || options.EndUrl != RedirectUri || string.IsNullOrEmpty(expectedState))
            return new BrowserResult { ResultType = BrowserResultType.UnknownError, Error = "不可信的登录地址。" };
        _openBrowser(options.StartUrl);
        try
        {
            while (true)
            {
                using var connection = await _listener.AcceptTcpClientAsync(deadline.Token);
                using var incoming = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                incoming.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = connection.GetStream();
                try
                {
                    var buffer = new byte[8192];
                    var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(count), incoming.Token);
                        if (read == 0) break;
                        count += read;
                        if (Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n")) break;
                    }
                    var headers = Encoding.ASCII.GetString(buffer, 0, count);
                    var lines = headers.Split("\r\n");
                    var parts = lines[0].Split(' ');
                    var host = lines.FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
                    var expected = new Uri(RedirectUri);
                    Uri? callback = null;
                    var valid = count < buffer.Length && headers.Contains("\r\n\r\n") && parts.Length == 3 && parts[0] == "GET" &&
                        parts[1].StartsWith("/oauth/callback?", StringComparison.Ordinal) && host == expected.Authority &&
                        Uri.TryCreate(expected.GetLeftPart(UriPartial.Authority) + parts[1], UriKind.Absolute, out callback) &&
                        callback.AbsolutePath == expected.AbsolutePath && QueryValue(callback.Query, "state") == expectedState;
                    var body = Encoding.UTF8.GetBytes(valid ? "<!doctype html><meta charset=utf-8><title>星迹登录</title><p>授权回调已收到，请返回星迹客户端查看登录结果。可以关闭此页面。</p>" : "Invalid callback");
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, incoming.Token);
                    await stream.WriteAsync(body, incoming.Token);
                    if (valid) return new BrowserResult { ResultType = BrowserResultType.Success, Response = callback!.AbsoluteUri };
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException)
        {
            return new BrowserResult { ResultType = BrowserResultType.Timeout, Error = "登录等待已超时，请重新登录。" };
        }
    }

    private static string? QueryValue(string query, string name)
    {
        var values = query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .Where(pair => Uri.UnescapeDataString(pair[0]) == name).ToArray();
        return values.Length == 1 && values[0].Length == 2 ? Uri.UnescapeDataString(values[0][1].Replace('+', ' ')) : null;
    }
    public void Dispose() => _listener.Stop();
}
