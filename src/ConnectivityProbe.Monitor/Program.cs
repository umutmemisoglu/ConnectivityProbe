using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ConnectivityProbe;
using ConnectivityProbe.Monitor;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// step 1: Ayarları, tanım deposunu ve arka planda periyodik test yapan servisi ekliyoruz.
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.AddSingleton<DefinitionStore>();
builder.Services.AddSingleton<PodStateStore>();   // görülen / eksik pod'lar diske yazılır (data/pod-state.json)
builder.Services.AddSingleton<MonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitorService>());

// step 1b: Arayüz girişi (çerez). Çerezi şifreleyen anahtarlar veri klasöründe tutulur; Monitor yeniden başlayınca
//          kullanıcıların tekrar giriş yapması gerekmez.
var dataFile = builder.Configuration["Monitor:DataFile"] ?? "data/definitions.json";
var dataDir = Path.GetDirectoryName(Path.IsPathRooted(dataFile) ? dataFile : Path.Combine(builder.Environment.ContentRootPath, dataFile))!;
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "cpmonitor";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;          // başka sitelerden gelen isteklere çerez eklenmez (CSRF koruması)
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
});

var app = builder.Build();
var monitorOptions = app.Services.GetRequiredService<IOptions<MonitorOptions>>().Value;
var loginRequired = !string.IsNullOrEmpty(monitorOptions.AdminPassword);
if (!loginRequired)
    app.Logger.LogWarning("Monitor:AdminPassword is not set: the UI is only reachable from this machine (localhost). Set it before exposing the Monitor.");

// step 2: Erişim kapısı.
//   - /api/agent/*: Strict pod'ların uçları; giriş değil uygulama anahtarı (X-ConnectivityProbe-AppKey) ister.
//   - Giriş sayfası ve giriş uçları: herkese açık.
//   - Geri kalan her şey (arayüz, tanımlar, monitör): şifre tanımlıysa giriş yapmış kullanıcı; tanımlı değilse yalnızca localhost.
app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api/agent") || path.StartsWithSegments("/api/auth")
        || path == "/login.html" || path == "/login.js" || path == "/style.css")
    {
        await next(ctx);
        return;
    }

    bool allowed = loginRequired
        ? ctx.User.Identity?.IsAuthenticated == true
        : ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
    if (allowed)
    {
        await next(ctx);
        return;
    }

    if (!loginRequired)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = "Monitor:AdminPassword tanımlı değil; arayüze yalnızca Monitor'ün çalıştığı makineden erişilebilir." });
    }
    else if (path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Giriş gerekli" });
    }
    else
    {
        ctx.Response.Redirect("/login.html");
    }
});

// step 3: Arayüz (wwwroot/index.html) kök adreste yayınlanır.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // "no-cache": tarayıcı dosyayı her seferinde sunucuya sorar (ETag ile değişmediyse 304 döner). Böylece arayüz
    // güncellendiğinde kullanıcılar eski app.js/style.css'i önbellekten görmez.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});

var api = app.MapGroup("/api");

// ---------------------------------------------------------------------------------------------
// Giriş
// ---------------------------------------------------------------------------------------------

// Giriş durumu: arayüz çıkış düğmesini göstermek ve oturumu kontrol etmek için kullanır.
api.MapGet("/auth/me", (HttpContext ctx) => new
{
    loginRequired,
    authenticated = !loginRequired || ctx.User.Identity?.IsAuthenticated == true,
    user = ctx.User.Identity?.Name
});

api.MapPost("/auth/login", async (LoginInput input, HttpContext ctx) =>
{
    if (!loginRequired) return Results.Ok();
    // Sabit sürede karşılaştırma (zamanlama saldırısına karşı); hatalı denemede kaba kuvvet denemelerini yavaşlatmak için bekleme.
    var ok = FixedEquals(input.Username, monitorOptions.AdminUser) & FixedEquals(input.Password, monitorOptions.AdminPassword);
    if (!ok)
    {
        await Task.Delay(1000);
        return Results.Json(new { error = "Kullanıcı adı veya şifre hatalı" }, statusCode: StatusCodes.Status401Unauthorized);
    }
    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, monitorOptions.AdminUser) }, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok();
});

api.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

// ---------------------------------------------------------------------------------------------
// Pod'ların (ConnectivityProbeAgent) uçları. Girişten bağımsızdır; uygulama anahtarı başlıkta gelir.
// ---------------------------------------------------------------------------------------------

// Pod bildirimi: "bu pod yaşıyor" (+ varsa test sonuçları). Monitor bu anahtarı ilk kez görüyorsa uygulamayı kendiliğinden
// kaydeder ("Atanmamış" altında). Yanıtta uygulamaya atanmış bağlantılar ve test ayarları döner.
api.MapPost("/agent/v2/report", (HttpContext ctx, AgentReport report, DefinitionStore store, MonitorService monitor) =>
{
    var key = ctx.Request.Headers[ConnectivityProbeOptions.AppKeyHeader].ToString().Trim();
    if (ValidateAppKey(key) is { } keyError) return Results.BadRequest(new { error = keyError });
    if (string.IsNullOrWhiteSpace(report.Pod?.InstanceId)) return Results.BadRequest(new { error = "pod.instanceId is required" });

    var defs = store.Snapshot();
    var target = FindApp(defs, key);
    if (target == null)
    {
        // Kayıt: aynı anda birden fazla pod gelebilir; kilit altında tekrar kontrol ediyoruz.
        var name = string.IsNullOrWhiteSpace(report.AppName) ? key : report.AppName.Trim();
        target = store.Mutate(d =>
        {
            var existing = FindApp(d, key);
            if (existing != null) return existing;
            var created = new AppDefinition { Id = NewId(), Name = name.Length > 100 ? name[..100] : name, AppKey = key, RegisteredAtUtc = DateTime.UtcNow };
            d.Apps.Add(created);
            return created;
        });
        app.Logger.LogInformation("Application {Name} registered with key {Key}", target.Name, key);
        defs = store.Snapshot();
    }
    return Results.Ok(monitor.AcceptAgentReport(target, defs, report, SourceIp(ctx)));
});

// Pod düzgün kapanıyor: alarm vermeden listeden çıkarılır.
api.MapPost("/agent/v2/goodbye", (HttpContext ctx, AgentGoodbye input, DefinitionStore store, MonitorService monitor) =>
{
    var target = FindApp(store.Snapshot(), ctx.Request.Headers[ConnectivityProbeOptions.AppKeyHeader].ToString().Trim());
    if (target != null && !string.IsNullOrWhiteSpace(input.InstanceId)) monitor.AgentGoodbye(target.Id, input.InstanceId);
    return Results.NoContent();
});

// Tüm tanımları (birimler, ekipler, uygulamalar, bağlantı havuzu, cluster'lar) döner.
api.MapGet("/definitions", (DefinitionStore store) =>
{
    var d = store.Snapshot();
    return new DefinitionsView(d.Units, d.Teams,
        d.Apps.Select(AppView.From).ToList(), d.Connections.Select(ConnectionView.From).ToList(), d.Clusters);
});

// Cluster'a anlamlı bir ad verir (ör. "Prod İstanbul"); ilk görüldüğünde "Cluster N" adını alır.
api.MapPut("/clusters/{key}", (string key, ClusterInput input, DefinitionStore store) =>
{
    if (ValidateName(input.Name) is { } error) return Results.BadRequest(new { error });
    var cluster = store.Mutate(d =>
    {
        var c = d.Clusters.FirstOrDefault(x => x.Key == key);
        if (c != null) c.Name = input.Name!.Trim();
        return c;
    });
    return cluster == null ? Results.NotFound() : Results.Ok(cluster);
});

// ---------------------------------------------------------------------------------------------
// Birimler ve ekipler: Birim (ör. Efatura) -> Ekip -> Uygulama
// ---------------------------------------------------------------------------------------------

api.MapPost("/units", (UnitInput input, DefinitionStore store) =>
{
    if (ValidateName(input.Name) is { } error) return Results.BadRequest(new { error });
    var unit = new UnitDefinition { Id = NewId(), Name = input.Name!.Trim() };
    store.Mutate(d => { d.Units.Add(unit); return 0; });
    return Results.Ok(unit);
});

api.MapPut("/units/{id}", (string id, UnitInput input, DefinitionStore store) =>
{
    if (ValidateName(input.Name) is { } error) return Results.BadRequest(new { error });
    var unit = store.Mutate(d =>
    {
        var u = d.Units.FirstOrDefault(x => x.Id == id);
        if (u != null) u.Name = input.Name!.Trim();
        return u;
    });
    return unit == null ? Results.NotFound() : Results.Ok(unit);
});

// Birim, içinde ekip varken silinemez (ekipler ve uygulamaları yanlışlıkla kaybolmasın).
api.MapDelete("/units/{id}", (string id, DefinitionStore store) =>
{
    var result = store.Mutate(d =>
    {
        if (d.Teams.Any(t => t.UnitId == id)) return "Birimde ekip var; önce ekipleri silin veya başka birime taşıyın";
        return d.Units.RemoveAll(u => u.Id == id) > 0 ? null : "notfound";
    });
    return result == null ? Results.NoContent() : result == "notfound" ? Results.NotFound() : Results.BadRequest(new { error = result });
});

api.MapPost("/teams", (TeamInput input, DefinitionStore store) =>
{
    if (ValidateName(input.Name) is { } error) return Results.BadRequest(new { error });
    var team = store.Mutate(d =>
    {
        if (d.Units.All(u => u.Id != input.UnitId)) return null;
        var t = new TeamDefinition { Id = NewId(), Name = input.Name!.Trim(), UnitId = input.UnitId! };
        d.Teams.Add(t);
        return t;
    });
    return team == null ? Results.BadRequest(new { error = "Birim bulunamadı" }) : Results.Ok(team);
});

// Ekibin adını veya birimini değiştirir.
api.MapPut("/teams/{id}", (string id, TeamInput input, DefinitionStore store) =>
{
    if (ValidateName(input.Name) is { } error) return Results.BadRequest(new { error });
    var result = store.Mutate(d =>
    {
        var t = d.Teams.FirstOrDefault(x => x.Id == id);
        if (t == null) return (Team: (TeamDefinition?)null, Error: (string?)"notfound");
        if (d.Units.All(u => u.Id != input.UnitId)) return (null, "Birim bulunamadı");
        t.Name = input.Name!.Trim();
        t.UnitId = input.UnitId!;
        return (t, null);
    });
    return result.Error == "notfound" ? Results.NotFound()
        : result.Error != null ? Results.BadRequest(new { error = result.Error })
        : Results.Ok(result.Team);
});

// Ekip, içinde uygulama veya bağlantı varken silinemez.
api.MapDelete("/teams/{id}", (string id, DefinitionStore store) =>
{
    var result = store.Mutate(d =>
    {
        if (d.Apps.Any(a => a.TeamId == id)) return "Ekipte uygulama var; önce uygulamaları silin veya başka ekibe taşıyın";
        if (d.Connections.Any(c => c.TeamId == id)) return "Ekibin bağlantı havuzunda bağlantı var; önce onları silin veya ortak havuza taşıyın";
        return d.Teams.RemoveAll(t => t.Id == id) > 0 ? null : "notfound";
    });
    return result == null ? Results.NoContent() : result == "notfound" ? Results.NotFound() : Results.BadRequest(new { error = result });
});

// ---------------------------------------------------------------------------------------------
// Bağlantı havuzu: ekip havuzları + ortak havuz (TeamId null)
// ---------------------------------------------------------------------------------------------

// Havuza yeni bağlantı ekler. Host ad, IP, ad:port veya tam URL olabilir. TeamId verilirse o ekibin havuzuna, verilmezse ortak
// havuza eklenir. TargetAppId: hedef de Monitor'e kayıtlı bir uygulamaysa onun kimliği (isteğe bağlı).
api.MapPost("/connections", (ConnectionInput input, DefinitionStore store) =>
{
    if (ValidateConnection(input) is { } error) return Results.BadRequest(new { error });

    var conn = new ConnectionDefinition
    {
        Id = NewId(), TeamId = NullIfEmpty(input.TeamId), Name = input.Name!.Trim(), Host = input.Host!.Trim(), Port = input.Port,
        TargetAppId = NullIfEmpty(input.TargetAppId)
    };
    var error2 = store.Mutate(d =>
    {
        if (conn.TeamId != null && d.Teams.All(t => t.Id != conn.TeamId)) return "Ekip bulunamadı";
        if (conn.TargetAppId != null && d.Apps.All(a => a.Id != conn.TargetAppId)) return "Hedef uygulama bulunamadı";
        d.Connections.Add(conn);
        return null;
    });
    return error2 == null ? Results.Ok(ConnectionView.From(conn)) : Results.BadRequest(new { error = error2 });
});

// Havuzdaki bir bağlantıyı günceller. Sahibi değiştiyse (başka ekip / ortak), artık kullanamayacak uygulamalardan çıkarılır.
api.MapPut("/connections/{id}", (string id, ConnectionInput input, DefinitionStore store, MonitorService monitor) =>
{
    if (ValidateConnection(input) is { } error) return Results.BadRequest(new { error });

    var result = store.Mutate(d =>
    {
        var c = d.Connections.FirstOrDefault(x => x.Id == id);
        if (c == null) return (View: (ConnectionView?)null, Error: (string?)"notfound");
        var teamId = NullIfEmpty(input.TeamId);
        if (teamId != null && d.Teams.All(t => t.Id != teamId)) return (null, "Ekip bulunamadı");
        var targetAppId = NullIfEmpty(input.TargetAppId);
        if (targetAppId != null && d.Apps.All(a => a.Id != targetAppId)) return (null, "Hedef uygulama bulunamadı");

        c.Name = input.Name!.Trim();
        c.Host = input.Host!.Trim();
        c.Port = input.Port;
        c.TargetAppId = targetAppId;
        c.TeamId = teamId;
        if (teamId != null)
            foreach (var a in d.Apps.Where(a => a.TeamId != teamId)) a.ConnectionIds.Remove(id);
        return (ConnectionView.From(c), null);
    });

    if (result.Error == "notfound") return Results.NotFound();
    if (result.Error != null) return Results.BadRequest(new { error = result.Error });
    monitor.Trigger();
    return Results.Ok(result.View);
});

// Bağlantıyı havuzdan siler ve ilişkilendirildiği tüm uygulamalardan çıkarır.
api.MapDelete("/connections/{id}", (string id, DefinitionStore store) =>
{
    var removed = store.Mutate(d =>
    {
        foreach (var a in d.Apps) a.ConnectionIds.Remove(id);
        return d.Connections.RemoveAll(c => c.Id == id) > 0;
    });
    return removed ? Results.NoContent() : Results.NotFound();
});

// ---------------------------------------------------------------------------------------------
// Uygulamalar: Monitor'de eklenmez; pod'lar anahtarlarıyla ilk bildirimde kendiliğinden kaydeder.
// ---------------------------------------------------------------------------------------------

// Uygulamanın adını ve ekibini günceller (anahtar uygulamanın kimliğidir, değişmez).
// Ekip değiştiyse eski ekibin havuzundan atanmış bağlantılar çıkarılır (ortak bağlantılar kalır).
api.MapPut("/apps/{id}", (string id, AppInput input, DefinitionStore store, MonitorService monitor) =>
{
    if (ValidateName(input.Name) is { } error) return Results.BadRequest(new { error });

    var result = store.Mutate(d =>
    {
        var a = d.Apps.FirstOrDefault(x => x.Id == id);
        if (a == null) return (View: (AppView?)null, Error: (string?)"notfound");
        var teamId = NullIfEmpty(input.TeamId);
        if (teamId != null && d.Teams.All(t => t.Id != teamId)) return (null, "Ekip bulunamadı");

        a.Name = input.Name!.Trim();
        if (a.TeamId != teamId)
        {
            a.TeamId = teamId;
            a.ConnectionIds.RemoveAll(cid => d.Connections.FirstOrDefault(c => c.Id == cid) is { TeamId: { } owner } && owner != teamId);
        }
        return (AppView.From(a), null);
    });

    if (result.Error == "notfound") return Results.NotFound();
    if (result.Error != null) return Results.BadRequest(new { error = result.Error });
    monitor.Trigger();
    return Results.Ok(result.View);
});

// Uygulamayı siler. Pod'ları hâlâ çalışıyorsa bir sonraki bildirimde kendini yeniden kaydeder (ekipsiz, bağlantısız);
// kalıcı olarak kaldırmak için uygulamadan ConnectivityProbe'u da çıkarın.
api.MapDelete("/apps/{id}", (string id, DefinitionStore store) =>
    store.Mutate(d =>
    {
        foreach (var c in d.Connections.Where(c => c.TargetAppId == id)) c.TargetAppId = null;
        return d.Apps.RemoveAll(a => a.Id == id) > 0;
    }) ? Results.NoContent() : Results.NotFound());

// "Pod listesini sıfırla": Monitor'ün bu uygulama için hatırladığı pod'ları (eksik olanlar dahil) siler; mevcut pod'lar
// hemen yeniden keşfedilir. Eksik pod'lar kendiliğinden hiç silinmediği için temizlemenin tek yolu budur.
api.MapPost("/apps/{id}/reset", (string id, DefinitionStore store, MonitorService monitor) =>
{
    if (store.Snapshot().Apps.All(a => a.Id != id)) return Results.NotFound();
    monitor.ForgetApp(id);
    monitor.Trigger();
    return Results.NoContent();
});

// Sürükle-bırak: havuzdaki bir bağlantıyı uygulamayla ilişkilendirir. Ekip bağlantısı yalnızca o ekibin uygulamalarına,
// ortak bağlantı herkese atanabilir.
api.MapPut("/apps/{appId}/connections/{connId}", (string appId, string connId, DefinitionStore store, MonitorService monitor) =>
{
    var result = store.Mutate(d =>
    {
        var a = d.Apps.FirstOrDefault(x => x.Id == appId);
        var c = d.Connections.FirstOrDefault(x => x.Id == connId);
        if (a == null || c == null) return "notfound";
        if (c.TeamId != null && c.TeamId != a.TeamId)
            return "Bu bağlantı başka bir ekibin havuzunda; yalnızca o ekibin uygulamalarına atanabilir (ya da ortak havuza taşıyın)";
        if (!a.ConnectionIds.Contains(connId)) a.ConnectionIds.Add(connId);
        return null;
    });

    if (result == "notfound") return Results.NotFound();
    if (result != null) return Results.BadRequest(new { error = result });
    monitor.Trigger();
    return Results.NoContent();
});

// Bağlantı ile uygulama arasındaki ilişkiyi kaldırır.
api.MapDelete("/apps/{appId}/connections/{connId}", (string appId, string connId, DefinitionStore store) =>
    store.Mutate(d => d.Apps.FirstOrDefault(a => a.Id == appId)?.ConnectionIds.Remove(connId) ?? false)
        ? Results.NoContent() : Results.NotFound());

// ---------------------------------------------------------------------------------------------
// Monitör
// ---------------------------------------------------------------------------------------------

// Arayüzün periyodik okuduğu güncel durum: her uygulama için pod sayısı, pod listesi ve bağlantı sonuçları.
api.MapGet("/monitor", (MonitorService monitor) => monitor.GetSnapshot());

// "Şimdi test et": pod'lar bunu bir sonraki bildirimlerinde (en geç PollSeconds, varsayılan 10 sn) görüp beklemeden test eder.
api.MapPost("/monitor/run", (MonitorService monitor) =>
{
    monitor.RequestRun();
    return Results.Accepted();
});

app.Run();

// ---------------------------------------------------------------------------------------------
// Yardımcılar
// ---------------------------------------------------------------------------------------------

static string NewId() => Guid.NewGuid().ToString("N");

// Anahtara sahip uygulamayı bulur. Anahtarlar büyük/küçük harfe duyarlıdır ("Orders" ile "orders" farklı uygulamalardır).
static AppDefinition? FindApp(DefinitionData defs, string? key) =>
    string.IsNullOrEmpty(key) ? null : defs.Apps.FirstOrDefault(a => string.Equals(a.AppKey, key, StringComparison.Ordinal));

// Uygulama anahtarı: geliştiricinin belirlediği değer (ör. "orders-api"). Görünür ASCII karakterler, en fazla 200.
static string? ValidateAppKey(string key)
{
    if (key.Length == 0) return "X-ConnectivityProbe-AppKey header is required";
    if (key.Length > 200) return "App key is too long (max 200 characters)";
    if (key.Any(ch => ch < 0x21 || ch > 0x7E)) return "App key may only contain visible ASCII characters (no spaces)";
    return null;
}

// Bildirimin geldiği adres. Ters proxy arkasındaysa X-Forwarded-For'un ilk değeri; Kubernetes dışındaki pod'lar buna göre gruplanır.
static string SourceIp(HttpContext ctx)
{
    var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrWhiteSpace(forwarded)) return forwarded.Split(',')[0].Trim();
    var ip = ctx.Connection.RemoteIpAddress;
    if (ip == null) return "unknown";
    return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
}

static bool FixedEquals(string? a, string? b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? "")) && a != null && b != null;

static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

static string? ValidateName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return "Ad zorunlu";
    if (name.Trim().Length > 100) return "Ad en fazla 100 karakter olabilir";
    return null;
}

// Bağlantı girdisi geçerli değilse hata mesajını, geçerliyse null döner. Host/port kuralı pod'lardaki testle aynıdır
// (ProbeTarget): sql01 + port, sql01:1433, IP, [::1]:5078 veya https://... (port URL'den) kabul edilir.
static string? ValidateConnection(ConnectionInput i)
{
    if (ValidateName(i.Name) is { } nameError) return nameError;
    if (string.IsNullOrWhiteSpace(i.Host)) return "Host zorunlu";
    if (i.Port is < 1 or > 65535) return "Port 1-65535 arasında olmalı";
    if (!ProbeTarget.TryParse(i.Host, i.Port?.ToString(), out _, out _, out _, out var error))
        return error != null && error.StartsWith("port")
            ? "Port zorunlu: host içinde port yoksa (sql01:1433) ve tam URL değilse (https://...) port alanını doldurun"
            : "Host geçersiz: sunucu adı, IP, sunucu:port veya http(s):// ile başlayan bir URL olmalı";
    return null;
}
