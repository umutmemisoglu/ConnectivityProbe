# ConnectivityProbe

Bir uygulamanın **kendi içinden** (her pod / instance'ın içinden) başka sunuculara ve servislere erişebildiğini telnet benzeri
TCP bağlantısıyla test etmesini ve bir servisin arkasında **kaç instance (pod) çalıştığının** bulunmasını sağlar.

İki uç ekler:

| Uç | Ne işe yarar |
|---|---|
| `GET /connectivity-probe/discover` | Bu pod'un içinden hedefe TCP testi; hedef de ConnectivityProbe kullanıyorsa hedefin pod'larını keşfeder. |
| `GET /connectivity-probe/identity` | Bu instance'ın kimliği (pod başına sabit, pod'lar arasında farklı). Pod keşfi bununla yapılır. |

> *English summary:* adds `/connectivity-probe/discover` (TCP reachability check from inside each instance, plus instance
> discovery of targets that also use ConnectivityProbe) and `/connectivity-probe/identity`. Works on ASP.NET Core 2.1–10,
> IIS / classic ASP.NET, OWIN and non-web apps. Disabled until you set an access key or explicitly allow anonymous access.

## Kurulum

```bash
dotnet add package ConnectivityProbe
```

| Uygulama türü | Paketteki hedef | Devreye alma |
|---|---|---|
| **ASP.NET Core / .NET 8, 9, 10** | `net8.0` | `app.UseConnectivityProbe();` **veya** kodsuz: ortam değişkeni `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=ConnectivityProbe` |
| **ASP.NET Core 2.1 – 7** | `netstandard2.0` | Aynı |
| **IIS / klasik ASP.NET** (MVC 5, Web API 2, WebForms) — .NET Framework 4.6.2+ | `net462` | **Hiçbir şey.** DLL `bin`'e girince kendini kaydeder (Integrated pipeline). |
| **OWIN self-host** | `net462` / `netstandard2.0` | `app.Use(typeof(ConnectivityProbeOwinMiddleware));` |
| **Web sunucusu olmayan uygulama** (Windows Service, worker, console) | hepsi | `var probe = ConnectivityProbeListener.Start();` (kapanırken `probe?.Dispose()`) |

.NET Framework hedefinin **hiçbir NuGet bağımlılığı yoktur** (binding redirect gerekmez).

### ASP.NET Core (kodla)

```csharp
using ConnectivityProbe;

var app = builder.Build();

// HTTPS yönlendirmesinden ve uygulamanın yetkilendirmesinden önce ekleyin; uçlar kendi anahtarıyla korunur.
app.UseConnectivityProbe(options =>
{
    options.Info["app"] = "my-service";       // identity yanıtına eklenecek sabit bilgi (isteğe bağlı)
});

app.UseHttpsRedirection();
// ...
```

Ayarlar ayrıca yapılandırmadan okunur (`appsettings.json` → `"ConnectivityProbe"` bölümü, ortam değişkenleri
`ConnectivityProbe__<Ad>`); `UseConnectivityProbe` içindeki atamalar bunların üzerine yazar.

### Kubernetes

```yaml
env:
  - name: ConnectivityProbe__AccessKey               # tüm uygulamalarda ve Monitor'de aynı ortak anahtar
    valueFrom: { secretKeyRef: { name: connectivity-probe, key: access-key } }
  - name: POD_NAME                                   # pod adları ve hostNetwork pod'larının ayrılması için önerilir
    valueFrom: { fieldRef: { fieldPath: metadata.name } }
  - name: POD_NAMESPACE
    valueFrom: { fieldRef: { fieldPath: metadata.namespace } }
  - name: NODE_NAME
    valueFrom: { fieldRef: { fieldPath: spec.nodeName } }
```

Kodsuz kullanımda ayrıca `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=ConnectivityProbe` ekleyin (başka hosting startup'lar varsa
noktalı virgülle: `Diger.Assembly;ConnectivityProbe`).

### IIS / klasik ASP.NET — `web.config`

```xml
<appSettings>
  <add key="ConnectivityProbe:AccessKey" value="..." />
  <add key="ConnectivityProbe:Info:cluster" value="prod-iis-1" />
</appSettings>
```

## Güvenlik

**Varsayılan olarak kapalıdır:** ne `AccessKey` ne de `AllowAnonymous=true` verilmemişse uçlar her isteği `403` ile reddeder.

- **Önerilen:** `AccessKey`. İstekler `X-ConnectivityProbe-Key` başlığında anahtarı taşımalıdır (yoksa `401`). Anahtarı
  `appsettings.json`'a değil, secret / ortam değişkeni olarak verin.
- `AllowAnonymous=true`: yalnızca dışarıya açık olmayan iç servislerde. **discover ucu, pod'un içinden verilen adrese TCP
  bağlantısı açtırır**; dışarıdan erişilebilen bir serviste anahtarsız bırakmayın ve ingress'te `/connectivity-probe` yolunu
  dışarıya kapatın.
- `AllowedTargets`: bağlanılabilecek hedefleri kısıtlar (`sql01:1433`, `redis:*`, `*.svc.cluster.local:443`, `*.lan:*`).
- `MaxConcurrentDiscover` (varsayılan 20): aynı anda işlenen discover isteği sınırı; aşan istekler `429` alır.
- `identity` ucu pod IP'lerini, makine adını, işletim sistemi/.NET sürümünü ve yalnızca `IdentityEnvironmentVariables`
  listesindeki ortam değişkenlerini döner (gizli bilgi sızmasın diye diğerleri hiç okunmaz). İstemiyorsanız
  `EnableIdentity=false` (bu durumda bu uygulamanın pod'ları keşfedilemez).

## Uçlar

Hepsi `GET`, yanıt JSON, önbelleğe alınmaz. Taban yol varsayılan olarak `/connectivity-probe` (uygulama köküne göre).
Tüm yanıtlarda (hata yanıtları dahil) `X-ConnectivityProbe: 1` başlığı bulunur; böylece "anahtar yanlış" ile "burada
ConnectivityProbe yok" ayırt edilir.

### `GET /connectivity-probe/discover`

```
/connectivity-probe/discover?host=sql01:1433                                          → yalnızca TCP testi
/connectivity-probe/discover?host=https://orders.prod.svc&usesConnectivityProbe=true  → TCP + hedefin pod keşfi
```

1. **Her durumda önce TCP.** İsim çözülür; her IP ve ismin kendisi aynı anda denenir. Port kapalıysa hedefe **hiç HTTP isteği
   gönderilmez** (`targetKind: "unreachable"`), nedeni `tcp` raporunda yazar (timeout, reddedildi, DNS).
2. `usesConnectivityProbe` verilmediyse (DB, Redis, SMTP, dış API) burada biter: `targetKind: "tcp"`.
3. `usesConnectivityProbe=true` ise hedefin identity ucuna her seferinde **yeni bağlantıyla** istek atılır, tekrar eden
   kimlikler ayıklanarak hedefin pod'ları bulunur; yeni pod çıkmayı kesince durulur (adaptive).
4. Hedefte ConnectivityProbe yoksa veya istekler `401/403` ile reddediliyorsa ilk 3 istekten sonra bırakılır.

| Parametre | Varsayılan | Açıklama |
|---|---|---|
| `host` | (zorunlu) | Sunucu adı, IP, `sunucu:port` veya tam URL (`https://x.com/`). IPv6: `[::1]:80`. |
| `port` | host'taki port | 1–65535. URL'de port yoksa https 443, http 80. |
| `usesConnectivityProbe` | `false` | Hedef de ConnectivityProbe kullanıyorsa `true`: pod keşfi yapılır. |
| `timeoutMs` | 5000 | Tek bağlantının / isteğin zaman aşımı. Üst sınır `MaxTimeoutMs`. |
| `attempts` | `MaxAttempts` | Pod keşfinde en fazla identity isteği. |
| `confidence` | 0.99 | Pod keşfi: "görmediğim başka pod yok" olasılığı (0.5–0.999). |
| `scheme` | URL'nin şeması / `http` | Pod keşfi: `http` veya `https`. |

`targetKind`: `tcp`, `unreachable`, `connectivityProbe` (pod'lar `instances[]` içinde), `connectivityProbeError` (hedefte
ConnectivityProbe var ama reddetti; nedeni `errors` içinde), `other` (hedefte ConnectivityProbe yok).

Yanıtta ayrıca: testi yapan instance (`executedByInstanceId`, `executedByMachineName`), `probeVersion`, `tcp` raporu,
`distinctInstances`, `attemptsMade`, `succeeded`/`failed`/`errors`, `converged`, `confidence` (tahmin), `stoppedEarly`,
`truncated` ve `notes` (ör. tüm yanıtlar tek pod'dan geldiyse: *session affinity açık olabilir* uyarısı).

**Pod sayımının varsayımı:** load balancer her yeni bağlantıyı pod'lara dağıtmalıdır (Kubernetes Service ve ingress'ler
varsayılan olarak böyledir). Session affinity açıksa hep aynı pod görülür; bu durumda `notes` uyarısı döner.
`d` pod görülüp üst üste `k` istekte yeni pod çıkmadıysa gizli bir pod kalma ihtimali `(d/(d+1))^k`'dir (ör. 3 pod, %99 → 17 istek).

### `GET /connectivity-probe/identity`

`instanceId` (makine/pod adından türetilen 12 karakterlik sabit kimlik), `machineName`, `processId`, `startedAtUtc`,
`uptimeSeconds`, `localAddresses`, `os`, `framework`, `probeVersion`, `environment` (seçili ortam değişkenleri), `info`
(`Info:*` ayarları), `request` (`remoteIp`, `host`, `X-Forwarded-For/Host`).

### Hata kodları

| Kod | Ne zaman |
|---|---|
| `400` | `host` eksik/geçersiz, port belirlenemedi, `confidence` veya `scheme` geçersiz. |
| `401` | `AccessKey` tanımlı ve `X-ConnectivityProbe-Key` başlığı yok/yanlış. |
| `403` | Yapılandırılmamış (ne `AccessKey` ne `AllowAnonymous`) veya hedef `AllowedTargets` listesinde değil. |
| `429` | Aynı anda işlenen discover isteği `MaxConcurrentDiscover` sınırında; biraz sonra tekrar deneyin. |

Hata gövdesi her zaman `{"error": "..."}` biçimindedir.

## Ayarlar

`appsettings.json`'da `"ConnectivityProbe": { ... }`, ortam değişkeninde `ConnectivityProbe__<Ad>`, `web.config`/`app.config`
`appSettings`'te `ConnectivityProbe:<Ad>`. Ortam değişkeni `web.config`'teki aynı ayarı ezer.

| Ayar | Varsayılan | Amacı |
|---|---|---|
| `Enabled` | `true` | `false` ise uçlar tamamen kapanır. |
| `AccessKey` | boş | Paylaşılan erişim anahtarı (önerilir). |
| `AllowAnonymous` | `false` | Anahtarsız erişime açıkça izin verir. |
| `AllowedTargets` | boş | İzin verilen hedefler (virgülle veya dizi). Boşsa her hedef serbest. |
| `MaxConcurrentDiscover` | 20 | Aynı anda işlenen discover isteği (0 = sınırsız). |
| `Path` | `/connectivity-probe` | Uçların taban yolu. |
| `DefaultTimeoutMs` / `MaxTimeoutMs` | 5000 / 30000 | `timeoutMs` varsayılanı ve üst sınırı. |
| `MaxAttempts` | 100 | Pod keşfinde en fazla identity isteği. |
| `MaxRequestDurationSeconds` | 60 | Tek isteğin toplam süre sınırı; aşılırsa `truncated: true`. |
| `MaxAddresses` | 64 | Bir isim için test edilecek en fazla IP. |
| `EnableIdentity` | `true` | `identity` ucunu açar/kapatır. |
| `InstanceIdSeed` | boş | `instanceId`'ye eklenir (aynı makine adını paylaşan kopyalar için). |
| `IdentityEnvironmentVariables` | `POD_NAME, POD_NAMESPACE, POD_IP, NODE_NAME, CLUSTER_NAME, HOSTNAME, APP_POOL_ID, ASPNETCORE_ENVIRONMENT` | `identity` yanıtına kopyalanan ortam değişkenleri. |
| `Info:<ad>` | – | `identity` yanıtına eklenen sabit bilgiler. |
| `ListenerPrefixes` | `http://+:8099/` | Yalnızca `ConnectivityProbeListener`: dinlenecek adresler. |
| `AutoRegister` | `true` | Yalnızca IIS: `false` ise modül kendini kaydetmez. |

Kodda ayrıca `options.Log = (level, message) => ...` ile log çıkışı verilebilir. ASP.NET Core'da verilmezse uygulamanın
`ILogger`'ına `ConnectivityProbe` kategorisiyle yazılır: reddedilen istekler `Warning`, tamamlanan discover istekleri `Information`.

## Platform notları

- **IIS Classic pipeline:** `web.config` → `<system.web><httpModules><add name="ConnectivityProbe" type="ConnectivityProbe.ConnectivityProbeModule, ConnectivityProbe" /></httpModules></system.web>` ve `ConnectivityProbe:AutoRegister=false`.
- **ConnectivityProbeListener (Windows):** `http://+:8099/` yönetici yetkisi ister; `netsh http add urlacl url=http://+:8099/ user="NT AUTHORITY\NETWORK SERVICE"` veya `ListenerPrefixes=http://localhost:8099/`.
- **.NET Framework 4.6.2 – 4.7 ve HTTPS hedefler:** TLS 1.2 açık olmalı (`httpRuntime targetFramework="4.7"+` veya `ServicePointManager.SecurityProtocol`).
- Self-signed sertifikalı HTTPS hedeflerde pod keşfi desteklenmez (TCP testi çalışır).

## Lisans

MIT. Copyright (c) 2026 Fatih Umut Memişoğlu.
