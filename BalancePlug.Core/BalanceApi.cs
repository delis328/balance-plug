using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BalancePlug.Core;

public sealed record AccountSession(string SiteUrl, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public sealed class BalanceException(string message) : Exception(message);
public sealed class SessionExpiredException() : Exception("登录已失效，请重新登录");

public sealed class BalanceApi : IDisposable
{
    public const string DefaultSite = "https://api.ikungod.online";
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);
    public AccountSession? Session { get; private set; }
    public event Action<AccountSession?>? SessionChanged;

    public BalanceApi(HttpMessageHandler? handler = null)
    {
        http = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(10);
        http.MaxResponseContentBufferSize = 1024 * 1024;
    }

    public static string NormalizeSite(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new BalanceException("站点须为 HTTPS 根地址，例如 https://api.ikungod.online");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public void RestoreSession(AccountSession session)
    {
        var normalized = NormalizeSite(session.SiteUrl);
        if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.RefreshToken) || session.ExpiresAt == default)
            throw new BalanceException("会话数据不完整，请重新登录");
        Session = session with { SiteUrl = normalized };
    }

    public async Task LoginAsync(string site, string email, string password, CancellationToken ct = default)
    {
        site = NormalizeSite(site);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new BalanceException("请填写邮箱和密码");
        await gate.WaitAsync(ct);
        try
        {
            var data = await SendAsync(site, "auth/login", HttpMethod.Post,
                new { email = email.Trim(), password }, null, ct);
            if (data.TryGetProperty("requires_2fa", out var twoFactor) && twoFactor.ValueKind == JsonValueKind.True)
                throw new BalanceException("此账户需要两步验证，首版暂不支持，请勿关闭账户安全设置");
            var candidate = ReadSession(data, site);
            // Verify the actual balance endpoint before replacing a previously working session.
            var me = await SendAsync(site, "auth/me", HttpMethod.Get, null, candidate.AccessToken, ct);
            ReadBalance(me);
            SetSession(candidate);
        }
        finally { gate.Release(); }
    }

    public async Task<decimal> GetBalanceAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Session is null) throw new SessionExpiredException();
            var refreshed = false;
            if (Session.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
            {
                await RefreshAsync(ct);
                refreshed = true;
            }
            try { return await FetchBalanceAsync(ct); }
            catch (SessionExpiredException) when (!refreshed)
            {
                await RefreshAsync(ct);
                try { return await FetchBalanceAsync(ct); }
                catch (SessionExpiredException) { SetSession(null); throw; }
            }
            catch (SessionExpiredException) { SetSession(null); throw; }
        }
        finally { gate.Release(); }
    }

    private async Task<decimal> FetchBalanceAsync(CancellationToken ct)
    {
        var session = Session ?? throw new SessionExpiredException();
        return ReadBalance(await SendAsync(session.SiteUrl, "auth/me", HttpMethod.Get, null, session.AccessToken, ct));
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var session = Session ?? throw new SessionExpiredException();
        try
        {
            var data = await SendAsync(session.SiteUrl, "auth/refresh", HttpMethod.Post,
                new { refresh_token = session.RefreshToken }, null, ct);
            SetSession(ReadSession(data, session.SiteUrl));
        }
        catch (SessionExpiredException) { SetSession(null); throw; }
        // Transient network/429/5xx errors retain the session for a later scheduled retry.
    }

    private void SetSession(AccountSession? value)
    {
        Session = value;
        SessionChanged?.Invoke(value);
    }

    private async Task<JsonElement> SendAsync(string site, string path, HttpMethod method,
        object? body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{site}/api/v1/{path}");
        request.Headers.AcceptLanguage.ParseAdd("zh");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (path == "auth/me") request.Headers.Add("X-User-UI-Request", "1");
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                if (path == "auth/login") throw new BalanceException("登录失败，请检查邮箱和密码或账户状态");
                throw new SessionExpiredException();
            }
            if (path == "auth/refresh" && response.StatusCode == HttpStatusCode.BadRequest) throw new SessionExpiredException();
            if ((int)response.StatusCode == 429) throw new BalanceException("请求过于频繁，请稍后重试");
            if (!response.IsSuccessStatusCode) throw new BalanceException($"服务器返回 HTTP {(int)response.StatusCode}");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var codeNumber)) throw new BalanceException("接口返回格式不符合 Sub2API 协议");
            if (codeNumber != 0)
            {
                if (path == "auth/refresh") throw new SessionExpiredException();
                throw new BalanceException(path == "auth/login" ? "登录失败，请检查账户信息" : "查询失败，服务器未返回有效数据");
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new BalanceException("接口缺少有效 data 对象");
            return data.Clone();
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new BalanceException("请求超时，请检查网络"); }
        catch (HttpRequestException) { throw new BalanceException("网络连接失败，请检查站点和网络"); }
        catch (JsonException) { throw new BalanceException("服务器返回非 JSON 或损坏数据"); }
    }

    private static AccountSession ReadSession(JsonElement data, string site)
    {
        if (!data.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(access.GetString()) || !data.TryGetProperty("refresh_token", out var refresh) ||
            refresh.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(refresh.GetString()) ||
            !data.TryGetProperty("expires_in", out var expiry) || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt32(out var seconds) || seconds <= 0)
            throw new BalanceException("接口返回的登录会话不完整");
        return new AccountSession(site, access.GetString()!, refresh.GetString()!, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    public static decimal ReadBalance(JsonElement data)
    {
        if (!data.TryGetProperty("balance", out var balance) || balance.ValueKind != JsonValueKind.Number ||
            !balance.TryGetDecimal(out var value)) throw new BalanceException("接口缺少有效账户 balance，未将其当作零余额");
        return value;
    }

    public static string FormatBalance(decimal value) => "$" + value.ToString("F2", CultureInfo.InvariantCulture);
    public void Dispose() => http.Dispose();
}



