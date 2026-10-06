# ConnectivityProbe

[English](README.md) | **Türkçe**

[![NuGet](https://img.shields.io/nuget/v/ConnectivityProbe.svg)](https://www.nuget.org/packages/ConnectivityProbe)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

ConnectivityProbe, dışarıdan cevaplanması zor soruları cevaplar:

1. **Uygulamam X'e _kendi pod'larının / sunucularının her birinin içinden_ erişebiliyor mu?**
   Örneğin `orders-api`'nin her pod'u `sql01:1433`'e, Redis'e ve ödeme API'sine TCP bağlantısı açabiliyor mu?
2. **Gerçekte kaç pod çalışıyor, hangi cluster'da ve her biri hangi sürümü / build'i çalıştırıyor?**

Uygulamanıza **tek satır kodla** eklediğiniz küçük bir .NET kütüphanesidir. Her pod, bu depodaki merkezi web uygulaması
**ConnectivityProbe Monitor**'e kendini bildirir; Monitor yüzlerce uygulamayı tek ekranda gösterir.

| Proje | Nedir |
|---|---|
| [`src/ConnectivityProbe`](src/ConnectivityProbe) | Kütüphane; NuGet'te [`ConnectivityProbe`](https://www.nuget.org/packages/ConnectivityProbe) olarak yayınlanır. Hedefler: `netstandard2.0` ve `net462`, **bağımlılığı yoktur**. |
| [`src/ConnectivityProbe.Monitor`](src/ConnectivityProbe.Monitor) | Merkezi izleme uygulaması (ASP.NET Core web uygulaması). Pod'ları, sürümleri, cluster'ları ve sonuçları gösterir; bağlantı tanımlarını tutar. NuGet paketi **değildir**. |
| [`samples/ConnectivityProbe.SampleApi`](samples/ConnectivityProbe.SampleApi) | Örnek ASP.NET Core uygulaması. |
| [`tests/ConnectivityProbe.Tests`](tests/ConnectivityProbe.Tests) | Birim ve uçtan uca testler: `dotnet test tests/ConnectivityProbe.Tests` |

---

## İçindekiler

- [Nasıl çalışır](#nasıl-çalışır)
- [Desteklenen platformlar](#desteklenen-platformlar)
- [Platforma göre kurulum](#platforma-göre-kurulum)
- [Uygulama anahtarı](#uygulama-anahtarı)
- [Sürümler ve build'ler](#sürümler-ve-buildler)
- [Cluster'lar](#clusterlar)
- [Konsol mesajları](#konsol-mesajları)
- [Seçenekler](#seçenekler)
- [Güvenlik](#güvenlik)
- [ConnectivityProbe Monitor](#connectivityprobe-monitor)
- [1.x'ten geçiş](#1xten-geçiş)
- [Sürüm geçmişi](#sürüm-geçmişi)

---

## Nasıl çalışır

```
uygulama açılır ──►  ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)

her 10 sn     ──►  POST {MonitorUrl}/api/agent/v2/report     (başlık X-ConnectivityProbe-AppKey)
                   "<anahtar> uygulamasının X pod'uyum, sürüm 1.4.0, cluster C"
                   ◄── bağlantı listesi + test aralığı
her 30 sn     ──►  her bağlantıyı bu pod'un içinden test et (TCP)
                   └─► sonuçlar hemen bir sonraki bildirimle gider
kapanırken    ──►  POST {MonitorUrl}/api/agent/v2/goodbye    → pod alarm üretmeden listeden çıkar
```

- **Kayıt adımı yoktur.** Yeni bir anahtarla bildirim gönderen ilk pod, uygulamayı Monitor'e kendiliğinden kaydeder. Aynı
  anahtarı kullanan pod'lar aynı uygulamadır; uygulama ikinci kez kaydedilmez.
- **Uygulamanızda uç yoktur.** Kütüphane port açmaz, HTTP pipeline'ınıza bir şey eklemez. Pod'un yalnızca Monitor'e dışarı
  doğru HTTP(S) erişimi olması yeterlidir. HTTP'si olmayan uygulamalar (worker'lar, kuyruk tüketicileri, Windows
  servisleri) de aynı şekilde çalışır.
- **Pod sayısı kesindir.** Her pod kendini bildirdiği için Monitor kaç pod çalıştığını ve her birinin ne gördüğünü bilir.
- **Uygulamanız hiçbir zaman etkilenmez.** Monitor'e ulaşılamazsa veya ayarlar yanlışsa kütüphane konsola kısa bir
  İngilizce mesaj yazar ve arka planda denemeye devam eder. Hiçbir zaman hata fırlatmaz.
- **Bağlantılar Monitor'de tanımlanır**, uygulamada değil: Monitor'de `sql01:1433`'ü `orders-api`'ye bağlayın;
  `orders-api`'nin her pod'u bir sonraki bildirimde onu test etmeye başlar.
- Monitor'deki "**Şimdi test et**" her pod'a bir sonraki bildirimde (10 sn içinde) ulaşır ve pod'lar hemen test eder.
- Bir pod'da aynı anda en fazla 4 bağlantı test edilir; hedeflere ani bağlantı yükü binmez.

---

## Desteklenen platformlar

| Uygulama türü | Paketten kullanılan hedef |
|---|---|
| ASP.NET Core / .NET **Core 2.0 – .NET 10** (web API, MVC, Razor, gRPC, Worker Service, konsol) | `netstandard2.0` |
| **.NET Framework 4.6.2+** (IIS'te klasik ASP.NET: MVC 5, Web API 2, WebForms, WCF; Windows servisleri; konsol) | `net462` |
| Mono, Xamarin, Unity ve .NET Standard 2.0'ı destekleyen diğer her şey | `netstandard2.0` |

Kütüphanenin **hiçbir NuGet bağımlılığı yoktur** (`System.Text.Json` bile); sürüm çakışması yaratmaz, binding redirect
gerektirmez. .NET Framework 4.6.1 ve öncesi desteklenmez.

---

## Platforma göre kurulum

```bash
dotnet add package ConnectivityProbe
```

Her yerde aynı tek çağrı, uygulama açılırken bir kez yapılır:

```csharp
ConnectivityProbe.ConnectivityProbeAgent.Start(
    monitorUrl: "https://monitor.example.com",   // bu ortamın (test / prod) Monitor'ü
    appKey:     "orders-api",                    // siz belirlersiniz; bkz. "Uygulama anahtarı"
    appName:    "Orders API");                   // isteğe bağlı: Monitor'de görünen ad
```

Değerlerin nereden geleceği size kalmış (sabit, `appsettings.json`, ortam değişkeni, `web.config`). Aşağıdaki örnekler
değerleri yapılandırmadan okur; böylece her ortam kendi Monitor'üne bağlanır.

### ASP.NET Core 6 – 10 (`Program.cs`)

```csharp
using ConnectivityProbe;

var builder = WebApplication.CreateBuilder(args);

ConnectivityProbeAgent.Start(
    builder.Configuration["ConnectivityProbe:MonitorUrl"] ?? "",
    builder.Configuration["ConnectivityProbe:AppKey"] ?? "",
    "Orders API");

// ... servisleriniz
var app = builder.Build();
// ... pipeline'ınız (buraya bir şey eklenmez)
app.Run();
```

```json
"ConnectivityProbe": {
  "MonitorUrl": "https://monitor.example.com",
  "AppKey": "orders-api"
}
```

Ya da ortam değişkenleriyle: `ConnectivityProbe__MonitorUrl`, `ConnectivityProbe__AppKey`.

### ASP.NET Core 2.x – 5 (`Startup.cs` / `Program.cs`)

```csharp
public Startup(IConfiguration configuration)
{
    Configuration = configuration;
    ConnectivityProbeAgent.Start(configuration["ConnectivityProbe:MonitorUrl"] ?? "",
                                 configuration["ConnectivityProbe:AppKey"] ?? "",
                                 "Orders API");
}
```

### Worker Service (.NET Generic Host)

```csharp
var builder = Host.CreateApplicationBuilder(args);
ConnectivityProbeAgent.Start(builder.Configuration["ConnectivityProbe:MonitorUrl"] ?? "",
                             builder.Configuration["ConnectivityProbe:AppKey"] ?? "",
                             "Orders Worker");
builder.Services.AddHostedService<Worker>();
builder.Build().Run();
```

### Konsol uygulaması

```csharp
ConnectivityProbeAgent.Start(Environment.GetEnvironmentVariable("ConnectivityProbe__MonitorUrl") ?? "",
                             Environment.GetEnvironmentVariable("ConnectivityProbe__AppKey") ?? "",
                             "Orders Importer");
```

### IIS'te klasik ASP.NET (.NET Framework 4.6.2+, `Global.asax.cs`)

```csharp
using System.Configuration;
using ConnectivityProbe;

protected void Application_Start()
{
    ConnectivityProbeAgent.Start(ConfigurationManager.AppSettings["ConnectivityProbe:MonitorUrl"] ?? "",
                                 ConfigurationManager.AppSettings["ConnectivityProbe:AppKey"] ?? "",
                                 "Orders Web");
    // ... AreaRegistration, RouteConfig, ...
}
```

```xml
<appSettings>
  <add key="ConnectivityProbe:MonitorUrl" value="https://monitor.example.com" />
  <add key="ConnectivityProbe:AppKey" value="orders-web" />
</appSettings>
```

- IIS boşta kalan uygulama havuzunu varsayılan olarak 20 dakika sonra durdurur; bildirimler de onunla durur ve Monitor
  pod'u eksik gösterir. Havuzu `Start Mode = AlwaysRunning`, `Idle Time-out = 0`, siteyi `Preload Enabled = true` yapın.
- .NET Framework'te kütüphane HTTPS Monitor adresleri için TLS 1.2'yi kendisi açar.

### Windows Servisi (.NET Framework)

`Start`'ı `OnStart` içinde çağırın. `OnStop` içinde `ConnectivityProbeAgent.Current?.Stop();` çağrılabilir; zorunlu
değildir, çünkü kütüphane süreç kapanırken de veda eder.

### Durdurma

Agent süreç kapanırken (`ProcessExit`) veya IIS uygulama domain'i kaldırılırken (`DomainUnload`) kendiliğinden durur ve
Monitor'e pod'un ayrıldığını bildirir. `ConnectivityProbeAgent.Current?.Stop()` yalnızca daha erken durdurmak isterseniz
gerekir.

### Kubernetes: önerilen pod bilgileri

Kütüphane başka bir şey olmadan çalışır. Downward API ile Monitor pod ve node adlarını da gösterir:

```yaml
env:
  - name: ConnectivityProbe__MonitorUrl
    value: https://monitor.example.com
  - name: ConnectivityProbe__AppKey
    value: orders-api
  - name: POD_NAME
    valueFrom: { fieldRef: { fieldPath: metadata.name } }
  - name: POD_NAMESPACE
    valueFrom: { fieldRef: { fieldPath: metadata.namespace } }
  - name: NODE_NAME
    valueFrom: { fieldRef: { fieldPath: spec.nodeName } }
```

---

## Uygulama anahtarı

- Kütüphaneyi eklerken **siz belirlersiniz**, örneğin `orders-api`. İzin verilen: 1–200 görünür ASCII karakter.
- Yeni bir anahtarla bildirim gönderen ilk pod uygulamayı kaydeder. Aynı anahtarı kullanan tüm pod'lar aynı uygulamadır.
- **Aynı anahtar her ortamda kullanılabilir.** Test ve prod'un kendi Monitor'ü (`MonitorUrl`) vardır; test pod'ları test
  Monitor'ünde test bağlantılarıyla, prod pod'ları prod Monitor'ünde görünür.
- Monitor'de uygulamanın adını değiştirebilir ve onu bir ekibe taşıyabilirsiniz; anahtar değiştirilemez. Pod'ları hâlâ
  çalışan bir uygulamayı silerseniz, bir sonraki bildirimde yeniden kaydolur.

---

## Sürümler ve build'ler

Her pod, ConnectivityProbe'un değil **sizin uygulamanızın** sürümünü bildirir. Hiçbir ayar gerekmez:

- **Sürüm:** `Start`'ı çağıran assembly'nin `AssemblyInformationalVersion` ve `AssemblyVersion` değerlerinden büyük olanı
  (`+commit` eki atılır). Her zamanki gibi verin: `.csproj` içinde `<Version>1.4.0</Version>` veya CI'da
  `dotnet publish -p:Version=1.4.0`.
- **Build:** assembly'nin MVID'sinin ilk 8 karakteri; derleyicinin her derlemede ürettiği benzersiz kimlik. Sürümü aynı
  ama build'i farklı iki pod ayrı ayrı derlenmiştir.
- **Build tarihi:** assembly dosyasının tarihi.

Monitor sürümü her kartta ve **Sürümler** sekmesinde gösterir; diğerlerinden farklı sürüm veya build çalıştıran pod'ları
işaretler (ör. rollout sırasında ya da deploy yalnızca bir cluster'a ulaştığında).

---

## Cluster'lar

Monitor pod'ları cluster'a göre **kendiliğinden** gruplar:

- **Kubernetes:** her pod'da cluster'ının CA sertifikası `/var/run/secrets/kubernetes.io/serviceaccount/ca.crt` yolunda
  bulunur. Kütüphane bunun parmak izini gönderir (SHA-256; sertifikanın kendisi asla gönderilmez); aynı cluster'daki tüm
  pod'lar aynı cluster kimliğini alır. Namespace de aynı klasörden okunur.
- **Kubernetes dışında** (IIS, sanal makineler): pod'lar bildirimlerinin geldiği ağ adresine göre gruplanır.

Yeni cluster'lar "Cluster 1", "Cluster 2", … olarak görünür ve **Sürümler** sekmesinde yeniden adlandırılabilir
(ör. "Prod İstanbul").

---

## Konsol mesajları

Kütüphane hiçbir zaman hata fırlatmaz, stack trace yazmaz. Konsola (ve `System.Diagnostics.Trace`'e) `[ConnectivityProbe]`
ile başlayan birkaç İngilizce satır yazar:

| Ne zaman | Mesaj |
|---|---|
| İlk başarılı bildirim | `Registered to monitor https://monitor.example.com as "orders-api" (3 connections).` |
| Monitor'e ulaşılamadı / reddetti | `Could not connect to monitor https://monitor.example.com: <neden>` (ilk seferde, sonra en fazla 5 dakikada bir) |
| Monitor'e yeniden ulaşıldı | `Reconnected to monitor https://monitor.example.com.` |
| Ayarlar eksik | `MonitorUrl and AppKey are required. Connectivity probe is disabled.` |
| Geçersiz URL | `Invalid MonitorUrl '...' (expected http:// or https://). Connectivity probe is disabled.` |

---

## Seçenekler

Hepsi isteğe bağlıdır; dördüncü parametreyle verilir:

```csharp
ConnectivityProbeAgent.Start(url, key, "Orders API", o =>
{
    o.PollSeconds = 10;
    o.IntervalSeconds = 60;
});
```

| Seçenek | Varsayılan | Anlamı |
|---|---|---|
| `PollSeconds` | 10 | Pod'un Monitor'e ne sıklıkla bildirim gönderdiği. Bildirimler geldikçe pod "çalışıyor" sayılır; "Şimdi test et" bu süre içinde ulaşır. |
| `IntervalSeconds` | Monitor'ünki (30) | Test aralığı (sn). |
| `TimeoutMs` | Monitor'ünki (5000) | Tek bağlantı denemesinin (ve DNS çözümlemesinin) zaman aşımı. |
| `MaxAddresses` | 64 | Bir host adı için test edilen en fazla IP. |
| `MaxParallelTests` | 4 | Bir pod'da aynı anda test edilen bağlantı sayısı. |

`ConnectivityProbeAgent.Current` çalışan agent'ı döner (süreç başına bir tane); `LastContactUtc`, `LastRunUtc`,
`LastError` ve `AppId` özelliklerini sunar.

---

## Güvenlik

- Kütüphane **hiç port ve uç açmaz**. Yalnızca `MonitorUrl`'e dışarı doğru HTTP(S) isteği yapar.
- Gönderdikleri: pod adı, makine adı, süreç kimliği, IP adresleri, işletim sistemi / .NET sürümü, uygulama adı / sürümü /
  build'i, cluster parmak izi, namespace ve yalnızca şu ortam değişkenleri: `POD_NAME`, `POD_NAMESPACE`, `POD_IP`,
  `NODE_NAME`, `HOSTNAME`, `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, `APP_POOL_ID`. Başka hiçbir değişken okunmaz.
- Pod yalnızca Monitor'de kendi uygulamasına bağlanmış bağlantıları test eder.
- Monitor iç ağlar için tasarlanmıştır: kayıt açıktır ve uygulama anahtarı uygulamayı tanımlar ama şifre değildir. Monitor
  arayüzünü `Monitor:AdminPassword` ile koruyun ve HTTPS arkasında yayınlayın.

---

## ConnectivityProbe Monitor

```bash
dotnet run --project src/ConnectivityProbe.Monitor
```

Varsayılan adres: http://localhost:5087/ . Monitor bağımsız bir uygulamadır, NuGet paketi değildir. Her ortam (test, prod)
için ayrı bir Monitor çalıştırın.

**Ekranlar**

- **Monitör:** en kritik uygulama üstte büyük bir afişte; ardından "Dikkat gerektirenler" satırı ve her ekip için yatay
  kayan bir satır. Her kart durumunu, pod sayısını, bağlantı özetini ve sürümünü gösterir. Uygulama, anahtar, ekip, sürüm,
  cluster veya pod adıyla arayın.
- **Detay penceresi:** cluster'a göre gruplanmış pod'lar (ad, IP'ler, sürüm, build, namespace, son bildirim), bağlantı ×
  pod matrisi (hangi pod hangi hedefe ne zamandır erişemiyor), geçmiş ve *Pod listesini sıfırla*.
- **Sürümler:** cluster'lar (yeniden adlandırılabilir) ve her cluster'da çalışan sürümleri gösteren uygulamalar ×
  cluster'lar tablosu.
- **Tanımlar:** Birimler → Ekipler → Uygulamalar. Her ekibin kendi bağlantı havuzu vardır; ortak havuz herkese açıktır.
  Bağlantıyı uygulamanın üzerine sürükleyerek bağlayın. Bir bağlantı kayıtlı başka bir uygulamayı gösterebilir
  ("hedef uygulama"); bu, matriste görünür.

**Pod durumları**

| Durum | Anlamı |
|---|---|
| Çalışıyor | Pod son 2 × `PollSeconds` (+5 sn) içinde bildirim gönderdi. |
| Bildirim gecikti | Bildirim gecikti ama eşik henüz dolmadı. Alarm değildir. |
| Eksik | `MissingAfterCycles` (3) test aralığı boyunca bildirim yok ve pod sayısı düştü. **Alarm.** Yalnızca *Pod listesini sıfırla* ile silinir; geri gelen pod kendiliğinden normale döner. |
| (ayrıldı) | Pod veda etti (deploy, scale-down) veya sayı aynı kalırken yerine yeni pod geldi. Alarm yok. |

**Ayarlar** (`appsettings.json` → `Monitor` veya ortam değişkenleri `Monitor__<Ad>`)

| Ayar | Varsayılan | Anlamı |
|---|---|---|
| `AdminUser` / `AdminPassword` | `admin` / boş | Arayüz girişi. Şifre yoksa arayüze yalnızca Monitor'ün çalıştığı makineden (localhost) erişilir. Agent uçları (`/api/agent/*`) hiçbir zaman giriş istemez. |
| `IntervalSeconds` | 30 | Pod'lara gönderilen test aralığı. |
| `ProbeTimeoutMs` | 5000 | Pod'lara gönderilen bağlantı zaman aşımı. |
| `MissingAfterCycles` | 3 | Bir pod'un "eksik" sayılması için bildirimsiz geçen test aralığı. |
| `DataFile` | `data/definitions.json` | Tanımlar. Pod durumu (`pod-state.json`) ve giriş anahtarları (`keys/`) yanında tutulur. |

Aynı makinede bir reverse proxy çalışıyorsa her istek localhost'tan geliyor görünür; bu durumda mutlaka `AdminPassword`
verin.

---

## 1.x'ten geçiş

2.0.0 kıran bir sürümdür: her şey artık 1.1'in Strict modu gibi, tek satır kodla çalışır.

| 1.x | 2.0 |
|---|---|
| `app.UseConnectivityProbe(...)`, hosting startup, IIS modülü, OWIN middleware, `ConnectivityProbeListener` | `ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)` |
| `/connectivity-probe/discover` ve `/identity` uçları, `AccessKey`, `AllowAnonymous`, `AllowedTargets` | Kaldırıldı. Uygulama hiç uç açmaz. |
| Discover modu (Monitor uygulamayı çağırır, pod sayısı tahmini) | Kaldırıldı. Pod'lar kendini bildirir; sayı kesindir. |
| Uygulama Monitor'de eklenir, `cpk_...` anahtarı üretilir | Anahtarı geliştirici belirler; ilk pod uygulamayı kaydeder. |
| Paket hedefleri: net8.0, netstandard2.0, net462 | netstandard2.0, net462 (bağımlılıksız) |

Adımlar:

1. Paketi 2.0.0'a güncelleyin; `app.UseConnectivityProbe(...)` satırını (ve `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`,
   `AccessKey`, `AllowAnonymous` ayarlarını) kaldırın.
2. Açılışa `ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)` ekleyin. Strict mod kullanan bir uygulama mevcut
   `cpk_...` anahtarını kullanmaya devam edebilir: Monitor bu uygulamaları ve bağlantılarını korur.
3. Monitor'ü 2.0'a güncelleyin. İlk açılışta Discover modunda kaydedilmiş (anahtarı olmayan) uygulamaları siler. Hâlâ 1.x
   kullanan uygulamalar çalışmaya devam eder ama güncellenene kadar 2.0 Monitor'de görünmez.

---

## Sürüm geçmişi

| Sürüm | Öne çıkanlar |
|---|---|
| **2.0.0** | Tek satır: `ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)`. Kendiliğinden kayıt, pod başına uygulama sürümü / build'i, otomatik cluster gruplama, uç yok, bağımlılık yok. |
| 1.1.0 | Strict mod: pod'lar uygulama anahtarıyla tanımlarını çeker, içeriden test eder ve sonucu gönderir. |
| 1.0.0 | İlk sürüm: `discover` ve `identity` uçları. |

Tam liste: [CHANGELOG](src/ConnectivityProbe/CHANGELOG.md) · Sürümler: [GitHub Releases](https://github.com/umutmemisoglu/ConnectivityProbe/releases) ·
Yeni sürüm yayınlama: [PUBLISHING.md](PUBLISHING.md)

## Lisans

[MIT](LICENSE) © 2026 Fatih Umut Memişoğlu
