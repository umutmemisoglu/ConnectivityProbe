# Changelog / Değişiklik geçmişi

## 2.0.0

### English

**One line, no endpoints.** Breaking release: every application now works the way 1.1's Strict mode did.

- New single entry point: `ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)`. Optional settings with a fourth
  parameter (`PollSeconds`, `IntervalSeconds`, `TimeoutMs`, `MaxAddresses`, `MaxParallelTests`).
- **Automatic registration:** the developer chooses the app key (e.g. `orders-api`). The first pod that reports with a
  new key registers the application in the Monitor; pods with the same key are the same application. The same key can be
  used in every environment, each with its own Monitor.
- **Application version per pod:** the greater of `AssemblyInformationalVersion` and `AssemblyVersion` of the calling
  assembly, plus a build id (MVID) and build date. No manual setting.
- **Automatic cluster grouping:** fingerprint of the Kubernetes service-account CA certificate (and the namespace);
  outside Kubernetes, the Monitor groups pods by source address.
- **Never affects the application:** no exceptions; short English console messages on registration, on connection
  errors (first, then at most every 5 minutes) and on reconnect.
- Stops by itself on process exit / IIS app-domain unload and tells the Monitor the pod is leaving.
- Targets `netstandard2.0` and `net462` with **no NuGet dependencies** (own JSON handling).
- Agent protocol `v2` (`/api/agent/v2/report`, `/api/agent/v2/goodbye`); requires ConnectivityProbe Monitor 2.0.
- **Removed:** `discover` / `identity` endpoints, `UseConnectivityProbe()`, hosting startup, IIS module, OWIN middleware,
  `ConnectivityProbeListener`, Discover mode, `AccessKey`, `AllowAnonymous`, `AllowedTargets`, the `net8.0` target.

### Türkçe

**Tek satır, uç yok.** Kıran sürüm: artık her uygulama 1.1'in Strict modu gibi çalışır.

- Tek giriş noktası: `ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)`. İsteğe bağlı ayarlar dördüncü
  parametreyle (`PollSeconds`, `IntervalSeconds`, `TimeoutMs`, `MaxAddresses`, `MaxParallelTests`).
- **Kendiliğinden kayıt:** uygulama anahtarını geliştirici belirler (ör. `orders-api`). Yeni bir anahtarla bildirim
  gönderen ilk pod uygulamayı Monitor'e kaydeder; aynı anahtarlı pod'lar aynı uygulamadır. Aynı anahtar her ortamda, her
  biri kendi Monitor'üyle kullanılabilir.
- **Pod başına uygulama sürümü:** çağıran assembly'nin `AssemblyInformationalVersion` ve `AssemblyVersion` değerlerinden
  büyük olanı; ayrıca build kimliği (MVID) ve build tarihi. Elle ayar gerekmez.
- **Otomatik cluster gruplama:** Kubernetes service account CA sertifikasının parmak izi (ve namespace); Kubernetes
  dışında Monitor pod'ları kaynak adrese göre gruplar.
- **Uygulamayı asla etkilemez:** hata fırlatmaz; kayıtta, bağlantı hatalarında (ilk seferde, sonra en fazla 5 dakikada
  bir) ve yeniden bağlanınca konsola kısa İngilizce mesajlar yazar.
- Süreç kapanırken / IIS app domain'i kaldırılırken kendiliğinden durur ve Monitor'e pod'un ayrıldığını bildirir.
- Hedefler `netstandard2.0` ve `net462`, **hiçbir NuGet bağımlılığı yok** (kendi JSON işleme kodu).
- Agent protokolü `v2` (`/api/agent/v2/report`, `/api/agent/v2/goodbye`); ConnectivityProbe Monitor 2.0 gerekir.
- **Kaldırılanlar:** `discover` / `identity` uçları, `UseConnectivityProbe()`, hosting startup, IIS modülü, OWIN
  middleware, `ConnectivityProbeListener`, Discover modu, `AccessKey`, `AllowAnonymous`, `AllowedTargets`, `net8.0` hedefi.

## 1.1.0

### English

**Strict mode.** Every pod of the application reports to ConnectivityProbe Monitor by itself, so the pod count and every
pod's result are exact.

- New settings `ConnectivityProbe:MonitorUrl` and `ConnectivityProbe:AppKey`. When both are set, a background job starts
  automatically: with the application on ASP.NET Core, and when the application starts on IIS. Other applications call
  `ConnectivityProbeAgent.Start()` / `Stop()`.
- Every `Strict:CommandPollSeconds` (default 10 s) the pod reports to the Monitor and receives the application's
  connection list. When the test interval is due (default: the Monitor's interval, 30 s, or `Strict:IntervalSeconds`), or
  when "Test now" is pressed in the Monitor, the pod tests every connection from inside and sends the results. The tests
  use the same logic as the `discover` endpoint.
- On a clean shutdown the pod tells the Monitor it is leaving, so deploys and scale-downs raise no alarm.
- Works for applications without HTTP (workers, queue consumers).
- No new endpoint. The `discover` / `identity` endpoints and Discover mode work exactly as before.

### Türkçe

**Strict mod.** Uygulamanın her pod'u ConnectivityProbe Monitor'e kendini bildirir; pod sayısı ve her pod'un sonucu kesindir.

- Yeni ayarlar: `ConnectivityProbe:MonitorUrl` ve `ConnectivityProbe:AppKey`. İkisi verilince arka planda bir iş
  kendiliğinden başlar: ASP.NET Core'da uygulamayla birlikte, IIS'te uygulama başlarken. Diğer uygulamalarda
  `ConnectivityProbeAgent.Start()` / `Stop()` çağrılır.
- Pod her `Strict:CommandPollSeconds`'ta (varsayılan 10 sn) Monitor'e bildirim gönderir ve uygulamanın bağlantı listesini
  alır. Test zamanı gelince (varsayılan Monitor'ün aralığı, 30 sn; veya `Strict:IntervalSeconds`) ya da Monitor'de
  "Şimdi test et"e basılınca her bağlantıyı kendi içinden test edip sonuçları gönderir. Testler `discover` ucuyla aynı
  mantıkla çalışır.
- Düzgün kapanışta pod Monitor'e ayrıldığını bildirir; deploy ve scale-down alarm üretmez.
- HTTP'si olmayan uygulamalarda da çalışır (worker'lar, kuyruk tüketicileri).
- Yeni uç yoktur. `discover` / `identity` uçları ve Discover modu aynen çalışır.

## 1.0.0

### English

First NuGet release.

- `{Path}/discover`: TCP test of a target from inside this instance; with `usesConnectivityProbe=true`, pod discovery of
  the target.
- `{Path}/identity`: identity of this instance (pod).
- Platforms: ASP.NET Core 2.1 – 10 (zero-code hosting startup or `app.UseConnectivityProbe()`), classic ASP.NET on IIS
  (zero-code IHttpModule), OWIN, applications without a web server (`ConnectivityProbeListener`).
- Secure by default: without `AccessKey` or `AllowAnonymous=true` the endpoints return 403.
- `MaxConcurrentDiscover` (default 20): limit of discover requests processed at the same time; more get 429.
- `AllowedTargets` supports wildcards: `host:*`, `*.domain:port`, `*.domain:*`.
- `probeVersion` in responses; `notes` in discover responses (e.g. a session affinity warning when every response came
  from a single pod).
- On ASP.NET Core, rejected and completed requests are written to the application's `ILogger` (category
  `ConnectivityProbe`); elsewhere use `ConnectivityProbeOptions.Log`.
- Unique ids for `hostNetwork: true` pods: `POD_NAME` is added to the id when it differs from the machine name.

### Türkçe

İlk NuGet sürümü.

- `{Path}/discover`: uygulamanın kendi içinden hedefe TCP testi; `usesConnectivityProbe=true` ile hedefin pod keşfi.
- `{Path}/identity`: instance (pod) kimliği.
- Platformlar: ASP.NET Core 2.1 – 10 (hosting startup ile kodsuz veya `app.UseConnectivityProbe()`), IIS'te klasik ASP.NET
  (kodsuz IHttpModule), OWIN, web sunucusu olmayan uygulamalar (`ConnectivityProbeListener`).
- Güvenli varsayılan: `AccessKey` veya `AllowAnonymous=true` verilmeden uçlar 403 döner.
- `MaxConcurrentDiscover` (varsayılan 20): aynı anda işlenen discover isteği sınırı; aşılırsa 429.
- `AllowedTargets` joker karakter destekler: `host:*`, `*.alan:port`, `*.alan:*`.
- Yanıtlarda `probeVersion`; discover yanıtında `notes` (ör. tüm yanıtlar tek pod'dan geldiyse session affinity uyarısı).
- ASP.NET Core'da reddedilen ve tamamlanan istekler uygulamanın `ILogger`'ına ("ConnectivityProbe" kategorisi) yazılır;
  diğer platformlarda `ConnectivityProbeOptions.Log` ile bağlanabilir.
- `hostNetwork: true` pod'larında kimlik çakışması giderildi: `POD_NAME` makine adından farklıysa kimliğe eklenir.
