using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using StarTrace.Client.Models;
using StarTrace.Client.Services;

static class WorkflowTests
{
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    public static async Task CheckSdkBrowserAsync()
    {
        using var issuer = new DiscoveryHandler();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new IdentityLoginService(
            () => new LoopbackBrowser(url => opened.TrySetResult(url)), issuer);
        var login = identity.LoginAsync(deadline.Token);
        if (await Task.WhenAny(opened.Task, login) == login) await login;
        var url = new Uri(await opened.Task.WaitAsync(deadline.Token));
        var query = url.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
        Check(url.GetLeftPart(UriPartial.Authority) == IdentityLoginService.Issuer &&
            query["response_type"] == "code" && query["code_challenge_method"] == "S256" &&
            query.ContainsKey("state") && query.ContainsKey("nonce") && issuer.ParCalls == 0,
            "actual SDK launches browser with state, nonce and S256 when optional PAR is advertised");
        deadline.Cancel();
        try { await login; throw new Exception("Canceled SDK login must not succeed"); }
        catch (OperationCanceledException) { Check(true, "actual SDK login cancellation closes the callback listener"); }
    }

    public static async Task CheckTransfersAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "startrace-transfer-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var local = new LocalDataService(Path.Combine(directory, "client-data.json"));
            var genshin = Records("genshin", 1002);
            var wuwa = Records("wuwa", 1121);
            await local.MergeAsync(genshin);
            await local.MergeAsync(wuwa);
            Check((await local.GetPendingAsync()).Count == 2, "offline and anonymous saves are queued across both games");
            var accounts = await local.GetAllAccountsAsync();
            using var backup = JsonDocument.Parse(BackupService.Serialize(accounts));
            var data = backup.RootElement;
            Check(data.GetProperty("format").GetString() == "startrace-backup" &&
                data.GetProperty("accounts").GetArrayLength() == 2 &&
                data.GetProperty("accounts").EnumerateArray().Sum(a => a.GetProperty("records").GetArrayLength()) == 2123,
                "full backup includes all games and UIDs in website-compatible format");
            Check(!data.TryGetProperty("ProtectedAccessToken", out _) && !data.TryGetProperty("RefreshToken", out _),
                "record export contains no login state");

            using var handler = new UploadHandler();
            using var api = new HttpClient(handler);
            using var cloud = new CloudSyncService(api, api);
            var transfer = new CloudTransferService(local, cloud);
            var settings = new CloudSettings(IdentityLoginService.ApiOrigin, "fixture-access", "fixture", "fixture-refresh", DateTimeOffset.UtcNow.AddHours(1));
            try
            {
                await transfer.UploadPendingAsync(new CloudSettings(IdentityLoginService.ApiOrigin, "", ""));
                throw new Exception("Anonymous upload must fail");
            }
            catch (CloudAuthenticationException)
            {
                Check(handler.Calls == 0 && (await local.GetPendingAsync()).Count == 2,
                    "missing authentication is explicit and never drops queued records");
            }
            handler.FailAtCall = 2;
            try { await transfer.UploadPendingAsync(settings); throw new Exception("Simulated upload must fail"); }
            catch (InvalidOperationException)
            {
                Check((await local.GetPendingAsync()).Count == 2 &&
                    (await local.GetAllAccountsAsync()).Sum(a => a.Records.Count) == 2123,
                    "mid-account upload failure retains the whole account queue and all local records");
            }
            Check(await transfer.UploadPendingAsync(settings) == 2123 &&
                (await local.GetPendingAsync()).Count == 0 && handler.Stored.Count == 2123,
                "retry confirms every record without duplicates and clears only completed queues");
            await local.QueueAllAccountsAsync();
            Check(await transfer.UploadPendingAsync(settings) == 2123 && handler.Stored.Count == 2123 &&
                handler.LargestBatch <= 1000 && handler.Games.SetEquals(new[] { "genshin", "wuwa" }),
                "one-click reupload covers all games, counts existing records and uses bounded batches");
            await local.QueueAllAccountsAsync();
            handler.RejectNext = true;
            try { await transfer.UploadPendingAsync(settings); throw new Exception("Unauthorized upload must fail"); }
            catch (CloudAuthenticationException)
            {
                Check((await local.GetPendingAsync()).Count == 2, "API authentication failure preserves upload queue");
            }
            handler.HealthStatus = HttpStatusCode.Forbidden;
            Check(!await cloud.IsServerReachableAsync(IdentityLoginService.ApiOrigin),
                "HTTP denial is not reported as an online service");

            using var boundaryHandler = new UploadHandler { KeepTimestampsWhole = true };
            using var boundaryApi = new HttpClient(boundaryHandler);
            using var boundaryCloud = new CloudSyncService(boundaryApi, boundaryApi);
            var boundary = Records("wuwa", 997).Records.Concat(Enumerable.Range(0, 13).Select(index =>
                new GachaRecord { Uid = "123456789", RecordId = "ten-pull-" + index, PoolType = "1",
                    ItemId = "fixture", Rarity = 3, PulledAt = "same-second-ten-pull" })).ToList();
            Check(await boundaryCloud.UploadAsync(settings.BaseUrl, settings.AccessToken, "wuwa", boundary) == 1010,
                "WuWa same-second records never split at the upload batch boundary");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static FetchResult Records(string game, int count) => new(game, "123456789",
        Enumerable.Range(0, count).Select(index => new GachaRecord
        {
            Uid = "123456789", RecordId = game + "-" + index, PoolType = game == "genshin" ? "301" : "1",
            ItemId = "fixture", ItemName = "fixture", Rarity = 3, PulledAt = "2026-11-25 " + index,
        }).ToList());

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        private readonly RSA _rsa = RSA.Create(2048);
        public int ParCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object payload;
            var origin = IdentityLoginService.Issuer;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/.well-known/openid-configuration":
                    payload = new {
                        issuer = origin, authorization_endpoint = origin + "/auth", token_endpoint = origin + "/token",
                        userinfo_endpoint = origin + "/me", jwks_uri = origin + "/jwks",
                        pushed_authorization_request_endpoint = origin + "/request", response_types_supported = new[] { "code" },
                        token_endpoint_auth_methods_supported = new[] { "none" }, id_token_signing_alg_values_supported = new[] { "RS256" },
                        code_challenge_methods_supported = new[] { "S256" },
                    };
                    break;
                case "/jwks":
                    var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa) { KeyId = "fixture-key" });
                    payload = new { keys = new[] { new { kty = "RSA", kid = "fixture-key", alg = "RS256", use = "sig", n = jwk.N, e = jwk.E } } };
                    break;
                case "/request":
                    ParCalls++;
                    return Task.FromResult(Json(HttpStatusCode.BadRequest, new { error = "invalid_request" }));
                default: throw new Exception("Unexpected discovery request");
            }
            return Task.FromResult(Json(HttpStatusCode.OK, payload));
        }
        protected override void Dispose(bool disposing) { if (disposing) _rsa.Dispose(); base.Dispose(disposing); }
    }

    private sealed class UploadHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int FailAtCall { get; set; }
        public bool RejectNext { get; set; }
        public int LargestBatch { get; private set; }
        public HttpStatusCode HealthStatus { get; set; } = HttpStatusCode.OK;
        public HashSet<string> Stored { get; } = [];
        public HashSet<string> Games { get; } = [];
        public bool KeepTimestampsWhole { get; set; }
        private readonly HashSet<string> _timestamps = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return Json(HealthStatus, new { });
            Calls++;
            if (RejectNext) { RejectNext = false; return Json(HttpStatusCode.Unauthorized, new { error = "请先登录" }); }
            if (Calls == FailAtCall) return Json(HttpStatusCode.ServiceUnavailable, new { error = "fixture failure" });
            if (request.Headers.Authorization?.Parameter != "fixture-access") throw new Exception("Missing bearer");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            var game = root.GetProperty("game").GetString()!;
            var uid = root.GetProperty("uid").GetString();
            var rows = root.GetProperty("records");
            LargestBatch = Math.Max(LargestBatch, rows.GetArrayLength());
            Games.Add(game);
            var written = 0;
            if (KeepTimestampsWhole)
                foreach (var time in rows.EnumerateArray().Select(row => row.GetProperty("pulledAt").GetString()!).Distinct())
                    if (!_timestamps.Add(time)) throw new Exception("Same-second WuWa records crossed a batch boundary");
            foreach (var row in rows.EnumerateArray())
            {
                if (row.GetProperty("uid").GetString() != uid) throw new Exception("Mixed UID upload");
                if (Stored.Add(game + ":" + uid + ":" + row.GetProperty("recordId").GetString())) written++;
            }
            return Json(HttpStatusCode.OK, new { written, ignoredDuplicates = rows.GetArrayLength() - written });
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
