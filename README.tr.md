# ConnectivityProbe

[English](README.md) | **Türkçe**

[![NuGet](https://img.shields.io/nuget/v/ConnectivityProbe.svg)](https://www.nuget.org/packages/ConnectivityProbe)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

ConnectivityProbe, dışarıdan cevaplanması zor soruları cevaplar:

1. **Uygulamam X'e _kendi pod'larının / sunucularının her birinin içinden_ erişebiliyor mu?**
   Örneğin `orders-api`'nin her pod'u `sql01:1433`'e, Redis'e ve ödeme API'sine TCP bağlantısı açabiliyor mu?
2. **Gerçekte kaç pod çalışıyor, hangi cluster'da ve her biri hangi sürümü / build'i çalıştırıyor?**

Uygulamanıza **tek satır kodla** eklediğiniz küçük bir .NET kütüphanesidir. Her pod, merkezi web uygulaması
**ConnectivityProbe Monitor**'e kendini bildirir; Monitor yüzlerce uygulamayı tek ekranda gösterir. Monitor ayrıca
çalıştırılır; bu deponun ve NuGet paketinin parçası değildir.

| Proje | Nedir |
|---|---|
| [`src/ConnectivityProbe`](src/ConnectivityProbe) | Kütüphane; NuGet'te [`ConnectivityProbe`](https://www.nuget.org/packages/ConnectivityProbe) olarak yayınlanır. Hedefler: `netstandard2.0` ve `net462`, **bağımlılığı yoktur**. |
| [`samples/ConnectivityProbe.SampleApi`](samples/ConnectivityProbe.SampleApi) | Örnek ASP.NET Core uygulaması. |
| [`tests/ConnectivityProbe.Tests`](tests/ConnectivityProbe.Tests) | Kütüphanenin birim ve uçtan uca testleri: `dotnet test tests/ConnectivityProbe.Tests` |

---

## İçindekiler

- [Nasıl çalışır](#nasıl-çalışır)
- [Desteklenen platformlar](#desteklenen-platformlar)
- [Platforma göre kurulum](#platforma-göre-kurulum)
- [Uygulama anahtarı](#uygulama-anahtarı)
- [Sürümler ve build'ler](#sürümler-ve-buildler)
- [Cluster'lar (ağlar)](#clusterlar-ağlar)
- [Kaynaklar, TLS ve gecikme](#kaynaklar-tls-ve-gecikme-21)
- [Ayarlardaki bağımlılıklar](#ayarlardaki-bağımlılıklar-22)
- [Health ucu ve bağlantı grupları](#health-ucu-ve-bağlantı-grupları-23)
- [Konsol mesajları](#konsol-mesajları)
- [Seçenekler](#seçenekler)
- [Güvenlik](#güvenlik)
- [Monitor](#monitor)
- [1.x'ten geçiş](#1xten-geçiş)
- [Sürüm geçmişi](#sürüm-geçmişi)

---

## Nasıl çalışır

```
uygulama açılır ──►  ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)

her 10 sn     ──►  POST {MonitorUrl}/api/agent/v2/report     (başlık X-ConnectivityProbe-AppKey)
                   "<anahtar> uygulamasının X pod'uyum, sürüm 1.4.0, IP 10.42.1.15"
                   ◄── bağlantı listesi + test aralığı
her 30 sn     ──►  her bağlantıyı bu pod'un içinden test et (TCP)
                   └─► sonuçlar hemen bir sonraki bildirimle gider
kapanırken    ──►  POST {MonitorUrl}/api/agent/v2/goodbye    → pod alarm üretmeden listeden çıkar
```

- **Kayıt adımı yoktur.** Yeni bir anahtarla bildirim gönderen ilk pod, uygulamayı Monitor'e kendiliğinden kaydeder. Aynı
  anahtarı kullanan pod'lar aynı uygulamadır; uygulama ikinci kez kaydedilmez.
- **HTTP pipeline'ınıza bir şey eklenmez.** Kütüphane uygulamanıza yol (route) eklemez; ayrı porttaki küçük health ucu
  (2.3+, kapatılabilir) dışında port açmaz. Pod'un yalnızca Monitor'e dışarı
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

Monitor sürümü her kartta, uygulamanın detay penceresinde de sürümler × ağlar tablosu olarak gösterir. Diğerlerinden farklı
sürüm veya build çalıştıran pod'lar işaretlenir (ör. rollout sırasında ya da deploy yalnızca bir cluster'a ulaştığında).

---

## Cluster'lar (ağlar)

Monitor pod'ları ağlarına göre **kendiliğinden** gruplar ve grubu ağın adıyla gösterir:

```
orders-a1 → 10.42.1.15 ┐
orders-a2 → 10.42.1.16 ├─ 10.42.0.0/16   (3 pod)
orders-a3 → 10.42.2.17 ┘
orders-b1 → 10.43.1.21 ┐
orders-b2 → 10.43.1.22 ┘─ 10.43.0.0/16   (2 pod)
```

- **Pod adresi (2.1+):** `POD_IP` (Kubernetes Downward API) verilmişse o; yoksa pod'un Monitor'e giderken kullandığı yerel
  adres; o da yoksa makine adının ilk IPv4 adresi.
- **Ağ:** IPv4'te `/16` (Kubernetes her cluster'a ayrı bir pod ağı, her node'a da bunun bir `/24`'ünü verir; bir cluster'ın
  tüm node'ları aynı `/16`'ya düşer), IPv6'da `/64`.
- **Kubernetes:** pod'lar ayrıca cluster'ın CA sertifikasının parmak iziyle ayrılır
  (`/var/run/secrets/kubernetes.io/serviceaccount/ca.crt`; SHA-256, sertifikanın kendisi asla gönderilmez). Böylece aynı pod
  ağını kullanan iki cluster (ör. ikisi de varsayılan `10.42.0.0/16`) yine iki ayrı grup olur ve `10.42.0.0/16 · e88c3c`
  şeklinde gösterilir. Namespace de aynı klasörden okunur.

Ağa detay penceresinde ad verilebilir (yanındaki ✎, ör. "Prod İstanbul"); boş bırakılırsa yeniden ağ adresi gösterilir.

---

## Kaynaklar, TLS ve gecikme (2.1+)

Pod her bildirimde (10 saniyede bir) **kaynak kullanımını** da gönderir:

| | Her yerde | Linux container'da (Kubernetes, Docker) |
|---|---|---|
| CPU | sürecin kullandığı çekirdek | **CPU limiti** ve **throttling** (limit yüzünden yavaşlatılan dönemlerin oranı) |
| Bellek | working set, private bytes, GC heap, GC sayıları | **bellek kullanımı ve limiti**, **OOM kill** |
| Thread'ler | thread'ler, meşgul thread pool worker'ları, handle'lar | |
| Ağ | | gelen / giden byte (`/proc/net/dev`) |
| TCP soketleri | | kurulu, TIME_WAIT, yerel port aralığı (port tükenmesi) |

Monitor ayrıca **yeniden başlamaları** (aynı pod, yeni süreç) tespit eder. Hiçbir ayar gerekmez; bir platformda okunamayan
değerler boş kalır, okuma hiçbir zaman hata fırlatmaz.

Her bağlantıda TCP testine ek olarak:

- **DNS süresi** ayrıca ölçülür; ad için **yeni bir IP adresi** dönerse (DNS değişikliği, failover) işaretlenir. Round-robin
  DNS sahte uyarı üretmez: daha önce görülen adresler hatırlanır.
- **Yavaşlayan** bağlantılar işaretlenir: son 3 ölçüm olağanın 3 katından (ve en az 50 ms) yavaş.
- **TLS / sertifika kontrolü:** `https://` ve 443, 8443, 636, 993, 995, 465, 5671 portlarında otomatik (bağlantı bazında açık /
  kapalı da seçilebilir). Pod TCP bağlantısından sonra TLS el sıkışması yapar; protokolü, sertifika sahibini, vereni,
  **bitiş tarihini** ve sertifika hatalarını (güvenilmeyen, ad uyuşmazlığı, süresi dolmuş) bildirir. Geçersiz sertifika
  başarısız test sayılır.

**Uyarılar** (eşikler `Monitor:Alerts` altında):

| Uyarı | Varsayılan | Etkisi |
|---|---|---|
| Bellek ≥ container limitinin %'si | 90 | sorunlu |
| Son N dakikada yeniden başlama veya OOM kill | 60 | sorunlu |
| TCP soketleri ≥ yerel port aralığının %'si | 70 | sorunlu |
| Sertifikanın bitişine N gün | 14 | sorunlu |
| CPU throttling ≥ % | 25 | not |
| Yavaş bağlantı, değişen IP | – | not |

---

## Ayarlardaki bağımlılıklar (2.2+)

Pod'a uygulamanın ayarlarını verirseniz, ayarlardaki veritabanları, kuyruklar ve servis adresleri Monitor'de kendiliğinden
tanımlanır; değişiklikler takip edilir.

### Ayarlar nereden verilir

Ayarlar **`Start`'ın dördüncü parametresinde** (seçenek bloğu) verilir. `Start`'ın kendisi değişmedi; bunlardan hiçbiri
verilmezse ayarlardan tespit kapalıdır ve her şey önceki gibi çalışır. Üçünden **birini** kullanın:

| Seçenek | Ne zaman | Pod ne yapar |
|---|---|---|
| `o.SettingsUrl` | Ayarlar JSON dönen bir adresten geliyorsa (ör. merkezi ayar servisi) | Adresi düz bir `GET` ile (ek başlık olmadan) her `SettingsRefreshMinutes` dakikada bir okur |
| `o.SettingsJson` | Ayarlar elinizde JSON metni olarak varsa | Metni tarar |
| `o.Settings` | Uygulama ayarlarını zaten bir nesneye okuduysa (ör. `RemoteSettings`, `IOptions<T>.Value`) | Nesneyi bellekte JSON'a çevirip tarar |

Birden fazlası verilirse önce `SettingsJson`, sonra `Settings`, sonra `SettingsUrl` kullanılır.

**1. Ayar adresi** (ASP.NET Core; adres yapılandırmadan gelir, böylece aynı kod her ortamda çalışır):

```csharp
ConnectivityProbeAgent.Start(
    monitorUrl: builder.Configuration["ConnectivityProbe:MonitorUrl"]!,
    appKey:     "orders-api",
    appName:    "Orders API",
    configure:  o => o.SettingsUrl = builder.Configuration["SettingsFullUrl"]);   // ör. https://settings.example.com/api/settings/obj/<id>?version=1.0.0
```

**2. JSON metni** (ör. pod'a bağlanan bir dosya ya da zaten indirdiğiniz metin):

```csharp
ConnectivityProbeAgent.Start(monitorUrl, "orders-api", "Orders API",
    o => o.SettingsJson = File.ReadAllText("/config/settings.json"));
```

**3. Uygulamanın zaten okuduğu ayar nesnesi** (uygulama ayarlarını açılışta zaten indiriyorsa önerilen yol: ayar servisi
ikinci kez çağrılmaz, adresi de koda ikinci kez yazılmaz):

```csharp
var remoteSettings = LoadRemoteSettings();                 // uygulamanın kendi kodu
builder.Services.AddSingleton<IRemoteSettings>(remoteSettings);

ConnectivityProbeAgent.Start(monitorUrl, "orders-api", "Orders API",
    o => o.Settings = remoteSettings);
```

`Settings` / `SettingsJson` ile açılışta verilen değer kullanılır; yeniden başlatmadan değişiklikleri takip etmek için
`SettingsUrl` kullanın. İlgili diğer seçenek: `SettingsRefreshMinutes` (varsayılan 10).

Nerede görülür: sonuç değiştiğinde pod konsola tek satır yazar, ör.
`[ConnectivityProbe] Settings: 4 dependencies found (1 postgresql, 1 rabbitmq, 1 http, 1 mongodb).` Monitor'de
**Tanımlar → uygulamanın kartı → ⚙ Ayarlardan bulunanlar** bölümünü açın.

**Güvenlik:** Ayarlar **yalnızca pod'da** okunur. Monitor'e yalnızca ayarın adı, tür, sunucu, port ve TLS bilgisi gider;
şifre, kullanıcı adı, anahtar ve ayar adresinin kendisi hiçbir zaman gitmez. Adı password / secret / token / key olan
alanlara hiç bakılmaz.

**Adresler nasıl tanınır (yapay zekâ yok, yalnızca kurallar):**

1. **Adaylar:** bağlantı metinleri (`Host=..;` PostgreSQL, `Server=..;Initial Catalog=..` SQL Server, `Uid=` MySQL,
   `(HOST=..)(PORT=..)` Oracle, `host:6379,ssl=true` Redis), adresler (`https://`, `mongodb://`, `amqp(s)://`, `redis(s)://`,
   `postgres://`, `mysql://`, `kafka://`, `ldap(s)://`, `smtp(s)://`...), `host:port` ve `h1:9092,h2:9092` listeleri,
   aynı nesnedeki host + port alanları (`{"Host": "mq01", "Port": 0}`, `SmtpServer` + `SmtpPort`).
2. **Tür:** şemadan, bağlantı metninden, porttan (5672 RabbitMQ, 9092 Kafka, 27017 MongoDB, 5601 Kibana, 9200
   Elasticsearch, 6379 Redis...), ayarın adından (`RabbitMQSettings`, `KibanaUrl`) ve sunucu adından anlaşılır. Port yoksa
   ya da `0` ise türün varsayılanı kullanılır (RabbitMQ 5672). Kesin biçimler **kesin** sayılır ve kendiliğinden eklenir.
   Yalnızca adrese benzeyen değerler (ör. `"Endpoint": "srv01:8080"`) **öneri** olur, onaylarsanız eklenir. DNS'te
   çözülmeyen adlar elenir. `localhost`, örnek alan adları ve şema adresleri yok sayılır.
3. **Doğrulama (isteğe bağlı, Monitor'de uygulama bazında):** Pod her adrese bağlanıp o protokolün zararsız "merhaba"sını
   gönderir (AMQP başlığı, Kafka ApiVersions, MongoDB `hello`, Redis `PING`, PostgreSQL SSLRequest, SQL Server PRELOGIN,
   HTTP `GET /` → Kibana / Elasticsearch / RabbitMQ yönetim ekranı...). Tür sunucunun cevabından alınır; doğrulanan öneri
   kendiliğinden eklenir, hiç bağlanılamayan belirsiz adres elenir. Kimlik bilgisi gönderilmez, veri okunmaz. **Kesin
   bağımlılıklar asla elenmez:** erişilemeyen bir veritabanı tam da izlenmesi gereken durumdur.

**Monitor'de** (Tanımlar → uygulama kartı → ⚙ Ayarlardan bulunanlar):

- Yeni adres: havuzda aynı host:port'a giden bağlantı varsa o kullanılır, yoksa bağlantı oluşturulur (⚙ işaretli) ve atanır.
- Değişen adres (aynı ayar, yeni sunucu): eski bağlantı uygulamadan çıkarılır, yenisi atanır. Kaldırılan ayar: çıkarılır.
  Ayarlardan oluşturulmuş ve artık kimsenin kullanmadığı bağlantılar havuzdan silinir; elle eklenenlere dokunulmaz.
- Her ekleme / değişiklik / kaldırma kartta geçmiş olarak görünür.
- Sizin kaldırdığınız bir bağımlılık bir daha eklenmez ("Yok sayılanlar" altında, "Tekrar ekle" ile geri alınır).
- Ayarlar `SettingsRefreshMinutes` (10) dakikada bir yeniden okunur. Okuma başarısız olursa önceki sonuç geçerli kalır
  (uyarıyla gösterilir).
- Deploy sırasında yalnızca en son başlayan pod'un ayarları uygulanır; eski pod'lar tanımları geri alamaz.

## Health ucu ve bağlantı grupları (2.3+)

**Health ucu.** ConnectivityProbe kullanan her uygulama **ayrı bir portta** kendi küçük health ucunu açar:

```
http://<pod-ip>:29470/connectivityprobe/health
```

- ASP.NET Core, klasik ASP.NET / IIS, Worker Service, konsol ve Windows servislerinde, desteklenen tüm .NET sürümlerinde
  aynı çalışır: kütüphane portu kendisi dinler (ham TCP), web sunucusu ya da ek satır gerekmez.
- Uygulamanın kendi `/health` ucuyla asla çakışmaz: port da yol da farklıdır.
- **29470 portu** işletim sistemlerinin giden bağlantılara verdiği aralıkların (Linux 32768–60999, Windows 49152–65535)
  dışındadır ve yaygın servislerce kullanılmaz. Port başkasıyla paylaşılmadan açılır; doluysa sıradaki 9 port, o da olmazsa
  boş bir port kullanılır. Kullanılan port Monitor'e bildirilir.
- Cevapta ConnectivityProbe imzası (`X-ConnectivityProbe` başlığı ve `connectivityProbe` alanı), durum (`Healthy`; son
  testte erişilemeyen bağlantı varsa `Degraded`), uygulama, sürüm ve pod bilgisi vardır. Uygulama anahtarı cevapta yoktur.
- Seçenekler: `HealthEnabled` (varsayılan `true`), `HealthPort` (varsayılan `29470`).

**Kim kullanır.** Bağlantı testleri yine **pod'ların içinden** yapılır. **Monitor** her pod'un health ucunu sorgular
(detay sayfasında pod başına health) ve uygulamanın kullandığı servislerin ConnectivityProbe kullanıp kullanmadığına bakar:
imzalı cevap *var* demektir (bağlantının hedef uygulaması da kendiliğinden işaretlenir), "bağlantı reddedildi" ya da portta
başka bir servis *yok* demektir, zaman aşımı *belirsiz*dir (ör. Kubernetes Service yalnızca uygulamanın portunu açar).
Monitor'ün pod'lara / servislere bu port üzerinden ağ erişimi olmalıdır.

**Ingress arkasındaki servisler (2.4+).** Ingress ya da yük dengeleyici yalnızca uygulamanın web portunu dışarı açar;
servisin dış adresinden (ör. `https://orders.example.com`) 29470'e ulaşılamaz. Bu yüzden ConnectivityProbe uygulamanın
**hangi adreslerle çağrıldığını** da kendiliğinden öğrenir: ASP.NET Core'da çatının her isteğin başında yayınladığı olayı
dinler ve isteğin sunucu adını okur (isteğe dokunulmaz; IP adresleri ve localhost alınmaz, en fazla 20 ad). Pod'lar bu adları
Monitor'e bildirir; Monitor de başka uygulamaların bağlantı adresleriyle eşleştirir: `orders.example.com` yalnızca *Orders
API* için çağrılıyorsa o adrese giden her bağlantının hedef uygulaması kesin olarak *Orders API* olur ve sağlığı Orders API'nin
pod'larından gösterilir. Uygulamada hiçbir şey eklenmez ya da ayarlanmaz. Aynı ad birden fazla uygulama için çağrılıyorsa (ör.
yola göre yönlendiren bir ingress) hedef işaretlenmez. Kapatmak için `o.ReportServedHosts = false`. Worker, konsol ve klasik
ASP.NET uygulamaları adres bildirmez.

**Bağlantı grupları.** Anahtar kelimeleriyle grup verin; Monitor adında ya da adresinde kelimelerden biri geçen bağlantıları
uygulamanın detay sayfasında o grubun altında toplar:

```csharp
ConnectivityProbeAgent.Start(url, key, "Orders API", o =>
{
    o.AddGroup("Ödeme servisleri", "pay", "billing");
    o.AddGroup("Arama", "elastic", "kibana");
});
```

Diğer bağlantılar türüne göre gösterilir: Servisler, Veritabanları, Kuyruklar, Cache, Diğer.

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
| `SettingsUrl` / `SettingsJson` / `Settings` | — | Uygulamanın ayarları: içindeki bağımlılıklar Monitor'de kendiliğinden tanımlanır (bkz. [Ayarlardaki bağımlılıklar](#ayarlardaki-bağımlılıklar-22)). |
| `SettingsRefreshMinutes` | 10 | Ayarların yeniden okunma aralığı (dk). |
| `HealthEnabled` / `HealthPort` | true / 29470 | Ayrı portta ConnectivityProbe health ucu (bkz. [Health ucu](#health-ucu-ve-bağlantı-grupları-23)). |
| `ReportServedHosts` | true | Uygulamanın çağrıldığı sunucu adları bildirilir (ASP.NET Core); Monitor başka uygulamaların bağlantılarının hedefini bunlardan kesin olarak bulur (bkz. [Ingress arkasındaki servisler](#health-ucu-ve-bağlantı-grupları-23)). |
| `AddGroup(ad, kelimeler…)` | — | Bağlantıları Monitor'de anahtar kelimeye göre gruplar. |

`ConnectivityProbeAgent.Current` çalışan agent'ı döner (süreç başına bir tane); `LastContactUtc`, `LastRunUtc`,
`LastError` ve `AppId` özelliklerini sunar.

---

## Güvenlik

- Kütüphane **uygulamanızın HTTP pipeline'ına bir şey eklemez**. `MonitorUrl`'e dışarı doğru HTTP(S) isteği yapar ve
  2.3'ten itibaren kendi portunda salt okunur bir health ucu açar (`o.HealthEnabled = false` ile kapatılır).
- Gönderdikleri: pod adı, makine adı, süreç kimliği, IP adresleri, işletim sistemi / .NET sürümü, uygulama adı / sürümü /
  build'i, cluster parmak izi, namespace, kaynak ölçümleri (CPU, bellek, thread'ler, TCP soket sayıları, ağ byte sayaçları),
  test sonuçları (TLS bağlantılarında sunucu sertifikasının sahibi, vereni ve tarihleri) ve yalnızca şu ortam değişkenleri: `POD_NAME`, `POD_NAMESPACE`, `POD_IP`,
  `NODE_NAME`, `HOSTNAME`, `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, `APP_POOL_ID`. Başka hiçbir değişken okunmaz.
- Pod yalnızca Monitor'de kendi uygulamasına bağlanmış bağlantıları test eder.
- Monitor iç ağlar için tasarlanmıştır: kayıt açıktır ve uygulama anahtarı uygulamayı tanımlar ama şifre değildir. Monitor'ü
  yalnızca iç ağda, HTTPS arkasında yayınlayın.

---

## Monitor

Kütüphane bir ConnectivityProbe Monitor'e (`MonitorUrl`) bildirim gönderir: Monitor bu deponun ve NuGet paketinin
parçası olmayan ayrı bir web uygulamasıdır. Her ortam (test, prod) için bir Monitor çalıştırılır; pod'ların yalnızca ona
dışarı doğru HTTP(S) erişimi olmalıdır.

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
| **2.4.0** | Ingress arkasındaki servisler kesin olarak tanınır: pod'lar uygulamanın çağrıldığı sunucu adlarını bildirir (ASP.NET Core, yalnızca okunur, ayar gerekmez), Monitor bunları başka uygulamaların bağlantı adresleriyle eşleştirir (`ReportServedHosts`). |
| 2.3.0 | Her tür uygulamada ayrı portta health ucu (`http://<pod-ip>:29470/connectivityprobe/health`), ConnectivityProbe imzasıyla; Monitor pod sağlığını ve servislerin ConnectivityProbe kullanıp kullanmadığını sorgular. Bağlantı grupları (`o.AddGroup`). |
| 2.2.0 | Ayarlardaki bağımlılıklar: `SettingsUrl` / `SettingsJson` / `Settings` verilirse içindeki veritabanları, kuyruklar ve servisler Monitor'de kendiliğinden tanımlanır (pod'dan yalnızca tür, sunucu ve port çıkar); değişiklik takibi ve isteğe bağlı protokol doğrulaması (RabbitMQ, Kafka, MongoDB, Redis, PostgreSQL, SQL Server, Kibana, Elasticsearch...). Monitor: isteğe bağlı MongoDB depolama. |
| 2.1.0 | Pod kaynakları (CPU, bellek ve limitler, throttling, OOM, thread'ler, TCP soketleri), TLS / sertifika kontrolü, DNS süresi, yavaşlama ve yeniden başlama tespiti. Pod'lar kendi IP adresini bildirir (`POD_IP` veya Monitor'e giden yol); Monitor cluster'ları pod ağıyla adlandırır (ör. `10.42.0.0/16`). Monitor: sürümler ve ağ gruplaması detay penceresinde, Türkçe / İngilizce arayüz. |
| 2.0.0 | Tek satır: `ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)`. Kendiliğinden kayıt, pod başına uygulama sürümü / build'i, otomatik cluster gruplama, uç yok, bağımlılık yok. |
| 1.1.0 | Strict mod: pod'lar uygulama anahtarıyla tanımlarını çeker, içeriden test eder ve sonucu gönderir. |
| 1.0.0 | İlk sürüm: `discover` ve `identity` uçları. |

Tam liste: [CHANGELOG](src/ConnectivityProbe/CHANGELOG.md) · Sürümler: [GitHub Releases](https://github.com/umutmemisoglu/ConnectivityProbe/releases) ·
Yeni sürüm yayınlama: [PUBLISHING.md](PUBLISHING.md)

## Lisans

[MIT](LICENSE) © 2026 Fatih Umut Memişoğlu
