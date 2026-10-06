# ConnectivityProbe

[English](README.md) | **Türkçe**

[![NuGet](https://img.shields.io/nuget/v/ConnectivityProbe.svg)](https://www.nuget.org/packages/ConnectivityProbe)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

ConnectivityProbe, dışarıdan cevaplanması zor iki soruyu cevaplar:

1. **Uygulamam X'e _kendi pod'larının / sunucularının her birinin içinden_ erişebiliyor mu?**
   Örneğin `orders-api`'nin her pod'u `sql01:1433`'e, Redis'e ve ödeme API'sine TCP bağlantısı açabiliyor mu?
2. **Bir servisin arkasında gerçekte kaç instance (pod) çalışıyor ve hangisi bozuk?**

Uygulamanıza eklediğiniz küçük bir .NET kütüphanesidir. Bu depodaki merkezi web uygulaması **ConnectivityProbe Monitor**, onu
kullanarak yüzlerce uygulamayı tek ekranda izler.

| Proje | Nedir |
|---|---|
| [`src/ConnectivityProbe`](src/ConnectivityProbe) | Kütüphane; NuGet'te [`ConnectivityProbe`](https://www.nuget.org/packages/ConnectivityProbe) olarak yayınlanır. Hedefler: `net462`, `netstandard2.0`, `net8.0`. |
| [`src/ConnectivityProbe.Monitor`](src/ConnectivityProbe.Monitor) | Merkezi izleme uygulaması (ASP.NET Core web uygulaması). Uygulamaları ve bağlantıları kaydeder; pod'ları ve sonuçları gösterir. NuGet paketi **değildir**. |
| [`samples/ConnectivityProbe.SampleApi`](samples/ConnectivityProbe.SampleApi) | Kütüphaneyi tek satır kod yazmadan devreye alan örnek ASP.NET Core uygulaması. |
| [`tests/ConnectivityProbe.Tests`](tests/ConnectivityProbe.Tests) | Birim ve uçtan uca testler (gerçek Kestrel sunucularıyla): `dotnet test tests/ConnectivityProbe.Tests` |

---

## İçindekiler

- [İki mod: Discover ve Strict](#i̇ki-mod-discover-ve-strict)
- [Desteklenen platformlar](#desteklenen-platformlar)
- [Platforma göre kurulum](#platforma-göre-kurulum)
- [Strict mod ayrıntıları](#strict-mod-ayrıntıları)
- [Discover mod ayrıntıları](#discover-mod-ayrıntıları)
- [Ayarlar](#ayarlar)
- [Uçlar](#uçlar)
- [Güvenlik](#güvenlik)
- [ConnectivityProbe Monitor](#connectivityprobe-monitor)
- [Sürümler](#sürümler)

---

## İki mod: Discover ve Strict

Her uygulama Monitor'de iki moddan biriyle tanımlanır.

| | **Discover** | **Strict** (1.1.0+) |
|---|---|---|
| Testi kim başlatır | Monitor uygulamanın adresini çağırır. | Her pod Monitor'e kendisi başvurur. |
| Test nasıl yapılır | İstek load balancer üzerinden bir pod'a düşer; o pod hedefi kendi içinden test edip cevap verir. Monitor bunu tüm pod'ları görene kadar tekrarlar. | Her pod, bağlantı listesini Monitor'den alır (**uygulama anahtarıyla** tanınır), her bağlantıyı kendi içinden test eder ve sonucu gönderir. |
| Pod sayısı | **Tahmini**: istekler rastgele dağıldığı için sonuç bir güven oranıyla verilir (ör. %95). | **Kesin**: her pod kendini bildirir. |
| Pod başına sonuç | Olasılıksal: load balancer'ın hiç seçmediği pod son bilinen sonucuyla gösterilir. | Her pod, her turda. |
| Ağ yönü | Monitor → uygulama | Uygulama (pod) → Monitor |
| Uygulamada gerekenler | Paket + uçların açık olması (`AccessKey` veya `AllowAnonymous`) | Paket (1.1.0+) + `MonitorUrl` + `AppKey` |
| Uygulama adresi | Zorunlu | İsteğe bağlı. Verilirse Monitor dışarıdan erişimi de kontrol eder. |
| HTTP'si olmayan uygulamalar (kuyruk tüketicileri, worker'lar) | Hayır | **Evet** |
| Deploy / scale-down | Yerine yenisi gelen pod birkaç tur sonra sessizce düşer. | Düzgün kapanan pod "kapanıyorum" der ve alarm vermeden hemen çıkar. |
| "Şimdi test et" düğmesi | Hemen | `Strict:CommandPollSeconds` içinde (varsayılan 10 sn) |

**Hangisini seçmeliyim?**
- Kesin pod sayısı ve pod başına kesin sonuç istiyorsanız, uygulama HTTP'si olmayan bir worker ise ya da Monitor
  uygulamaya erişemiyor ama uygulama Monitor'e erişebiliyorsa **Strict**.
- Uygulamanın yapılandırmasını değiştiremiyorsanız ya da uygulama Monitor'e erişemiyorsa **Discover**.
- İkisi birlikte kullanılabilir: Strict modda `discover` ve `identity` uçları çalışmaya devam eder; diğer uygulamalar bu
  uygulamanın pod'larını yine keşfedebilir.

---

## Desteklenen platformlar

| Uygulama türü | Paketteki hedef | Discover uçları | Strict mod (1.1.0+) |
|---|---|---|---|
| ASP.NET Core **.NET 8 / 9 / 10** | `net8.0` | `app.UseConnectivityProbe()` veya kodsuz (ortam değişkeni) | Uygulamayla birlikte kendiliğinden başlar ve durur |
| ASP.NET Core **2.1 – 7** (.NET Core 2.1, 3.1, .NET 5, 6, 7) | `netstandard2.0` | Yukarıdakiyle aynı | Yukarıdakiyle aynı |
| **IIS'te klasik ASP.NET** (MVC 5, Web API 2, WebForms, WCF) – .NET Framework **4.6.2+** | `net462` | **Kodsuz**: `bin`'deki DLL kendini kaydeder | Uygulamayla birlikte kendiliğinden başlar |
| **OWIN self-host** (Katana, Web API 2 self-host) | `net462` / `netstandard2.0` | `app.Use(typeof(ConnectivityProbeOwinMiddleware))` | Middleware ile başlar; kapanırken `Stop()` çağırın |
| **Web sunucusu olmayan**: Worker Service, konsol, Windows Service | hepsi | `ConnectivityProbeListener.Start()` (kendi küçük HTTP dinleyicisi) | `ConnectivityProbeAgent.Start()` / `Stop()` |

NuGet doğru hedefi kendisi seçer. .NET Framework hedefinin **hiç NuGet bağımlılığı yoktur**; binding redirect gerekmez.
.NET Framework 4.6.1 ve öncesi desteklenmez.

---

## Platforma göre kurulum

```bash
dotnet add package ConnectivityProbe
```

Tüm ayarlar yapılandırmadan `ConnectivityProbe` ön ekiyle okunur:

| Kaynak | Örnek |
|---|---|
| `appsettings.json` (ASP.NET Core) | `"ConnectivityProbe": { "MonitorUrl": "https://monitor.example.com" }` |
| Ortam değişkeni (tüm platformlar) | `ConnectivityProbe__MonitorUrl=https://monitor.example.com` |
| `web.config` / `app.config` `<appSettings>` (.NET Framework) | `<add key="ConnectivityProbe:MonitorUrl" value="https://monitor.example.com" />` |
| Kod | `options.MonitorUrl = "https://monitor.example.com";` |

Aşağıdaki örnekler hem uçları (Discover modu için) hem Strict modu açar. Yalnızca Discover modu gerekiyorsa
`MonitorUrl` / `AppKey`'i vermeyin.

### ASP.NET Core 6, 7, 8, 9, 10 (minimal hosting, `Program.cs`)

```csharp
using ConnectivityProbe;

var builder = WebApplication.CreateBuilder(args);
// ... servisleriniz
var app = builder.Build();

// Erken ekleyin: UseHttpsRedirection'dan ve kendi kimlik doğrulamanızdan önce.
// Uçlar kendi erişim anahtarıyla (veya AllowAnonymous ile) korunur.
app.UseConnectivityProbe(options =>
{
    options.Info["app"] = "orders-api";            // isteğe bağlı: identity yanıtında ve Monitor'de görünür
});

app.UseHttpsRedirection();
// ... pipeline'ın geri kalanı
app.Run();
```

`appsettings.json` (veya karşılık gelen ortam değişkenleri):

```json
"ConnectivityProbe": {
  "AccessKey": "",
  "MonitorUrl": "https://monitor.example.com",
  "AppKey": ""
}
```

`AccessKey` ve `AppKey`'i `appsettings.json`'a değil, secret / ortam değişkeni olarak verin:
`ConnectivityProbe__AccessKey`, `ConnectivityProbe__AppKey`.

### ASP.NET Core 2.1 – 5 (`Startup.cs`)

```csharp
using ConnectivityProbe;

public void Configure(IApplicationBuilder app, IHostingEnvironment env)   // 3.0+ için IWebHostEnvironment
{
    app.UseConnectivityProbe();      // Configure'un ilk satırı; ayarlar yapılandırmadan okunur
    // ... app.UseMvc(), app.UseRouting(), ...
}
```

### Kod değiştirmeden ASP.NET Core (2.1 – 10 tüm sürümler)

ASP.NET Core'un resmi "hosting startup" mekanizması, referans verilen bir kütüphaneyi ortam değişkeniyle devreye alır.
Yalnızca paketi ekler ve deployment ayarlarını değiştirirsiniz:

```yaml
env:
  - name: ASPNETCORE_HOSTINGSTARTUPASSEMBLIES
    value: ConnectivityProbe                 # başka hosting startup'lar varsa: "Diger.Assembly;ConnectivityProbe"
  - name: ConnectivityProbe__MonitorUrl
    value: https://monitor.example.com
  - name: ConnectivityProbe__AppKey
    valueFrom: { secretKeyRef: { name: connectivity-probe, key: app-key } }
```

IIS'te `web.config` → `<aspNetCore><environmentVariables>`, Docker'da `ENV`, yerelde `launchSettings.json` kullanılır. Ayrıca
`app.UseConnectivityProbe()` de çağırırsanız ikinci kez eklenmez.

### IIS'te klasik ASP.NET (.NET Framework 4.6.2+)

Paketi ekleyin (veya `ConnectivityProbe.dll`'i `bin`'e kopyalayın). Başka bir şey gerekmez: modül uygulama başlarken kendini
kaydeder. Ayarlar `web.config`'e girer:

```xml
<appSettings>
  <add key="ConnectivityProbe:AccessKey" value="..." />
  <add key="ConnectivityProbe:MonitorUrl" value="https://monitor.example.com" />
  <add key="ConnectivityProbe:AppKey" value="cpk_..." />
  <add key="ConnectivityProbe:Info:app" value="orders-web" />
</appSettings>
```

- **IIS'te Strict mod:** IIS, istek gelmeyen uygulama havuzunu varsayılan olarak 20 dakika sonra durdurur; arka plan işi de
  onunla durur. Uygulama havuzunda `Start Mode = AlwaysRunning` ve `Idle Time-out = 0`, sitede `Preload Enabled = true`
  ayarlayın. Aksi halde havuz uykudayken pod "eksik" görünür.
- **Classic pipeline modu:** otomatik kayıt Integrated pipeline ister. Classic modda
  `<system.web><httpModules><add name="ConnectivityProbe" type="ConnectivityProbe.ConnectivityProbeModule, ConnectivityProbe" /></httpModules></system.web>`
  ekleyin ve `ConnectivityProbe:AutoRegister=false` verin.
- **.NET Framework 4.6.2 – 4.7 ve HTTPS:** TLS 1.2 açık olmalıdır (`httpRuntime targetFramework="4.7"` veya üstü ya da
  `ServicePointManager.SecurityProtocol`).

### OWIN self-host

```csharp
using ConnectivityProbe;

public void Configuration(IAppBuilder app)
{
    app.Use(typeof(ConnectivityProbeOwinMiddleware));   // ayarlar app.config / ortam değişkenlerinden
    // ...
}

// Kapanırken (OWIN'de standart bir kapanış olayı yoktur), Monitor'e "kapanıyorum" gitsin diye:
ConnectivityProbeAgent.Current?.Stop();
```

### Worker Service, konsol, Windows Service (web sunucusu yok)

**Strict mod** (worker'lar için önerilir): uygulamanın yalnızca Monitor'e erişebilmesi yeterlidir.

```csharp
using ConnectivityProbe;

// .NET Generic Host (Worker Service): host ile birlikte başlat ve durdur
builder.Services.AddHostedService<ConnectivityProbeAgentService>();

sealed class ConnectivityProbeAgentService : IHostedService
{
    private readonly IConfiguration _config;
    private ConnectivityProbeAgent? _agent;

    public ConnectivityProbeAgentService(IConfiguration config) => _config = config;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _agent = ConnectivityProbeAgent.Start(new ConnectivityProbeOptions
        {
            MonitorUrl = _config["ConnectivityProbe:MonitorUrl"],
            AppKey = _config["ConnectivityProbe:AppKey"],
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _agent?.Stop();          // Monitor'e "kapanıyorum" bildirir
        return Task.CompletedTask;
    }
}
```

Host yoksa ayarlar ortam değişkenlerinden (.NET Framework'te ayrıca `app.config`'ten) okunur:

```csharp
var agent = ConnectivityProbeAgent.Start();   // MonitorUrl / AppKey yoksa null döner
// ... uygulama çalışır
agent?.Stop();
```

Web sunucusu olmadan **Discover uçları**: `ConnectivityProbeListener` küçük bir HTTP dinleyicisi açar (varsayılan
`http://+:8099/`). Strict ayarları verilmişse agent'ı da başlatır.

```csharp
var probe = ConnectivityProbeListener.Start();
// ...
probe?.Dispose();
```

Windows'ta `http://+:port/` dinlemek yönetici yetkisi ister. Bir kez
`netsh http add urlacl url=http://+:8099/ user="NT AUTHORITY\NETWORK SERVICE"` çalıştırın veya
`ConnectivityProbe:ListenerPrefixes=http://localhost:8099/` kullanın.

### Kubernetes: tam örnek

```yaml
env:
  # Strict mod
  - name: ConnectivityProbe__MonitorUrl
    value: https://monitor.example.com
  - name: ConnectivityProbe__AppKey
    valueFrom: { secretKeyRef: { name: connectivity-probe, key: app-key } }
  # Discover uçları (ortak anahtar; iç servislerde ConnectivityProbe__AllowAnonymous=true da olur)
  - name: ConnectivityProbe__AccessKey
    valueFrom: { secretKeyRef: { name: connectivity-probe, key: access-key } }
  # Pod bilgisi (Downward API): Monitor'de pod adları ve hostNetwork pod'larının ayrı kimlik alması için
  - name: POD_NAME
    valueFrom: { fieldRef: { fieldPath: metadata.name } }
  - name: POD_NAMESPACE
    valueFrom: { fieldRef: { fieldPath: metadata.namespace } }
  - name: NODE_NAME
    valueFrom: { fieldRef: { fieldPath: spec.nodeName } }
```

---

## Strict mod ayrıntıları

### Kurulum

1. Monitor'de (Tanımlar sekmesi) uygulamayı **Mod = Strict** ile ekleyin. Bir **uygulama anahtarı** (`cpk_...`) üretilir.
   Anahtar uygulama kartında görünür; yanında *Kopyala*, *Yenile* düğmeleri ve kopyalanmaya hazır bir *Kurulum bilgisi*
   bölümü vardır.
2. Uygulamanın test edeceği bağlantıları atayın (bağlantı havuzundan sürükleyip bırakın).
3. Uygulamaya iki ayar verin, `ConnectivityProbe:MonitorUrl` ve `ConnectivityProbe:AppKey` (yukarıdaki platform örneklerine
   bakın), ve deploy edin.
4. Yaklaşık 10 saniye içinde her pod Monitor'de görünür; pod sayısı kesindir ve her pod'un sonuçları ayrı ayrı gelir.

### Her pod'da ne olur

```
10 sn'de bir ──►  POST {MonitorUrl}/api/agent/v1/report   (başlık X-ConnectivityProbe-AppKey)
                   "Ben X pod'uyum, yaşıyorum"  ◄── bağlantı listesi + test aralığı
30 sn'de bir ──►  her bağlantıyı bu pod'un içinden test et (TCP; hedef ConnectivityProbe kullanıyorsa pod keşfi)
                   └─► sonuçlar hemen bir sonraki bildirimle gider
kapanırken   ──►  POST {MonitorUrl}/api/agent/v1/goodbye   → pod alarm vermeden listeden çıkar
```

- Testler `discover` ucuyla birebir aynı mantıkla yapılır; sonuçlar iki modda aynı anlamı taşır.
- Monitor'deki **"Şimdi test et"**, her pod'a bir sonraki bildiriminde (varsayılan en geç 10 sn) ulaşır; pod'lar hemen test
  eder.
- Pod Monitor'e ulaşamazsa (yanlış adres, firewall, yanlış anahtar) uygulama loguna uyarı yazılır (`ConnectivityProbe`
  kategorisi) ve bir sonraki bildirimde tekrar denenir. Uygulamanın kendisi hiçbir şekilde etkilenmez.
- Bir pod aynı anda en fazla 4 bağlantıyı test eder; hedeflere ani bağlantı yükü binmez.

### Strict modda pod durumları

| Durum | Anlamı |
|---|---|
| Çalışıyor | Pod son 2 × bildirim aralığı içinde (yaklaşık 25 sn) bildirim gönderdi. |
| Bildirim gecikti | Bildirim gecikti ama eşik henüz dolmadı. Alarm değil. |
| Eksik | `MissingAfterCycles` (3) test aralığı boyunca bildirim yok ve pod sayısı azaldı. **Alarm.** Yalnızca *Pod listesini sıfırla* ile silinir. |
| (listeden çıktı) | Pod "kapanıyorum" dedi (deploy, scale-down) ya da sayı korunarak yerine yeni pod geldi. Alarm yok. |

### Ayarlar

| Ayar | Varsayılan | Anlamı |
|---|---|---|
| `MonitorUrl` | – | Monitor'ün adresi. Pod'lar bu adrese erişebilmelidir. |
| `AppKey` | – | Monitor'deki uygulama anahtarı. Pod'un hangi uygulamanın tanımlarını alacağını belirler. |
| `Strict:IntervalSeconds` | Monitor'ün aralığı (30) | Test aralığı (sn). |
| `Strict:CommandPollSeconds` | 10 | Pod'un Monitor'e bildirim sıklığı. "Şimdi test et" bu süre içinde pod'a ulaşır. |

### Uygulama anahtarı

- Uygulamayı tanımlar. Anahtarla bir pod yalnızca **o uygulamanın** bağlantı listesini okuyabilir ve onun adına sonuç
  gönderebilir; başka bir şey yapamaz.
- Monitor'de istediğiniz an yenileyebilirsiniz; eski anahtar hemen geçersiz olur.
- Her ortam için ayrı uygulama (ve anahtar) kullanın, örneğin "Orders Test" ve "Orders Prod".

---

## Discover mod ayrıntıları

1. Uygulamayı Monitor'e adresiyle kaydedin (pod'lara dağıtım yapan adres).
2. Uygulamada uçları açın: ya ortak bir `AccessKey` (Monitor'deki `Monitor:AccessKey` ile aynı değer) ya da iç servislerde
   `AllowAnonymous=true`.
3. Monitor her turda:
   - önce uygulamanın portuna TCP bağlantısı açar. Port kapalıysa nedenini yazar (timeout = firewall, reddedildi = servis
     kapalı, DNS);
   - `/connectivity-probe/identity`'yi yeni bağlantılarla çağırıp farklı `instanceId`'leri (pod'ları) sayar;
   - atanan her bağlantıyı uygulamaya `/connectivity-probe/discover` ile test ettirir. Yanıt testi hangi pod'un yaptığını
     söyler; sonuçlar pod başına gösterilir.

Pod sayımı, load balancer'ın **yeni bağlantıları** pod'lara dağıttığını varsayar (Kubernetes Service ve ingress'ler
varsayılan olarak böyledir). Session affinity açıksa her istek aynı pod'a düşer; bu durumda yanıtta `notes` uyarısı döner.
Böyle bir uygulamada Strict modu kullanın.

---

## Ayarlar

| Ayar | Varsayılan | Anlamı |
|---|---|---|
| `Enabled` | `true` | `false` ise uçlar kapanır (Strict mod bundan bağımsızdır). |
| `AccessKey` | boş | Uçlar için ortak erişim anahtarı. İstekler `X-ConnectivityProbe-Key` başlığında göndermelidir. |
| `AllowAnonymous` | `false` | Anahtarsız erişime izin verir. Yalnızca iç servislerde. |
| `AllowedTargets` | boş | İzin verilen hedefler: `sql01:1433`, `redis:*`, `*.svc.cluster.local:443`, `*.lan:*`. Boş = her hedef. |
| `MaxConcurrentDiscover` | 20 | Aynı anda işlenen discover isteği (0 = sınırsız); fazlası `429` alır. |
| `Path` | `/connectivity-probe` | Uçların taban yolu. |
| `DefaultTimeoutMs` / `MaxTimeoutMs` | 5000 / 30000 | `timeoutMs` varsayılanı ve üst sınırı. |
| `MaxAttempts` | 100 | Pod keşfinde en fazla identity isteği. |
| `MaxRequestDurationSeconds` | 60 | Tek isteğin süre sınırı; dolunca yanıt `truncated: true` olur. |
| `MaxAddresses` | 64 | Bir isim için test edilecek en fazla IP. |
| `EnableIdentity` | `true` | `identity` ucunu açar/kapatır. |
| `InstanceIdSeed` | boş | `instanceId`'ye eklenir (aynı makine adını paylaşan kopyalar için). |
| `IdentityEnvironmentVariables` | `POD_NAME, POD_NAMESPACE, POD_IP, NODE_NAME, CLUSTER_NAME, HOSTNAME, APP_POOL_ID, ASPNETCORE_ENVIRONMENT` | Identity yanıtına kopyalanan ortam değişkenleri. Bunların dışında hiçbir değişken okunmaz. |
| `Info:<ad>` | – | Identity yanıtına eklenen sabit değerler. |
| `ListenerPrefixes` | `http://+:8099/` | Yalnızca `ConnectivityProbeListener`. |
| `AutoRegister` | `true` | Yalnızca IIS: `false` ise modül kendini kaydetmez. |
| `MonitorUrl`, `AppKey`, `Strict:IntervalSeconds`, `Strict:CommandPollSeconds` | – | Strict mod (yukarıya bakın). |

Kodda `options.Log = (level, message) => ...` ile kütüphanenin log mesajlarını istediğiniz yere yönlendirebilirsiniz.
ASP.NET Core'da kendiliğinden uygulamanın `ILogger`'ına (`ConnectivityProbe` kategorisi) yazılır. IIS'te, `Log`
vermediyseniz Strict mod mesajları `System.Diagnostics.Trace`'e gider.

---

## Uçlar

Hepsi `GET`, JSON döner ve önbelleğe alınmaz. Hata yanıtları dahil her yanıtta `X-ConnectivityProbe: 1` başlığı bulunur.

### `GET /connectivity-probe/discover`

```
/connectivity-probe/discover?host=sql01:1433                                          → yalnızca TCP testi
/connectivity-probe/discover?host=https://orders.prod.svc&usesConnectivityProbe=true  → TCP + hedefin pod keşfi
```

| Parametre | Varsayılan | Anlamı |
|---|---|---|
| `host` | (zorunlu) | Sunucu adı, IP, `sunucu:port` veya URL. IPv6: `[::1]:80`. |
| `port` | `host`'taki port | 1–65535. Portsuz URL'de https için 443, http için 80. |
| `usesConnectivityProbe` | `false` | Hedef de ConnectivityProbe kullanıyorsa `true`: TCP testinden sonra hedefin pod'ları keşfedilir. |
| `timeoutMs` | 5000 | Tek bağlantının / isteğin zaman aşımı. |
| `attempts` | `MaxAttempts` | Pod keşfinde en fazla identity isteği. |
| `confidence` | 0.99 | Pod keşfi: hiçbir pod'un kaçırılmamış olma olasılığı (0.5–0.999). |
| `scheme` | URL'den, yoksa `http` | Pod keşfi: `http` veya `https`. |

`targetKind`: `tcp`, `unreachable`, `connectivityProbe` (hedefin pod'ları `instances[]` içinde), `connectivityProbeError`
(hedef ConnectivityProbe kullanıyor ama isteği reddetti), `other` (hedef ConnectivityProbe kullanmıyor).

### `GET /connectivity-probe/identity`

Bu instance'ın kimliğini döner: `instanceId` (12 karakter, pod başına sabit), `machineName`, `processId`, `startedAtUtc`,
`uptimeSeconds`, `localAddresses`, `os`, `framework`, `probeVersion`, `environment`, `info`, `request`.

### Hata kodları

| Kod | Ne zaman |
|---|---|
| `400` | Geçersiz `host`, port, `confidence` veya `scheme`. |
| `401` | `AccessKey` tanımlı ve `X-ConnectivityProbe-Key` yok veya yanlış. |
| `403` | Yapılandırılmamış (ne `AccessKey` ne `AllowAnonymous`) veya hedef `AllowedTargets` listesinde değil. |
| `429` | Aynı anda çok fazla discover isteği (`MaxConcurrentDiscover`). |

---

## Güvenlik

- **Varsayılan olarak kapalıdır:** `AccessKey` veya `AllowAnonymous=true` yoksa uçlar her isteğe `403` döner.
- `discover`, pod'un verilen adrese TCP bağlantısı açmasını sağlar. Dışarıdan erişilebilen bir serviste asla anahtarsız
  bırakmayın; ingress'te `/connectivity-probe` yolunu kapatın ve hedefleri `AllowedTargets` ile sınırlayın.
- `identity`; pod IP'lerini, makine adını, işletim sistemi / .NET sürümünü ve yalnızca `IdentityEnvironmentVariables`
  listesindeki ortam değişkenlerini döner.
- `AccessKey` ve `AppKey`'i secret veya ortam değişkeni olarak tutun.
- Strict mod yalnızca pod'dan Monitor'e giden HTTPS ister; dışarıdan gelen bir porta gerek yoktur.

---

## ConnectivityProbe Monitor

```bash
dotnet run --project src/ConnectivityProbe.Monitor
```

Varsayılan adres: http://localhost:5087/ . Monitor bir NuGet paketi değil, kendi başına çalışan bir uygulamadır.

**Neler sunar**

- **Birimler → Ekipler → Uygulamalar.** Her ekibin kendi bağlantı havuzu vardır; ortak havuz herkese açıktır. Ekip
  bağlantıları yalnızca o ekibin uygulamalarına atanabilir.
- **Monitör ekranı:** üstte büyük bir alanda en kritik uygulama, altında "Dikkat gerektirenler" satırı ve her ekip için yatay
  kayan bir satır. Her kartta durum, pod sayısı, bağlantı özeti ve **STRICT / DISCOVER** rozeti görünür. Ad, URL, ekip,
  birim veya moda göre arama yapılabilir ("strict" yazın).
- **Detay penceresi:** pod'lar (ad, IP'ler, son bildirim, kütüphane sürümü), bağlantı × pod matrisi (hangi hedef pod'a ne
  zamandır erişilemediği), geçmiş ve *Pod listesini sıfırla*.
- **Eksik pod'lar kendiliğinden asla silinmez.** Yalnızca *Pod listesini sıfırla* ile silinir. Geri dönen pod kendiliğinden
  normale döner.
- **Giriş:** `Monitor:AdminPassword` verilince arayüz ve yönetim API'si giriş ister. Şifre verilmezse arayüze yalnızca
  Monitor'ün çalıştığı makineden (localhost) erişilebilir. Strict uçları (`/api/agent/*`) girişle değil, uygulama
  anahtarıyla korunur.

**Ayarlar** (`appsettings.json` → `Monitor` veya ortam değişkenleri `Monitor__<Ad>`)

| Ayar | Varsayılan | Anlamı |
|---|---|---|
| `AdminUser` / `AdminPassword` | `admin` / boş | Arayüz girişi. Monitor'e başkaları erişebiliyorsa şifreyi mutlaka verin. |
| `AccessKey` | boş | Discover uygulamalarına gönderilen ortak anahtar (onların `ConnectivityProbe:AccessKey`'i ile aynı). |
| `IntervalSeconds` | 30 | Test aralığı (Strict pod'ların aralığı da budur). |
| `ProbeTimeoutMs` | 5000 | Tek bağlantının / isteğin zaman aşımı. |
| `MissingAfterCycles` | 3 | Bir pod'un "eksik" sayılması için cevapsız geçmesi gereken tur sayısı. |
| `MaxConcurrency` | 4 | Aynı anda kontrol edilen uygulama (Discover). |
| `MaxConcurrentConnections` | 4 | Bir uygulamanın aynı anda test edilen bağlantı sayısı (Discover). |
| `MaxInstanceAttempts` / `InstanceConfidence` | 60 / 0.95 | Pod sayımı (Discover). |
| `MaxProbeCallsPerConnection` | 40 | Bir bağlantıyı tüm pod'larda test etmek için en fazla çağrı (Discover). |
| `DataFile` | `data/definitions.json` | Tanımlar. Pod durumu (`pod-state.json`) ve giriş anahtarları (`keys/`) aynı klasörde tutulur. |

Monitor'ü HTTPS arkasında yayınlayın. Aynı makinede bir ters proxy çalışıyorsa her istek localhost'tan geliyor görünür;
bu durumda `AdminPassword`'ü mutlaka verin.

---

## Sürümler

| Sürüm | Öne çıkanlar |
|---|---|
| **1.1.0** | **Strict mod**: pod'lar tanımlarını uygulama anahtarıyla Monitor'den çeker, kendi içinden test eder ve sonucu gönderir. Kesin pod sayısı. Düzgün kapanışta "kapanıyorum" bildirimi. |
| 1.0.0 | İlk sürüm: `discover` ve `identity` uçları, ASP.NET Core ve IIS'te kodsuz devreye alma, `MaxConcurrentDiscover`, `AllowedTargets` joker karakterleri, `probeVersion`, loglama. |

Tam liste: [CHANGELOG](src/ConnectivityProbe/CHANGELOG.md) · Sürümler: [GitHub Releases](https://github.com/umutmemisoglu/ConnectivityProbe/releases) ·
Yeni sürüm yayınlama: [PUBLISHING.md](PUBLISHING.md)

## Lisans

[MIT](LICENSE) © 2026 Fatih Umut Memişoğlu
