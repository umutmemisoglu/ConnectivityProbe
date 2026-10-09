using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ConnectivityProbe;
using ConnectivityProbe.Monitor;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// step 1: Ayarları, tanım deposunu ve arka planda periyodik test yapan servisi ekliyoruz.
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.AddSingleton<DefinitionStore>();
builder.Services.AddSingleton<PodStateStore>();
builder.Services.AddSingleton<SettingsStore>();     // Teams bildirimlerinin kurulum ayarları (data/settings.json)   // görülen / eksik pod'lar diske yazılır (data/pod-state.json)
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<INotificationSender, TeamsWebhookSender>();
builder.Services.AddSingleton<NotificationService>();                  // Teams bildirimleri
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationService>());
builder.Services.AddSingleton<MonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitorService>());

// step 1b: Arayüz girişi (çerez). Çerezi şifreleyen anahtarlar veri klasöründe tutulur; Monitor yeniden başlayınca
//          kullanıcıların tekrar giriş yapması gerekmez.
var dataFile = builder.Configuration["Monitor:DataFile"] ?? "data/definitions.json";
var dataDir = Path.GetDirectoryName(Path.IsPathRooted(dataFile) ? dataFile : Path.Combine(builder.Environment.ContentRootPath, dataFile))!;
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "cpmonitor";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;          // başka sitelerden gelen isteklere çerez eklenmez (CSRF koruması)
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
});

// step 1c: Microsoft (Entra ID) ile giriş: "Bana haber ver" diyen kişiyi tanımak için (adı, e-postası); arayüzün admin
//          girişinden ayrıdır. Tenant ve client bilgileri Ayarlar'dan canlı okunur (MicrosoftLoginOptions). Client secret
//          gerekmez: Entra ID, imzalı kimlik belirtecini (id_token) doğrudan tarayıcı üzerinden geri gönderir.
authentication
    .AddCookie(MicrosoftLogin.PersonScheme, o =>
    {
        o.Cookie.Name = "cpperson";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;        // Microsoft'tan dönüşte (site dışından gelen yönlendirme) gönderilsin
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(180);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    })
    .AddOpenIdConnect(MicrosoftLogin.MicrosoftScheme, o =>
    {
        o.ResponseType = "id_token";
        o.ResponseMode = "form_post";
        o.Scope.Clear();
        foreach (var scope in new[] { "openid", "profile", "email" }) o.Scope.Add(scope);
        o.SignInScheme = MicrosoftLogin.PersonScheme;
        o.CallbackPath = "/signin-oidc";
        o.MapInboundClaims = false;                      // oid, name, email, preferred_username olduğu gibi kalsın
        o.TokenValidationParameters.NameClaimType = "name";
        // Doğrulama çerezleri Microsoft'tan dönen form ile gönderilir (site dışı POST): SameSite=None + Secure gerekir.
        // Tarayıcılar http://localhost'u güvenli saydığı için yerel denemede de çalışır.
        o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        o.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
    });
builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, MicrosoftLoginOptions>();

// Monitor bir ingress / reverse proxy arkasında https ile yayınlanıyorsa, dönüş adresi (redirect URI) https olarak kurulabilsin
// diye X-Forwarded-Proto ve X-Forwarded-Host dikkate alınır. Entra ID yalnızca kayıtlı adreslere döner.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();
var monitorOptions = app.Services.GetRequiredService<IOptions<MonitorOptions>>().Value;
var loginRequired = !string.IsNullOrEmpty(monitorOptions.AdminPassword);
if (!loginRequired)
    app.Logger.LogWarning("Monitor:AdminPassword is not set: the UI is open to everyone who can reach the Monitor.");

// step 2: Erişim kapısı.
//   - /api/agent/*: Strict pod'ların uçları; giriş değil uygulama anahtarı (X-ConnectivityProbe-AppKey) ister.
//   - Giriş sayfası ve giriş uçları: herkese açık.
//   - Geri kalan her şey (arayüz, tanımlar, monitör): şifre tanımlıysa giriş yapmış kullanıcı; tanımlı değilse herkese açık
//     (yetkilendirme sonra ele alınacak).
// Ayarlar kaydedilince Microsoft girişi yeni bilgilerle yeniden yapılandırılır (Monitor'ü yeniden başlatmak gerekmez).
var settingsStore = app.Services.GetRequiredService<SettingsStore>();
var oidcCache = app.Services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>();
settingsStore.Changed += () => oidcCache.TryRemove(MicrosoftLogin.MicrosoftScheme);

app.UseForwardedHeaders();
app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    Lang.Set(ctx); // hata mesajlarının dili (arayüzde seçilen)
    var path = ctx.Request.Path;
    // Container / Kubernetes sağlık kontrolü (liveness / readiness): girişsiz, bilgi içermez.
    if (path == "/healthz")
    {
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync("ok");
        return;
    }
    // Microsoft girişinin dönüşü site dışından gelir (admin çerezi gönderilmez); bu iki uç kendi doğrulamasını yapar.
    if (path.StartsWithSegments("/api/agent") || path.StartsWithSegments("/api/auth")
        || path == "/signin-oidc" || path.StartsWithSegments("/auth/microsoft")
        || path == "/login.html" || path == "/login.js" || path == "/i18n.js" || path == "/style.css")
    {
        await next(ctx);
        return;
    }

    if (!loginRequired || ctx.User.Identity?.IsAuthenticated == true)
    {
        await next(ctx);
        return;
    }

    if (path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = Lang.T("Giriş gerekli", "Login required") });
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
        return Results.Json(new { error = Lang.T("Kullanıcı adı veya şifre hatalı", "Invalid user name or password") }, statusCode: StatusCodes.Status401Unauthorized);
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
            var created = new AppDefinition
            {
                Id = NewId(), Name = name.Length > 100 ? name[..100] : name, AppKey = key, RegisteredAtUtc = DateTime.UtcNow,
                NotifyRules = NotifyRules.Defaults() // ilk kayıtta varsayılan bildirim kuralları işaretli gelir
            };
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
        d.Apps.Select(AppView.From).ToList(), d.Connections.Select(ConnectionView.From).ToList(), d.Clusters,
        d.People.Select(PersonView.From).ToList(), NotifyRules.All.Select(RuleView.From).ToList());
});

// Cluster'a elle ad verir (ör. "Prod İstanbul"). Boş ad otomatik ada (pod ağı, ör. 10.42.0.0/16) döndürür.
api.MapPut("/clusters/{key}", (string key, ClusterInput input, DefinitionStore store, MonitorService monitor) =>
{
    if (!string.IsNullOrWhiteSpace(input.Name) && ValidateName(input.Name) is { } error) return Results.BadRequest(new { error });
    var cluster = store.Mutate(d =>
    {
        var c = d.Clusters.FirstOrDefault(x => x.Key == key);
        if (c != null) c.Name = input.Name?.Trim() ?? "";
        return c;
    });
    monitor.Trigger();
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
        if (d.Teams.Any(t => t.UnitId == id)) return Lang.T("Birimde ekip var; önce ekipleri silin veya başka birime taşıyın",
            "The unit has teams; delete them or move them to another unit first");
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
    return team == null ? Results.BadRequest(new { error = Lang.T("Birim bulunamadı", "Unit not found") }) : Results.Ok(team);
});

// Ekibin adını veya birimini değiştirir.
api.MapPut("/teams/{id}", (string id, TeamInput input, DefinitionStore store) =>
{
    if (ValidateName(input.Name) is { } error) return Results.BadRequest(new { error });
    var result = store.Mutate(d =>
    {
        var t = d.Teams.FirstOrDefault(x => x.Id == id);
        if (t == null) return (Team: (TeamDefinition?)null, Error: (string?)"notfound");
        if (d.Units.All(u => u.Id != input.UnitId)) return (null, Lang.T("Birim bulunamadı", "Unit not found"));
        t.Name = input.Name!.Trim();
        t.UnitId = input.UnitId!;
        return (t, null);
    });
    return result.Error == "notfound" ? Results.NotFound()
        : result.Error != null ? Results.BadRequest(new { error = result.Error })
        : Results.Ok(result.Team);
});

// Ekip, içinde uygulama varken silinemez.
api.MapDelete("/teams/{id}", (string id, DefinitionStore store) =>
{
    var result = store.Mutate(d =>
    {
        if (d.Apps.Any(a => a.TeamId == id))
            return Lang.T("Ekipte uygulama var; önce uygulamaları silin veya başka ekibe taşıyın",
                "The team has applications; delete them or move them to another team first");
        return d.Teams.RemoveAll(t => t.Id == id) > 0 ? null : "notfound";
    });
    return result == null ? Results.NoContent() : result == "notfound" ? Results.NotFound() : Results.BadRequest(new { error = result });
});

// ---------------------------------------------------------------------------------------------
// Bağlantı havuzu: tek havuz, birim ve ekiplerden bağımsız; her bağlantı her uygulamaya atanabilir
// ---------------------------------------------------------------------------------------------

// Havuza yeni bağlantı ekler. Host ad, IP, ad:port veya tam URL olabilir. TargetAppId: hedef de Monitor'e kayıtlı bir uygulamaysa onun kimliği (isteğe bağlı).
api.MapPost("/connections", (ConnectionInput input, DefinitionStore store) =>
{
    if (ValidateConnection(input) is { } error) return Results.BadRequest(new { error });

    var conn = new ConnectionDefinition
    {
        Id = NewId(), Name = input.Name!.Trim(), Host = input.Host!.Trim(), Port = input.Port,
        TargetAppId = NullIfEmpty(input.TargetAppId), TlsCheck = TlsMode(input.TlsCheck)
    };
    var error2 = store.Mutate(d =>
    {
        if (ConnectionRules.DuplicateOf(d, conn.Host, conn.Port, exceptId: null) is { } dup) return DuplicateError(dup);
        if (conn.TargetAppId != null && d.Apps.All(a => a.Id != conn.TargetAppId)) return Lang.T("Hedef uygulama bulunamadı", "Target application not found");
        d.Connections.Add(conn);
        return null;
    });
    return error2 == null ? Results.Ok(ConnectionView.From(conn)) : Results.BadRequest(new { error = error2 });
});

// Havuzdaki bir bağlantıyı günceller.
api.MapPut("/connections/{id}", (string id, ConnectionInput input, DefinitionStore store, MonitorService monitor) =>
{
    if (ValidateConnection(input) is { } error) return Results.BadRequest(new { error });

    var result = store.Mutate(d =>
    {
        var c = d.Connections.FirstOrDefault(x => x.Id == id);
        if (c == null) return (View: (ConnectionView?)null, Error: (string?)"notfound");
        // Adres değiştiyse başka bir bağlantıyla çakışmamalı (adresi değişmeyen eski çiftler yine düzenlenebilir).
        if (ConnectionRules.EndpointKey(input.Host, input.Port) != ConnectionRules.EndpointKey(c.Host, c.Port)
            && ConnectionRules.DuplicateOf(d, input.Host, input.Port, exceptId: id) is { } dup)
            return (null, DuplicateError(dup));
        var targetAppId = NullIfEmpty(input.TargetAppId);
        if (targetAppId != null && d.Apps.All(a => a.Id != targetAppId)) return (null, Lang.T("Hedef uygulama bulunamadı", "Target application not found"));

        c.Name = input.Name!.Trim();
        c.Host = input.Host!.Trim();
        c.Port = input.Port;
        c.TargetAppId = targetAppId;
        c.TlsCheck = TlsMode(input.TlsCheck);
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

// Uygulamanın adını ve ekibini günceller (anahtar uygulamanın kimliğidir, değişmez). Bağlantıları ekipten bağımsızdır, olduğu gibi kalır.
api.MapPut("/apps/{id}", (string id, AppInput input, DefinitionStore store, MonitorService monitor) =>
{
    if (ValidateName(input.Name) is { } error) return Results.BadRequest(new { error });

    var result = store.Mutate(d =>
    {
        var a = d.Apps.FirstOrDefault(x => x.Id == id);
        if (a == null) return (View: (AppView?)null, Error: (string?)"notfound");
        var teamId = NullIfEmpty(input.TeamId);
        if (teamId != null && d.Teams.All(t => t.Id != teamId)) return (null, Lang.T("Ekip bulunamadı", "Team not found"));

        a.Name = input.Name!.Trim();
        a.TeamId = teamId;
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

// Sürükle-bırak: havuzdaki bir bağlantıyı uygulamayla ilişkilendirir (her bağlantı her uygulamaya atanabilir).
api.MapPut("/apps/{appId}/connections/{connId}", (string appId, string connId, DefinitionStore store, MonitorService monitor) =>
{
    var result = store.Mutate(d =>
    {
        var a = d.Apps.FirstOrDefault(x => x.Id == appId);
        var c = d.Connections.FirstOrDefault(x => x.Id == connId);
        if (a == null || c == null) return "notfound";
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
// Bildirimler: uygulama hangi durumlarda bildirim üretir (kişiden bağımsız) ve kimler alır.
// ---------------------------------------------------------------------------------------------

// Uygulamanın bildirim kurallarını kaydeder (Tanımlar'daki checkbox'lar).
api.MapPut("/apps/{id}/notify", (string id, NotifyInput input, DefinitionStore store) =>
{
    var rules = NotifyRules.Normalize(input.Rules);
    var app = store.Mutate(d =>
    {
        var a = d.Apps.FirstOrDefault(x => x.Id == id);
        if (a != null) a.NotifyRules = rules;
        return a;
    });
    return app == null ? Results.NotFound() : Results.Ok(AppView.From(app));
});

// Kurulum tamam mı ve oturumdaki kişi (Microsoft hesabıyla giriş yaptıysa). Arayüz "Bana haber ver"in ne yapacağını buradan
// öğrenir: kurulum yoksa önce Ayarlar'a yönlendirir; giriş yoksa Microsoft giriş sayfasına gider.
api.MapGet("/notify/me", async (HttpContext ctx, DefinitionStore store, SettingsStore settings) =>
{
    PersonView? me = null;
    if (MicrosoftLogin.PersonOf((await ctx.AuthenticateAsync(MicrosoftLogin.PersonScheme)).Principal) is { } who
        && store.Snapshot().People.FirstOrDefault(p => p.Id == who.Id) is { } person)
        me = PersonView.From(person);
    return Results.Ok(new { ready = settings.Current.Ready, person = me });
});

// Microsoft ile giriş ve (girişten dönünce) otomatik abonelik:
//   /auth/microsoft?app=<uygulama>&returnUrl=/?app=<uygulama>
// Oturum yoksa Microsoft giriş sayfasına gider ve aynı adrese döner; oturum varsa kişiyi kaydeder/günceller, uygulamaya
// abone eder ve kişiyi geldiği sayfaya geri gönderir.
app.MapGet("/auth/microsoft", async (HttpContext ctx, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "app")] string? appId, string? returnUrl,
    DefinitionStore store, SettingsStore settings, NotificationService notifications) =>
{
    if (!settings.Current.Ready) return MicrosoftLogin.PageRedirect("/?settings=notifications");
    var auth = await ctx.AuthenticateAsync(MicrosoftLogin.PersonScheme);
    if (MicrosoftLogin.PersonOf(auth.Principal) is not { } who)
    {
        // Microsoft giriş sayfasına yönlendirme. Tenant kimliği yanlışsa ya da Monitor Microsoft'a erişemiyorsa (proxy,
        // firewall) yönlendirme adresi alınamaz; kişiye anlaşılır bir sayfa gösterilir.
        try
        {
            await ctx.ChallengeAsync(MicrosoftLogin.MicrosoftScheme,
                new AuthenticationProperties { RedirectUri = ctx.Request.Path + ctx.Request.QueryString });
            return Results.Empty;
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Microsoft sign-in could not start");
            return MicrosoftLogin.ErrorPage(Lang.T(
                "Microsoft giriş sayfası açılamadı. Ayarlar → Teams bildirimleri bölümündeki Directory (tenant) ID yanlış olabilir ya da Monitor login.microsoftonline.com adresine erişemiyor olabilir.",
                "The Microsoft sign-in page could not be opened. The Directory (tenant) ID under Settings → Teams notifications may be wrong, or the Monitor cannot reach login.microsoftonline.com."));
        }
    }

    var lang = ctx.Request.Cookies[Lang.Cookie] == "en" ? "en" : "tr";
    var (person, created) = store.Mutate(d =>
    {
        var p = d.People.FirstOrDefault(x => x.Id == who.Id);
        var isNew = p == null;
        if (p == null) d.People.Add(p = new PersonDefinition { Id = who.Id, CreatedAtUtc = DateTime.UtcNow, Source = "microsoft" });
        p.Name = who.Name.Length > 100 ? who.Name[..100] : who.Name;
        p.Email = who.Email;
        p.Lang = lang;
        p.MonitorUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        if (!string.IsNullOrEmpty(appId) && d.Apps.FirstOrDefault(a => a.Id == appId) is { } target && !target.SubscriberIds.Contains(p.Id))
            target.SubscriberIds.Add(p.Id);
        return (p, isNew);
    });
    // İlk girişte Teams'e hoş geldin mesajı (gelmezse abonelik yine yapılır; Monitor günlüğüne yazılır).
    if (created && await notifications.SendWelcomeAsync(person, ctx.RequestAborted) is { } error)
        app.Logger.LogWarning("Welcome message to {Person} failed: {Error}", person.Name, error);
    return MicrosoftLogin.PageRedirect(returnUrl ?? "/");
});

// Bu tarayıcıdaki Microsoft oturumunu kapatır (bildirimler sürer; tekrar "Bana haber ver" denince yeniden girilir).
app.MapGet("/auth/microsoft/signout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(MicrosoftLogin.PersonScheme);
    return MicrosoftLogin.PageRedirect("/");
});

// ---------------------------------------------------------------------------------------------
// Ayarlar → Teams bildirimleri (tek seferlik kurulum). İş akışı adresi gizlidir; hiçbir zaman geri gönderilmez.
// ---------------------------------------------------------------------------------------------

api.MapGet("/settings/notifications", (HttpContext ctx, SettingsStore settings) =>
{
    var s = settings.Current;
    return Results.Ok(new
    {
        tenantId = s.TenantId ?? "", clientId = s.ClientId ?? "",
        workflowConfigured = s.WorkflowConfigured, signInConfigured = s.SignInConfigured, ready = s.Ready,
        // Entra ID kaydına girilecek dönüş adresi (bu Monitor'ün adresiyle).
        redirectUri = $"{ctx.Request.Scheme}://{ctx.Request.Host}/signin-oidc"
    });
});

// Kaydeder. Alan gönderilmezse (null) değişmez; iş akışı adresi yalnızca yeni değer yazılınca değişir.
api.MapPut("/settings/notifications", (NotifySettingsInput input, SettingsStore settings) =>
{
    if (!string.IsNullOrWhiteSpace(input.TenantId) && !Guid.TryParse(input.TenantId.Trim(), out _))
        return Results.BadRequest(new { error = Lang.T("Directory (tenant) ID bir GUID olmalı (ör. 1234abcd-…).", "Directory (tenant) ID must be a GUID (e.g. 1234abcd-…).") });
    if (!string.IsNullOrWhiteSpace(input.ClientId) && !Guid.TryParse(input.ClientId.Trim(), out _))
        return Results.BadRequest(new { error = Lang.T("Application (client) ID bir GUID olmalı.", "Application (client) ID must be a GUID.") });
    if (!string.IsNullOrWhiteSpace(input.WorkflowUrl) && ValidateWebhook(input.WorkflowUrl) is { } urlError)
        return Results.BadRequest(new { error = urlError });
    settings.Save(input.TenantId, input.ClientId, string.IsNullOrWhiteSpace(input.WorkflowUrl) ? null : input.WorkflowUrl);
    return Results.NoContent();
});

// Merkezi iş akışından verilen e-postaya deneme mesajı gönderir (kurulumu doğrulamak için).
api.MapPost("/settings/notifications/test", async (NotifyTestInput input, HttpContext ctx, SettingsStore settings, NotificationService notifications) =>
{
    if (!settings.Current.WorkflowConfigured)
        return Results.BadRequest(new { error = Lang.T("Önce merkezi iş akışı adresini kaydedin.", "Save the central workflow address first.") });
    if (ValidateEmail(input.Email) is { } emailError) return Results.BadRequest(new { error = emailError });
    var probe = new PersonDefinition { Name = input.Email!.Trim(), Email = input.Email.Trim(), Lang = ctx.Request.Cookies[Lang.Cookie] == "en" ? "en" : "tr" };
    return await notifications.SendWelcomeAsync(probe, ctx.RequestAborted) is { } error
        ? Results.BadRequest(new { error = Lang.T($"Deneme mesajı gönderilemedi: {error}", $"The test message could not be sent: {error}") })
        : Results.NoContent();
});

// Tarayıcının hatırladığı kişi hâlâ kayıtlı mı.
api.MapGet("/people/{id}", (string id, DefinitionStore store) =>
    store.Snapshot().People.FirstOrDefault(p => p.Id == id) is { } p ? Results.Ok(PersonView.From(p)) : Results.NotFound());

// Kişiyi siler ve tüm uygulamaların bildirim listesinden çıkarır.
api.MapDelete("/people/{id}", (string id, DefinitionStore store) =>
    store.Mutate(d =>
    {
        foreach (var a in d.Apps) a.SubscriberIds.Remove(id);
        return d.People.RemoveAll(p => p.Id == id) > 0;
    }) ? Results.NoContent() : Results.NotFound());

// "Bana haber ver" / "Bildirimi kapat": kişiyi uygulamanın bildirim listesine ekler veya çıkarır.
api.MapPut("/apps/{appId}/subscribers/{personId}", (string appId, string personId, DefinitionStore store) =>
    store.Mutate(d =>
    {
        var a = d.Apps.FirstOrDefault(x => x.Id == appId);
        if (a == null || d.People.All(p => p.Id != personId)) return false;
        if (!a.SubscriberIds.Contains(personId)) a.SubscriberIds.Add(personId);
        return true;
    }) ? Results.NoContent() : Results.NotFound());

api.MapDelete("/apps/{appId}/subscribers/{personId}", (string appId, string personId, DefinitionStore store) =>
    store.Mutate(d => d.Apps.FirstOrDefault(a => a.Id == appId)?.SubscriberIds.Remove(personId) ?? false)
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

static string DuplicateError(ConnectionDefinition existing) => Lang.T(
    $"Bu adres havuzda zaten var: \"{existing.Name}\" ({ConnectionRules.EndpointKey(existing.Host, existing.Port)}). Aynı adres ikinci kez eklenemez; mevcut bağlantıyı kullanın.",
    $"This address is already in the pool: \"{existing.Name}\" ({ConnectionRules.EndpointKey(existing.Host, existing.Port)}). The same address cannot be added twice; use the existing connection.");

// Teams e-postası (Microsoft hesabının adresi).
static string? ValidateEmail(string? email) =>
    System.Net.Mail.MailAddress.TryCreate(email?.Trim(), out var m) && m.Address == email!.Trim() && email.Length <= 254
        ? null
        : Lang.T("Geçerli bir e-posta adresi girin (Teams'te kullandığınız şirket adresi).", "Enter a valid e-mail address (the company address you use in Teams).");

// Teams Workflows adresi: https ile başlayan tam bir adres olmalı.
static string? ValidateWebhook(string? url) =>
    Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && url!.Length <= 2000
        ? null
        : Lang.T("Teams iş akışı adresi https:// ile başlayan tam bir adres olmalı.", "The Teams workflow address must be a full address starting with https://.");

// TLS kontrolü modu: "on" / "off"; diğer her değer otomatik (null).
static string? TlsMode(string? mode) => mode?.Trim().ToLowerInvariant() is "on" or "off" ? mode.Trim().ToLowerInvariant() : null;

static string? ValidateName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return Lang.T("Ad zorunlu", "Name is required");
    if (name.Trim().Length > 100) return Lang.T("Ad en fazla 100 karakter olabilir", "Name can be at most 100 characters");
    return null;
}

// Bağlantı girdisi geçerli değilse hata mesajını, geçerliyse null döner. Host/port kuralı pod'lardaki testle aynıdır
// (ProbeTarget): sql01 + port, sql01:1433, IP, [::1]:5078 veya https://... (port URL'den) kabul edilir.
static string? ValidateConnection(ConnectionInput i)
{
    if (ValidateName(i.Name) is { } nameError) return nameError;
    if (string.IsNullOrWhiteSpace(i.Host)) return Lang.T("Host zorunlu", "Host is required");
    if (i.Port is < 1 or > 65535) return Lang.T("Port 1-65535 arasında olmalı", "Port must be between 1 and 65535");
    if (!ProbeTarget.TryParse(i.Host, i.Port?.ToString(), out _, out _, out _, out var error))
        return error != null && error.StartsWith("port")
            ? Lang.T("Port zorunlu: host içinde port yoksa (sql01:1433) ve tam URL değilse (https://...) port alanını doldurun",
                "Port is required: fill in the port unless the host contains one (sql01:1433) or is a full URL (https://...)")
            : Lang.T("Host geçersiz: sunucu adı, IP, sunucu:port veya http(s):// ile başlayan bir URL olmalı",
                "Invalid host: use a server name, IP, server:port or a URL starting with http(s)://");
    return null;
}
