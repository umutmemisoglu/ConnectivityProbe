# ConnectivityProbe

Test, **from inside every pod / server of your application**, whether it can reach its databases, queues and APIs, and
find out **how many instances really run** behind a service.

📖 Full documentation: **[English](https://github.com/umutmemisoglu/ConnectivityProbe/blob/main/README.md)** ·
**[Türkçe](https://github.com/umutmemisoglu/ConnectivityProbe/blob/main/README.tr.md)**

## Two modes

| | Discover | Strict (1.1.0+) |
|---|---|---|
| How | ConnectivityProbe Monitor calls your app's URL; the load balancer picks a pod | Every pod pulls its checks from the Monitor with an **app key**, runs them locally and reports back |
| Pod count | Estimated (with a confidence) | **Exact** |
| Needs | Endpoints enabled (`AccessKey` or `AllowAnonymous`) | `MonitorUrl` + `AppKey` |
| Apps without HTTP (workers) | No | Yes |

## Quick start

```bash
dotnet add package ConnectivityProbe
```

**ASP.NET Core 6 – 10**

```csharp
using ConnectivityProbe;

var app = builder.Build();
app.UseConnectivityProbe();          // early in the pipeline, before UseHttpsRedirection
```

**ASP.NET Core 2.1 – 5**: `app.UseConnectivityProbe();` as the first line of `Startup.Configure`.
**Any ASP.NET Core 2.1 – 10 without code**: set the environment variable `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=ConnectivityProbe`.
**Classic ASP.NET on IIS (.NET Framework 4.6.2+)**: nothing to code; the module registers itself. Settings go to `web.config` `<appSettings>`.
**OWIN**: `app.Use(typeof(ConnectivityProbeOwinMiddleware));`
**Worker / console / Windows Service**: `ConnectivityProbeAgent.Start()` (Strict) or `ConnectivityProbeListener.Start()` (endpoints).

**Settings** (environment variables shown; `appsettings.json` → `"ConnectivityProbe"` and `web.config` → `ConnectivityProbe:<Name>` work too):

```bash
# Strict mode
ConnectivityProbe__MonitorUrl=https://monitor.example.com
ConnectivityProbe__AppKey=cpk_...            # from the Monitor (keep it in a secret)

# Discover endpoints: shared key, or AllowAnonymous=true on internal services only
ConnectivityProbe__AccessKey=...
```

Endpoints are **disabled by default**: without `AccessKey` or `AllowAnonymous=true` they return 403.

## Endpoints

- `GET /connectivity-probe/discover?host=sql01:1433`: TCP test from inside this instance.
  Add `&usesConnectivityProbe=true` for a target that also uses ConnectivityProbe to discover its pods.
- `GET /connectivity-probe/identity`: identity of this instance (stable `instanceId` per pod, IPs, pod name, version).

---

## Türkçe özet

Uygulamanızın **her pod'unun / sunucusunun içinden** veritabanlarına, kuyruklara ve API'lere erişebildiğini test eder ve bir
servisin arkasında **gerçekte kaç instance çalıştığını** bulur.

- **Discover:** ConnectivityProbe Monitor uygulamanın adresine gider; pod sayısı tahminidir.
- **Strict (1.1.0+):** Her pod, Monitor'deki **uygulama anahtarıyla** tanımlarını çeker, kendi içinden test eder ve
  sonucu gönderir; pod sayısı kesindir. HTTP'si olmayan worker'larda da çalışır.

Kurulum: `dotnet add package ConnectivityProbe`; ASP.NET Core'da `app.UseConnectivityProbe();`, IIS'te kod gerekmez.
Strict mod için `ConnectivityProbe__MonitorUrl` ve `ConnectivityProbe__AppKey` ayarlarını verin. Platform bazında ayrıntılı
anlatım: **[Türkçe dokümantasyon](https://github.com/umutmemisoglu/ConnectivityProbe/blob/main/README.tr.md)**.

License / Lisans: MIT © 2026 Fatih Umut Memişoğlu
