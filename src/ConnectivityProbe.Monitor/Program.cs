using ConnectivityProbe;
using ConnectivityProbe.Monitor;

var builder = WebApplication.CreateBuilder(args);

// step 1: Ayarları, tanım deposunu ve arka planda periyodik test yapan servisi ekliyoruz.
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.AddSingleton<DefinitionStore>();
builder.Services.AddSingleton<PodStateStore>();   // görülen / eksik pod'lar diske yazılır (data/pod-state.json)
builder.Services.AddSingleton<MonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitorService>());

var app = builder.Build();

// step 2: Arayüz (wwwroot/index.html) kök adreste yayınlanır.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // "no-cache": tarayıcı dosyayı her seferinde sunucuya sorar (ETag ile değişmediyse 304 döner). Böylece arayüz
    // güncellendiğinde kullanıcılar eski app.js/style.css'i önbellekten görmez.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});

var api = app.MapGroup("/api");

// Tüm tanımları (birimler, ekipler, uygulamalar, bağlantı havuzu) döner.
api.MapGet("/definitions", (DefinitionStore store) =>
{
    var d = store.Snapshot();
    return new DefinitionsView(d.Units, d.Teams,
        d.Apps.Select(AppView.From).ToList(), d.Connections.Select(ConnectionView.From).ToList());
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
// havuza eklenir. Checkpoint: UsesConnectivityProbe = hedef de ConnectivityProbe kullanıyor mu.
api.MapPost("/connections", (ConnectionInput input, DefinitionStore store) =>
{
    if (ValidateConnection(input) is { } error) return Results.BadRequest(new { error });

    var conn = new ConnectionDefinition
    {
        Id = NewId(), TeamId = NullIfEmpty(input.TeamId), Name = input.Name!.Trim(), Host = input.Host!.Trim(), Port = input.Port,
        UsesConnectivityProbe = input.UsesConnectivityProbe == true
    };
    var ok = store.Mutate(d =>
    {
        if (conn.TeamId != null && d.Teams.All(t => t.Id != conn.TeamId)) return false;
        d.Connections.Add(conn);
        return true;
    });
    return ok ? Results.Ok(ConnectionView.From(conn)) : Results.BadRequest(new { error = "Ekip bulunamadı" });
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

        c.Name = input.Name!.Trim();
        c.Host = input.Host!.Trim();
        c.Port = input.Port;
        c.UsesConnectivityProbe = input.UsesConnectivityProbe == true;
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
// Uygulamalar: izlenecek, ConnectivityProbe yüklü uygulamalar
// ---------------------------------------------------------------------------------------------

// Yeni uygulama kaydeder (ad + URL + ekip). Erişim anahtarı ortaktır (Monitor:AccessKey).
api.MapPost("/apps", (AppInput input, DefinitionStore store, MonitorService monitor) =>
{
    if (Validate(input) is { } error) return Results.BadRequest(new { error });

    var app = new AppDefinition
    {
        Id = NewId(), TeamId = NullIfEmpty(input.TeamId), Name = input.Name!.Trim(),
        BaseUrl = input.BaseUrl!.Trim().TrimEnd('/'), ProbePath = NormalizePath(input.ProbePath)
    };
    var ok = store.Mutate(d =>
    {
        if (app.TeamId != null && d.Teams.All(t => t.Id != app.TeamId)) return false;
        d.Apps.Add(app);
        return true;
    });
    if (!ok) return Results.BadRequest(new { error = "Ekip bulunamadı" });
    monitor.Trigger();
    return Results.Ok(AppView.From(app));
});

// Uygulamanın adını/URL'sini/ekibini günceller.
// - URL değiştiyse bu artık başka bir uygulamadır: eski pod geçmişi silinir (yoksa eski adresin pod'ları yanlış "eksik" görünürdü).
// - Ekip değiştiyse eski ekibin havuzundan atanmış bağlantılar çıkarılır (ortak bağlantılar kalır).
api.MapPut("/apps/{id}", (string id, AppInput input, DefinitionStore store, MonitorService monitor) =>
{
    if (Validate(input) is { } error) return Results.BadRequest(new { error });

    var urlChanged = false;
    var result = store.Mutate(d =>
    {
        var a = d.Apps.FirstOrDefault(x => x.Id == id);
        if (a == null) return (View: (AppView?)null, Error: (string?)"notfound");
        var teamId = NullIfEmpty(input.TeamId);
        if (teamId != null && d.Teams.All(t => t.Id != teamId)) return (null, "Ekip bulunamadı");

        var newUrl = input.BaseUrl!.Trim().TrimEnd('/');
        urlChanged = !string.Equals(a.BaseUrl, newUrl, StringComparison.OrdinalIgnoreCase);
        a.Name = input.Name!.Trim();
        a.BaseUrl = newUrl;
        a.ProbePath = NormalizePath(input.ProbePath);
        if (a.TeamId != teamId)
        {
            a.TeamId = teamId;
            a.ConnectionIds.RemoveAll(cid => d.Connections.FirstOrDefault(c => c.Id == cid) is { TeamId: { } owner } && owner != teamId);
        }
        return (AppView.From(a), null);
    });

    if (result.Error == "notfound") return Results.NotFound();
    if (result.Error != null) return Results.BadRequest(new { error = result.Error });
    if (urlChanged) monitor.ForgetApp(id);
    monitor.Trigger();
    return Results.Ok(result.View);
});

// Uygulamayı siler (izleme de durur).
api.MapDelete("/apps/{id}", (string id, DefinitionStore store) =>
    store.Mutate(d => d.Apps.RemoveAll(a => a.Id == id) > 0) ? Results.NoContent() : Results.NotFound());

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

// "Şimdi test et": bir sonraki aralığı beklemeden yeni bir test turu başlatır.
api.MapPost("/monitor/run", (MonitorService monitor) =>
{
    monitor.Trigger();
    return Results.Accepted();
});

app.Run();

// ---------------------------------------------------------------------------------------------
// Yardımcılar
// ---------------------------------------------------------------------------------------------

static string NewId() => Guid.NewGuid().ToString("N");

static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

// Probe yolu "/" ile başlamalı; boşsa varsayılanı kullanılır.
static string NormalizePath(string? path)
{
    path = path?.Trim();
    if (string.IsNullOrEmpty(path)) return "/connectivity-probe";
    return (path.StartsWith('/') ? path : "/" + path).TrimEnd('/');
}

static string? ValidateName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return "Ad zorunlu";
    if (name.Trim().Length > 100) return "Ad en fazla 100 karakter olabilir";
    return null;
}

// Uygulama girdisi geçerli değilse hata mesajını, geçerliyse null döner.
static string? Validate(AppInput i)
{
    if (ValidateName(i.Name) is { } nameError) return nameError;
    if (!Uri.TryCreate(i.BaseUrl?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        return "URL http:// veya https:// ile başlayan geçerli bir adres olmalı";
    // Probe yolu URL'nin sonuna eklendiği için sorgu (?...) veya parça (#...) içeremez.
    if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return "URL '?' veya '#' içeremez";
    if (i.ProbePath != null && i.ProbePath.IndexOfAny(new[] { '?', '#', ' ' }) >= 0) return "Probe yolu '?', '#' veya boşluk içeremez";
    return null;
}

// Bağlantı girdisi geçerli değilse hata mesajını, geçerliyse null döner. Host/port kuralı uygulamalardaki probe uçlarıyla
// aynıdır (ProbeTarget): sql01 + port, sql01:1433, IP, [::1]:5078 veya https://... (port URL'den) kabul edilir.
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
