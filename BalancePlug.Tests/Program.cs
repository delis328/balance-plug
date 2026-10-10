using System.IO;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;
using BalancePlug;
using BalancePlug.Core;

if (args.Contains("--ui-smoke")) return UiSmoke.Run();
if (args.Contains("--interactive")) return UiSmoke.Run(interactive: true);
if (args.Contains("--ui-performance")) return UiPerformance.Run();
if (args.Contains("--live"))
{
    var email = Environment.GetEnvironmentVariable("BALANCEPLUG_VERIFY_EMAIL") ?? throw new Exception("Missing verification email");
    var password = Environment.GetEnvironmentVariable("BALANCEPLUG_VERIFY_PASSWORD") ?? throw new Exception("Missing verification password");
    var directory = Environment.GetEnvironmentVariable("BALANCEPLUG_DATA_DIR") ?? throw new Exception("Explicit isolated data directory required");
    using var live = new BalanceApi();
    await live.LoginAsync(BalanceApi.DefaultSite, email, password);
    // Force only the local expiry timestamp to verify the real refresh protocol as well.
    live.RestoreSession(live.Session! with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
    var value = await live.GetBalanceAsync();
    var store = new LocalStore(directory);
    store.SaveSession(live.Session);
    store.SaveSettings(new AppSettings { SiteUrl = BalanceApi.DefaultSite });
    Console.WriteLine("LIVE VERIFIED login/me/refresh balance=" + BalanceApi.FormatBalance(value) + " at=" + DateTimeOffset.Now.ToString("O"));
    return 0;
}
var passed = 0;
var failures = new List<string>();
async Task Test(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Equal<T>(T expected, T actual)
{ if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
async Task Throws<T>(Func<Task> action) where T : Exception
{ try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
const string Site = "https://balance.test";
AccountSession Session(bool expired = false) => new(Site, "old-access", "old-refresh", DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1));
const string Pair = "{\"code\":0,\"data\":{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":86400}}";
string Me(string value) => "{\"code\":0,\"data\":{\"balance\":" + value + "}}";

foreach (var value in new[] { "17.39964736", "0", "-2.345", "0.00000000000000000001" })
{
    await Test("decimal balance " + value, async () =>
    {
        using var api = new BalanceApi(new ScriptedHandler((_, _) => Task.FromResult(Response(Me(value)))));
        api.RestoreSession(Session());
        Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), await api.GetBalanceAsync());
    });
}
await Test("format has invariant dollar and two decimals", () =>
{
    Equal("$17.40", BalanceApi.FormatBalance(17.39964736m)); Equal("$0.00", BalanceApi.FormatBalance(0));
    Equal("$-2.35", BalanceApi.FormatBalance(-2.345m)); return Task.CompletedTask;
});
foreach (var json in new[] { "{\"code\":0,\"data\":{}}", "{\"code\":0,\"data\":{\"balance\":\"5\"}}", "{\"code\":0,\"data\":{\"balance\":null}}", "{\"code\":\"0\",\"data\":{}}", "{\"code\":0,\"data\":null}", "<html>unavailable</html>" })
{
    await Test("reject malformed response " + json, async () =>
    {
        using var api = new BalanceApi(new ScriptedHandler((_, _) => Task.FromResult(Response(json))));
        api.RestoreSession(Session()); await Throws<BalanceException>(async () => await api.GetBalanceAsync());
    });
}
await Test("endpoint and authenticated headers", async () =>
{
    using var api = new BalanceApi(new ScriptedHandler((request, _) =>
    {
        Equal("/api/v1/auth/me", request.RequestUri!.AbsolutePath); Equal(HttpMethod.Get, request.Method);
        Equal("Bearer old-access", request.Headers.Authorization!.ToString());
        Equal("1", request.Headers.GetValues("X-User-UI-Request").Single());
        return Task.FromResult(Response(Me("1")));
    }));
    api.RestoreSession(Session()); await api.GetBalanceAsync();
});
await Test("login validates balance and emits session only after success", async () =>
{
    var calls = 0; var emitted = 0;
    using var api = new BalanceApi(new ScriptedHandler(async (request, _) =>
    {
        calls++;
        if (calls == 1)
        {
            Equal("/api/v1/auth/login", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Equal("test@example.test", body.RootElement.GetProperty("email").GetString());
            Equal("test-only", body.RootElement.GetProperty("password").GetString());
            return Response(Pair);
        }
        Equal("Bearer new-access", request.Headers.Authorization!.ToString()); return Response(Me("8"));
    }));
    api.SessionChanged += _ => emitted++;
    await api.LoginAsync(Site + "/", "test@example.test", "test-only");
    Equal(2, calls); Equal(1, emitted); Equal("new-refresh", api.Session!.RefreshToken);
});
await Test("wrong password retains existing session", async () =>
{
    using var api = new BalanceApi(new ScriptedHandler((_, _) => Task.FromResult(Response("{}", HttpStatusCode.Unauthorized))));
    api.RestoreSession(Session()); await Throws<BalanceException>(() => api.LoginAsync(Site, "test@example.test", "wrong"));
    Equal("old-access", api.Session!.AccessToken);
});
await Test("2FA rejected without replacing existing session", async () =>
{
    using var api = new BalanceApi(new ScriptedHandler((_, _) => Task.FromResult(Response("{\"code\":0,\"data\":{\"requires_2fa\":true}}"))));
    api.RestoreSession(Session()); await Throws<BalanceException>(() => api.LoginAsync(Site, "test@example.test", "test-only"));
    Equal("old-access", api.Session!.AccessToken);
});
await Test("401 refreshes rotating pair and retries once", async () =>
{
    var calls = 0; var events = 0;
    using var api = new BalanceApi(new ScriptedHandler(async (request, _) =>
    {
        calls++;
        if (calls == 1) return Response("{}", HttpStatusCode.Unauthorized);
        if (calls == 2)
        {
            Equal("/api/v1/auth/refresh", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Equal("old-refresh", body.RootElement.GetProperty("refresh_token").GetString()); return Response(Pair);
        }
        Equal("Bearer new-access", request.Headers.Authorization!.ToString()); return Response(Me("42.5"));
    }));
    api.RestoreSession(Session()); api.SessionChanged += _ => events++;
    Equal(42.5m, await api.GetBalanceAsync()); Equal(3, calls); Equal(1, events); Equal("new-refresh", api.Session!.RefreshToken);
});
await Test("known expired session refreshes before querying", async () =>
{
    var calls = 0;
    using var api = new BalanceApi(new ScriptedHandler((request, _) =>
    {
        calls++; Equal(calls == 1 ? "/api/v1/auth/refresh" : "/api/v1/auth/me", request.RequestUri!.AbsolutePath);
        return Task.FromResult(Response(calls == 1 ? Pair : Me("2")));
    }));
    api.RestoreSession(Session(true)); Equal(2m, await api.GetBalanceAsync()); Equal(2, calls);
});
await Test("refresh refusal clears session", async () =>
{
    var changes = 0;
    using var api = new BalanceApi(new ScriptedHandler((_, _) => Task.FromResult(Response("{}", HttpStatusCode.Unauthorized))));
    api.RestoreSession(Session(true)); api.SessionChanged += _ => changes++;
    await Throws<SessionExpiredException>(async () => await api.GetBalanceAsync()); Equal<AccountSession?>(null, api.Session); Equal(1, changes);
});
await Test("second 401 does not loop", async () =>
{
    var calls = 0;
    using var api = new BalanceApi(new ScriptedHandler((_, _) =>
    { calls++; return Task.FromResult(calls == 2 ? Response(Pair) : Response("{}", HttpStatusCode.Unauthorized)); }));
    api.RestoreSession(Session()); await Throws<SessionExpiredException>(async () => await api.GetBalanceAsync());
    Equal(3, calls); Equal<AccountSession?>(null, api.Session);
});
foreach (var code in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
{
    await Test("transient refresh keeps session " + code, async () =>
    {
        using var api = new BalanceApi(new ScriptedHandler((_, _) => Task.FromResult(Response("{}", code))));
        api.RestoreSession(Session(true)); await Throws<BalanceException>(async () => await api.GetBalanceAsync()); Equal("old-refresh", api.Session!.RefreshToken);
    });
}
await Test("timeout is friendly and preserves session", async () =>
{
    using var api = new BalanceApi(new ScriptedHandler((_, _) => throw new TaskCanceledException()));
    api.RestoreSession(Session()); await Throws<BalanceException>(async () => await api.GetBalanceAsync()); Equal("old-access", api.Session!.AccessToken);
});
await Test("caller cancellation is not disguised as timeout", async () =>
{
    using var api = new BalanceApi(new ScriptedHandler(async (_, ct) => { await Task.Delay(10000, ct); return Response(Me("1")); }));
    api.RestoreSession(Session()); using var ct = new CancellationTokenSource(25);
    await Throws<OperationCanceledException>(async () => await api.GetBalanceAsync(ct.Token));
});
await Test("queries and rotating refresh never overlap", async () =>
{
    var active = 0; var max = 0; var refresh = 0;
    using var api = new BalanceApi(new ScriptedHandler(async (request, ct) =>
    {
        active++; max = Math.Max(max, active); await Task.Delay(10, ct); active--;
        if (request.RequestUri!.AbsolutePath.EndsWith("refresh")) { refresh++; return Response(Pair); }
        return Response(Me("1"));
    }));
    api.RestoreSession(Session(true)); await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => api.GetBalanceAsync()));
    Equal(1, max); Equal(1, refresh);
});
await Test("query before login never requests network", async () =>
{
    using var api = new BalanceApi(new ScriptedHandler((_, _) => throw new Exception("Network should not run")));
    await Throws<SessionExpiredException>(async () => await api.GetBalanceAsync());
});
await Test("display failure keeps amount and success timestamp", () =>
{
    var view = new BalanceDisplay(); Equal("—", view.AmountText); view.Success(17.4m); var time = view.UpdatedAt;
    view.Fail("offline"); Equal("$17.40", view.AmountText); Equal(time, view.UpdatedAt);
    view.Expire(); Equal("$17.40", view.AmountText); Equal(time, view.UpdatedAt); view.Reset(); Equal("—", view.AmountText);
    return Task.CompletedTask;
});
await Test("only HTTPS root addresses accepted", async () =>
{
    foreach (var site in new[] { "http://test", "https://test/api", "https://u:p@test", "https://test?token=x", "not a url" })
        await Throws<BalanceException>(() => Task.FromResult(BalanceApi.NormalizeSite(site)));
    Equal(Site, BalanceApi.NormalizeSite(Site + "/"));
});
await Test("invalid settings normalize safely", () =>
{
    var settings = new AppSettings { SiteUrl = "bad", Theme = "bad", Size = "bad", Left = double.NaN, Top = double.PositiveInfinity };
    settings.Normalize(); Equal(BalanceApi.DefaultSite, settings.SiteUrl); Equal("Blue", settings.Theme); Equal(1d, settings.BubbleScale); Equal(1d, settings.MascotScale);
    Equal<double?>(null, settings.Left); Equal<double?>(null, settings.Top); return Task.CompletedTask;
});
await Test("atomic settings and DPAPI session persistence", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "BalancePlug-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new LocalStore(dir); var settings = new AppSettings { Theme = "Dark", ClickThrough = true, Left = -400 };
        store.SaveSettings(settings); Equal("Blue", store.LoadSettings(out var corrupt).Theme); Equal(false, corrupt);
        store.SaveSettings(new AppSettings { BubbleScale = .8, MascotScale = 1.2 }); Equal(.8, store.LoadSettings(out corrupt).BubbleScale);
        var session = Session(); store.SaveSession(session); Equal(session, store.LoadSession(out corrupt)); Equal(false, corrupt);
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "session.dat")));
        if (text.Contains("old-access") || text.Contains("old-refresh")) throw new Exception("Plaintext secret leaked");
        Equal(0, Directory.GetFiles(dir, "*.tmp").Length);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "broken"); Equal("Blue", store.LoadSettings(out corrupt).Theme); Equal(true, corrupt);
        File.WriteAllText(Path.Combine(dir, "session.dat"), "broken"); Equal<AccountSession?>(null, store.LoadSession(out corrupt)); Equal(true, corrupt);
        store.SaveSession(null); Equal(false, File.Exists(Path.Combine(dir, "session.dat")));
        return Task.CompletedTask;
    }
    finally
    {
        var resolved = Path.GetFullPath(dir);
        if (!resolved.StartsWith(Path.Combine(Path.GetTempPath(), "BalancePlug-tests-"), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
});

await Test("incomplete encrypted session cannot crash restoration", async () =>
{
    using var api = new BalanceApi(new ScriptedHandler((_, _) => throw new Exception("No network expected")));
    var session = JsonSerializer.Deserialize<AccountSession>("{}")!;
    await Throws<BalanceException>(() => { api.RestoreSession(session); return Task.CompletedTask; });
});
await Test("refresh bad-request expires instead of looping", async () =>
{
    using var api = new BalanceApi(new ScriptedHandler((_, _) => Task.FromResult(Response("{}", HttpStatusCode.BadRequest))));
    api.RestoreSession(Session(true)); await Throws<SessionExpiredException>(async () => await api.GetBalanceAsync());
    Equal<AccountSession?>(null, api.Session);
});
await Test("balance decrease uses last success, not failed query", () =>
{
    var view = new BalanceDisplay(); Equal<decimal?>(null, view.Success(10m)); Equal<decimal?>(null, view.Success(10m));
    Equal(.00000123m, view.Success(9.99999877m)); view.Fail("offline"); Equal(1.99999877m, view.Success(8m));
    Equal<decimal?>(null, view.Success(12m)); Equal(1m, view.Success(11m)); view.Reset(); Equal<decimal?>(null, view.Success(2m));
    return Task.CompletedTask;
});
await Test("decrease formatting keeps small values and precision", async () =>
{
    Equal("-$1.00", BalanceDisplay.FormatDecrease(1)); Equal("-$0.005", BalanceDisplay.FormatDecrease(.005m));
    Equal("-$0.000001", BalanceDisplay.FormatDecrease(.000001m)); Equal("-$<0.000001", BalanceDisplay.FormatDecrease(.0000009m));
    Equal("-$1.234568", BalanceDisplay.FormatDecrease(1.23456789m));
    await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(BalanceDisplay.FormatDecrease(0)));
});
await Test("independent scales normalize and invalid library IDs cannot escape directory", async () =>
{
    var settings = new AppSettings { BubbleScale = double.NaN, MascotScale = 5, ActiveMascotId = "../session",
        Mascots = new() { new MascotItem("../session", "bad"), new MascotItem(new string('a',64), "A"), new MascotItem(new string('a',64), "duplicate") } };
    settings.Normalize(); Equal(1d,settings.BubbleScale); Equal(2d,settings.MascotScale); Equal(1,settings.Mascots.Count); Equal<string?>(null,settings.ActiveMascotId);
    var store = new LocalStore(Path.GetTempPath()); await Throws<BalanceException>(() => Task.FromResult(store.GetMascotPath("../session")));
});
foreach (var useCustom in new[] { true, false })
await Test("legacy image and 80 percent settings migrate, custom=" + useCustom, () =>
{
    var dir = FixtureDirectory();
    try
    {
        var store = new LocalStore(dir); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"settings.json"), "{\"Theme\":\"Blue\",\"Size\":\"Small\",\"CustomMascot\":" + useCustom.ToString().ToLowerInvariant() + "}");
        var png = TestAssets.Png(24,32,1); store.SaveMascot(png); store.SaveSession(Session());
        var protectedBytes = File.ReadAllBytes(Path.Combine(dir,"session.dat"));
        var settings = store.LoadSettings(out var corrupt);
        Equal(false,corrupt); Equal(.8,settings.BubbleScale); Equal(.8,settings.MascotScale); Equal(1,settings.Mascots.Count);
        Equal("已有自定义形象",settings.Mascots[0].Name); Equal(useCustom ? settings.Mascots[0].Id : null, settings.ActiveMascotId);
        if (!png.SequenceEqual(File.ReadAllBytes(store.MascotPath))) throw new Exception("Legacy original changed");
        if (!protectedBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(dir,"session.dat")))) throw new Exception("Session changed by migration");
        var reloaded = store.LoadSettings(out corrupt); Equal(1,reloaded.Mascots.Count); Equal(settings.ActiveMascotId,reloaded.ActiveMascotId);
        return Task.CompletedTask;
    }
    finally { CleanFixture(dir); }
});
await Test("library deduplicates content, disambiguates names, and keeps images after source deletion", () =>
{
    var dir = FixtureDirectory();
    try
    {
        var store = new LocalStore(dir); var settings = new AppSettings();
        var first = store.ImportMascot(settings,TestAssets.Png(24,32,1),"girl.png");
        var second = store.ImportMascot(settings,TestAssets.Png(32,24,2),"girl.png");
        Equal("girl（2）",second.Name); Equal(2,settings.Mascots.Count);
        var repeat = store.ImportMascot(settings,TestAssets.Png(24,32,1),"different-name.png"); Equal(first.Id,repeat.Id); Equal(2,settings.Mascots.Count);
        var source = Path.Combine(dir,"source.png"); File.WriteAllBytes(source,TestAssets.Png(24,32,1)); File.Delete(source);
        if (!File.Exists(store.GetMascotPath(first.Id))) throw new Exception("Cached image missing");
        settings.ActiveMascotId = second.Id; store.SaveSettings(settings); Equal(second.Id,store.LoadSettings(out _).ActiveMascotId);
        settings.ActiveMascotId = null; store.SaveSettings(settings); Equal<string?>(null,store.LoadSettings(out _).ActiveMascotId); Equal(2,store.LoadSettings(out _).Mascots.Count);
        return Task.CompletedTask;
    }
    finally { CleanFixture(dir); }
});
await Test("invalid image and failed settings save do not replace selection", async () =>
{
    var dir = FixtureDirectory();
    try
    {
        var store = new LocalStore(dir); var settings = new AppSettings();
        var first = store.ImportMascot(settings,TestAssets.Png(20,20,1),"first.png");
        await Throws<BalanceException>(() => { store.ImportMascot(settings,new byte[] {1,2,3},"bad.png"); return Task.CompletedTask; });
        Equal(first.Id,settings.ActiveMascotId); Equal(1,settings.Mascots.Count);
        using (var locked = File.Open(Path.Combine(dir,"settings.json"),FileMode.Open,FileAccess.Read,FileShare.None))
            {
                var failed = false;
                try { store.ImportMascot(settings,TestAssets.Png(20,20,2),"second.png"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                Equal(true,failed);
            }
        Equal(first.Id,settings.ActiveMascotId); Equal(1,settings.Mascots.Count); Equal(1,store.LoadSettings(out _).Mascots.Count);
        Equal(1,Directory.GetFiles(Path.Combine(dir,"mascots"),"*.png").Length);
        Equal(0,Directory.GetFiles(dir,"*.tmp",SearchOption.AllDirectories).Length);
    }
    finally { CleanFixture(dir); }
});
await Test("corrupt legacy PNG does not discard valid settings or session", () =>
{
    var dir = FixtureDirectory();
    try
    {
        var store = new LocalStore(dir); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"settings.json"),"{\"Theme\":\"Dark\",\"Size\":\"Large\",\"CustomMascot\":true}");
        store.SaveMascot(new byte[] {1,2,3}); store.SaveSession(Session());
        var loaded = store.LoadSettings(out var corrupt); Equal(false,corrupt); Equal("Blue",loaded.Theme); Equal(1.2,loaded.BubbleScale);
        Equal(0,loaded.Mascots.Count); Equal<string?>(null,loaded.ActiveMascotId); Equal(true,store.MigrationNotice is not null); Equal(Session().AccessToken,store.LoadSession(out _)!.AccessToken);
        return Task.CompletedTask;
    }
    finally { CleanFixture(dir); }
});

string FixtureDirectory() => Path.Combine(Path.GetTempPath(),"BalancePlug-tests-" + Guid.NewGuid().ToString("N"));
void CleanFixture(string dir)
{
    var resolved = Path.GetFullPath(dir);
    if (!resolved.StartsWith(Path.Combine(Path.GetTempPath(),"BalancePlug-tests-"),StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup");
    if (Directory.Exists(resolved)) Directory.Delete(resolved,true);
}

await Test("deferred migration keeps old settings intact until window is ready", () =>
{
    var dir = FixtureDirectory();
    try
    {
        var store = new LocalStore(dir); Directory.CreateDirectory(dir);
        var old = "{\"Theme\":\"Blue\",\"Size\":\"Small\",\"CustomMascot\":true}";
        File.WriteAllText(Path.Combine(dir,"settings.json"),old); store.SaveMascot(TestAssets.Png(32,32,1));
        var settings = store.LoadSettings(out var corrupt,deferMigration:true);
        Equal(false,corrupt); Equal(true,store.MigrationPending); Equal(.8,settings.BubbleScale);
        Equal(old,File.ReadAllText(Path.Combine(dir,"settings.json"))); Equal(0,settings.Mascots.Count);
        store.CompleteMigration(settings); Equal(false,store.MigrationPending); Equal(1,settings.Mascots.Count);
        Equal(settings.Mascots[0].Id,settings.ActiveMascotId); return Task.CompletedTask;
    }
    finally { CleanFixture(dir); }
});
await Test("topmost defaults on for old settings and false survives reload", () =>
{
    var dir = FixtureDirectory();
    try
    {
        Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir,"settings.json"),"{\"Theme\":\"Blue\"}");
        var store = new LocalStore(dir); var settings = store.LoadSettings(out _); Equal(true,settings.Topmost);
        settings.Topmost=false; store.SaveSettings(settings); Equal(false,store.LoadSettings(out _).Topmost); return Task.CompletedTask;
    }
    finally { CleanFixture(dir); }
});
await Test("explicit imported name preserves dots and rename persists without changing image or selection", () =>
{
    var dir = FixtureDirectory();
    try
    {
        var store = new LocalStore(dir); var settings = new AppSettings(); var png = TestAssets.Png(24,24,1);
        var item = store.ImportMascot(settings,png,"file.png",displayName:"  鲸鱼娘.v2  "); Equal("鲸鱼娘.v2",item.Name);
        var renamed = store.RenameMascot(settings,item.Id,"  公司形象  "); Equal("公司形象",renamed.Name);
        Equal(item.Id,settings.ActiveMascotId); Equal(item.Id,renamed.Id); Equal(1,settings.Mascots.Count);
        if(!png.SequenceEqual(File.ReadAllBytes(store.GetMascotPath(item.Id)))) throw new Exception("Image changed during rename");
        Equal("公司形象",store.LoadSettings(out _).Mascots[0].Name);
        var reused=store.ImportMascot(settings,png,"file.png",displayName:"家中形象"); Equal(item.Id,reused.Id); Equal("家中形象",reused.Name); Equal(1,settings.Mascots.Count);
        return Task.CompletedTask;
    }
    finally { CleanFixture(dir); }
});
await Test("rename disambiguates names and rejects empty, long, control or missing entries", async () =>
{
    var dir = FixtureDirectory();
    try
    {
        var store = new LocalStore(dir); var settings=new AppSettings();
        var first=store.ImportMascot(settings,TestAssets.Png(20,20,1),"a.png",displayName:"鲸鱼");
        var second=store.ImportMascot(settings,TestAssets.Png(20,20,2),"b.png",displayName:"其他");
        Equal("鲸鱼（2）",store.RenameMascot(settings,second.Id,"鲸鱼").Name);
        Equal("鲸鱼（2）",store.RenameMascot(settings,second.Id,"鲸鱼（2）").Name);
        foreach(var name in new[] {"   ",new string('x',41),"a\nb"})
            await Throws<BalanceException>(() => {store.RenameMascot(settings,first.Id,name);return Task.CompletedTask;});
        await Throws<BalanceException>(() => {store.RenameMascot(settings,new string('f',64),"missing");return Task.CompletedTask;});
        Equal("鲸鱼",settings.Mascots[0].Name); Equal(second.Id,settings.ActiveMascotId);
    }
    finally { CleanFixture(dir); }
});
await Test("rename and duplicate-import naming roll back on save failure", () =>
{
    var dir=FixtureDirectory();
    try
    {
        var store=new LocalStore(dir);var settings=new AppSettings();var png=TestAssets.Png(20,20,1);
        var first=store.ImportMascot(settings,png,"original.png");
        using(var locked=File.Open(Path.Combine(dir,"settings.json"),FileMode.Open,FileAccess.Read,FileShare.None))
        {
            foreach(var importing in new[] {false,true})
            {
                var failed=false;
                try {if(importing)store.ImportMascot(settings,png,"original.png",displayName:"changed");else store.RenameMascot(settings,first.Id,"changed");}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){failed=true;}
                Equal(true,failed);Equal("original",settings.Mascots[0].Name);Equal(first.Id,settings.ActiveMascotId);
            }
        }
        Equal("original",store.LoadSettings(out _).Mascots[0].Name);return Task.CompletedTask;
    }
    finally {CleanFixture(dir);}
});
await Test("update interval and motion settings normalize safely", () =>
{
    var settings = new AppSettings { UpdateIntervalSeconds = 3, Theme = "Dark", BubbleMotion = new BubbleMotionSettings { StartX = -1, StartY = 3, TargetX = double.NaN, TargetY = .2, Speed = 999, Scale = .1 } };
    settings.Normalize(); Equal(15,settings.UpdateIntervalSeconds); Equal("Blue",settings.Theme); Equal(0,settings.BubbleMotion.StartX); Equal(1,settings.BubbleMotion.StartY); Equal(.28,settings.BubbleMotion.TargetX); Equal(300,settings.BubbleMotion.Speed); Equal(.5,settings.BubbleMotion.Scale); return Task.CompletedTask;
});
await Test("motion settings persist independently from bubble scale", () =>
{
    var dir=FixtureDirectory(); try { var store=new LocalStore(dir); var settings=new AppSettings { BubbleScale=.8, MascotScale=1.4, UpdateIntervalSeconds=315, BubbleMotion=new BubbleMotionSettings { StartX=.1,StartY=.9,TargetX=.8,TargetY=.1,Speed=75,Scale=1.25 } }; store.SaveSettings(settings); var loaded=store.LoadSettings(out _); Equal(.8,loaded.BubbleScale); Equal(1.4,loaded.MascotScale); Equal(315,loaded.UpdateIntervalSeconds); Equal(.1,loaded.BubbleMotion.StartX); Equal(75,loaded.BubbleMotion.Speed); Equal(1.25,loaded.BubbleMotion.Scale); return Task.CompletedTask; } finally { CleanFixture(dir); }
});
Console.WriteLine($"RESULT {passed} passed, {failures.Count} failed");
return failures.Count == 0 ? 0 : 1;

static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
{ Content = new StringContent(body, Encoding.UTF8, "application/json") };
sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
}







