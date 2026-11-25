using System.Net;
using System.Security.Cryptography;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using StarTrace.Client.Services;
using StarTrace.Client.Models;

static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
await WorkflowTests.CheckSdkBrowserAsync();
await WorkflowTests.CheckTransfersAsync();
using var rsa = RSA.Create(2048);
var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
var publicJson = System.Text.Json.JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = key.KeyId, alg = "RS256", use = "sig", n = jwk.N, e = jwk.E } } });
var options = new OidcClientOptions { ClientId = IdentityLoginService.ClientId, ProviderInformation = new ProviderInformation { IssuerName = IdentityLoginService.Issuer, KeySet = new Duende.IdentityModel.Jwk.JsonWebKeySet(publicJson) } };
var handler = new JsonWebTokenHandler();
string Token(string issuer = IdentityLoginService.Issuer, string audience = IdentityLoginService.ClientId, string nonce = "nonce-123", DateTime? expires = null) => handler.CreateToken(new SecurityTokenDescriptor
{
    Issuer = issuer, Audience = audience, IssuedAt = DateTime.UtcNow.AddMinutes(-10), NotBefore = DateTime.UtcNow.AddMinutes(-10),
    Expires = expires ?? DateTime.UtcNow.AddMinutes(5), SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
    Claims = new Dictionary<string, object> { ["sub"] = "test-sub", ["nonce"] = nonce, ["preferred_username"] = "test-user" },
});
var validator = new SignedIdentityTokenValidator("nonce-123");
Check(!(await validator.ValidateAsync(Token(), options)).IsError, "valid RS256 ID token passes");
Check((await validator.ValidateAsync(Token(issuer: "https://evil.invalid"), options)).IsError, "foreign issuer rejected");
Check((await validator.ValidateAsync(Token(audience: "other-client"), options)).IsError, "foreign client audience rejected");
Check((await validator.ValidateAsync(Token(nonce: "wrong"), options)).IsError, "nonce mismatch rejected");
Check((await validator.ValidateAsync(Token(expires: DateTime.UtcNow.AddMinutes(-1)), options)).IsError, "expired token rejected");
var pieces = Token().Split('.');
pieces[2] = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(256));
Check((await validator.ValidateAsync(string.Join('.', pieces), options)).IsError, "forged signature rejected");
var unsigned = handler.CreateToken(new SecurityTokenDescriptor { Issuer = IdentityLoginService.Issuer, Audience = IdentityLoginService.ClientId, Expires = DateTime.UtcNow.AddMinutes(5), Claims = new Dictionary<string, object> { ["sub"] = "test-sub", ["nonce"] = "nonce-123" } });
Check((await validator.ValidateAsync(unsigned, options)).IsError, "unsigned token rejected");

using var browser = new LoopbackBrowser(_ => { });
var callback = new Uri(browser.RedirectUri);
Check(callback.Host == "127.0.0.1" && callback.Port > 0 && callback.AbsolutePath == "/oauth/callback", "listener uses reserved random loopback port");
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var completion = browser.InvokeAsync(new BrowserOptions(IdentityLoginService.Issuer + "/auth?state=expected-state", browser.RedirectUri), deadline.Token);
Check((await http.GetAsync(browser.RedirectUri + "?state=wrong&code=x", deadline.Token)).StatusCode == HttpStatusCode.BadRequest, "wrong state does not consume listener");
Check((await http.GetAsync(callback.GetLeftPart(UriPartial.Authority) + "/wrong?state=expected-state", deadline.Token)).StatusCode == HttpStatusCode.BadRequest, "wrong callback path rejected");
Check((await http.GetAsync(browser.RedirectUri + "?state=expected-state&state=expected-state&code=x", deadline.Token)).StatusCode == HttpStatusCode.BadRequest, "duplicate state rejected");
Check((await http.GetAsync(browser.RedirectUri + "?state=expected-state&code=x", deadline.Token)).StatusCode == HttpStatusCode.OK, "correct callback accepted");
var completed = await completion;
Check(completed.ResultType == BrowserResultType.Success && completed.Response.Contains("state=expected-state"), "callback handed to OIDC SDK for exchange");
using var canceled = new LoopbackBrowser(_ => { });
using var canceledToken = new CancellationTokenSource(); canceledToken.Cancel();
Check((await canceled.InvokeAsync(new BrowserOptions(IdentityLoginService.Issuer + "/auth?state=x", canceled.RedirectUri), canceledToken.Token)).ResultType == BrowserResultType.Timeout, "cancel/timeout handled without hanging");
Check(!new CloudSettings(IdentityLoginService.ApiOrigin, "legacy-token", "legacy-user").UsesUnifiedIdentity, "legacy credentials require explicit unified login");
Check(!new CloudSettings("https://foreign.invalid", "access", "user", "refresh", DateTimeOffset.UtcNow.AddHours(1)).UsesUnifiedIdentity, "foreign origin cannot be a unified session");
using var syncHttp = new HttpClient();
using var sync = new CloudSyncService(syncHttp);
try
{
    await sync.UploadAsync("https://foreign.invalid", "fixture-access", "genshin", Array.Empty<GachaRecord>());
    throw new Exception("Foreign API must be rejected before a request");
}
catch (InvalidOperationException) { Check(true, "unified bearer token cannot be sent to a foreign API"); }

if (OperatingSystem.IsWindows())
{
    var directory = Path.Combine(Path.GetTempPath(), "startrace-identity-test-" + Guid.NewGuid().ToString("N"));
    var file = Path.Combine(directory, "client-data.json");
    try
    {
        var store = new LocalDataService(file);
        await store.MergeAsync(new FetchResult("genshin", "123456789", new[] {
            new GachaRecord { Uid = "123456789", RecordId = "fixture-record", PoolType = "301", ItemName = "fixture", Rarity = 3, PulledAt = "2026-11-25 00:00:00" }
        }));
        await store.QueueSyncAsync("genshin", "123456789");
        const string access = "fixture-desktop-access-123456789";
        const string refresh = "fixture-desktop-refresh-987654321";
        await store.SaveIdentitySettingsAsync(new IdentityLogin(access, refresh, DateTimeOffset.UtcNow.AddHours(1), "fixture-user"));
        var restored = await new LocalDataService(file).GetCloudSettingsAsync();
        Check(restored.UsesUnifiedIdentity && restored.AccessToken == access && restored.RefreshToken == refresh,
            "DPAPI credentials survive a client restart");
        var raw = await File.ReadAllTextAsync(file);
        Check(!raw.Contains(access) && !raw.Contains(refresh), "neither access nor refresh token is stored in plaintext");
        await store.ClearCloudLoginAsync();
        var cleared = await store.GetCloudSettingsAsync();
        Check(!cleared.UsesUnifiedIdentity && cleared.AccessToken == "" && cleared.RefreshToken == "", "logout clears both credentials");
        Check((await store.LoadLatestAsync())?.Records.Count == 1 && (await store.GetPendingAsync()).Count == 1,
            "logout preserves local records and pending sync queue");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
Console.WriteLine("Identity tests passed. Interactive WinUI and system-browser verification requires a Windows desktop.");
