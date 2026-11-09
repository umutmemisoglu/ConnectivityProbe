# Changelog / Değişiklik geçmişi

## 2.3.0

### English

**Health endpoint on its own port and connection groups.**

- Every application with ConnectivityProbe opens a health endpoint on a separate port:
  `http://<pod-ip>:29470/connectivityprobe/health` (`HealthEnabled`, `HealthPort`). A tiny raw-TCP listener, so it works in
  ASP.NET Core, classic ASP.NET / IIS, Worker Service, console and Windows services on every supported .NET and never
  collides with the application's own `/health`. The port is opened exclusively; if busy, the next 9 ports, then a free port.
  The port in use is reported (`PodIdentity.HealthPort`).
- The answer carries a ConnectivityProbe signature (`X-ConnectivityProbe`) with status (Healthy / Degraded), application,
  version and pod; the application key is not included.
- `HealthProbe.CheckAsync` tells for sure whether a service uses ConnectivityProbe (probe / refused / other / timeout / dns).
  The Monitor uses it to check pod health and the services an application uses; pods can also run it per connection when
  the Monitor asks (`AgentResult.Health`).
- Connection groups: `o.AddGroup("Payment services", "pay", "billing")` (`AgentReport.Groups`).

### Türkçe

**Ayrı portta health ucu ve bağlantı grupları.**

- ConnectivityProbe kullanan her uygulama ayrı bir portta health ucu açar: `http://<pod-ip>:29470/connectivityprobe/health`
  (`HealthEnabled`, `HealthPort`). Ham TCP üzerinde küçük bir dinleyici: ASP.NET Core, klasik ASP.NET / IIS, Worker Service,
  konsol ve Windows servislerinde, desteklenen tüm .NET sürümlerinde çalışır; uygulamanın kendi `/health` ucuyla çakışmaz.
  Port paylaşılmadan açılır; doluysa sıradaki 9 port, o da olmazsa boş bir port. Kullanılan port bildirilir
  (`PodIdentity.HealthPort`).
- Cevapta ConnectivityProbe imzası (`X-ConnectivityProbe`), durum (Healthy / Degraded), uygulama, sürüm ve pod vardır;
  uygulama anahtarı yoktur.
- `HealthProbe.CheckAsync` bir servisin ConnectivityProbe kullanıp kullanmadığını kesin söyler (probe / refused / other /
  timeout / dns). Monitor bunu pod sağlığını ve uygulamaların kullandığı servisleri sorgulamak için kullanır; Monitor isterse
  pod'lar da bağlantı başına çalıştırabilir (`AgentResult.Health`).
- Bağlantı grupları: `o.AddGroup("Ödeme servisleri", "pay", "billing")` (`AgentReport.Groups`).

## 2.2.0

### English

**Dependencies from the application's settings.**

- New options `SettingsUrl` (GET, returns JSON), `SettingsJson` and `Settings` (an object the application already loaded)
  and `SettingsRefreshMinutes` (10). The pod finds the databases, queues and services in the settings and reports them
  with every report (`AgentReport.Settings`): **only the setting's name, type, host, port and TLS**; passwords, user names,
  keys and the settings address never leave the pod.
- Recognized formats: PostgreSQL / SQL Server / MySQL / Oracle connection strings, StackExchange.Redis strings, URLs
  (http(s), mongodb, redis(s), amqp(s), postgres(ql), mysql, kafka, ldap(s), smtp(s), nats...), `host:port` lists and
  host + port fields. The type comes from the scheme, the connection string, the port, the setting's name and the server
  name. Unambiguous results are "high" confidence; values that only look like an address are "low" (suggestions).
  `SettingsDiscovery.Discover(json)` is public.
- Optional verification (switched on per application in the Monitor): the pod connects to each address and sends that
  protocol's harmless greeting (AMQP, Kafka ApiVersions, MongoDB hello, Redis PING, PostgreSQL SSLRequest, SQL Server
  PRELOGIN, HTTP GET → Kibana / Elasticsearch / RabbitMQ management / Consul; banners for MySQL, SMTP, SSH, FTP) and takes
  the type from the answer (`EndpointVerifier`). No credentials are sent. Results are cached (6 h, failures 30 min).
- A failed settings read keeps the previous result and reports the error. Reading runs in the background and never delays
  the reports. Still no dependencies (netstandard2.0 + net462).
- Monitor: found dependencies are added to the connection pool (reusing an existing connection to the same address) and
  attached automatically; changed / removed settings are applied, with a change log, suggestions, ignore / add again, and
  the newest pod wins during a deploy. Monitor can also store its data in MongoDB.

### Türkçe

**Uygulamanın ayarlarındaki bağımlılıklar.**

- Yeni seçenekler: `SettingsUrl` (GET, JSON döner), `SettingsJson`, `Settings` (uygulamanın zaten okuduğu nesne) ve
  `SettingsRefreshMinutes` (10). Pod ayarlardaki veritabanlarını, kuyrukları ve servisleri bulur ve her bildirimde gönderir
  (`AgentReport.Settings`): **yalnızca ayarın adı, tür, host, port ve TLS**; şifre, kullanıcı adı, anahtar ve ayar adresi
  pod'dan hiç çıkmaz.
- Tanınan biçimler: PostgreSQL / SQL Server / MySQL / Oracle bağlantı metinleri, StackExchange.Redis metinleri, adresler
  (http(s), mongodb, redis(s), amqp(s), postgres(ql), mysql, kafka, ldap(s), smtp(s), nats...), `host:port` listeleri ve
  host + port alanları. Tür; şemadan, bağlantı metninden, porttan, ayarın adından ve sunucu adından anlaşılır. Kesin
  sonuçlar "high", yalnızca adrese benzeyenler "low" (öneri) güvenlidir. `SettingsDiscovery.Discover(json)` herkese açıktır.
- İsteğe bağlı doğrulama (Monitor'de uygulama bazında açılır): pod her adrese bağlanıp o protokolün zararsız karşılamasını
  gönderir (AMQP, Kafka ApiVersions, MongoDB hello, Redis PING, PostgreSQL SSLRequest, SQL Server PRELOGIN, HTTP GET →
  Kibana / Elasticsearch / RabbitMQ yönetim / Consul; MySQL, SMTP, SSH, FTP karşılamaları) ve türü cevaptan alır
  (`EndpointVerifier`). Kimlik bilgisi gönderilmez. Sonuçlar saklanır (6 saat, başarısızlar 30 dk).
- Ayarlar okunamazsa önceki sonuç korunur ve hata bildirilir. Okuma arka planda yapılır, bildirimleri geciktirmez. Hâlâ
  bağımlılık yok (netstandard2.0 + net462).
- Monitor: bulunan bağımlılıklar bağlantı havuzuna eklenir (aynı adrese giden mevcut bağlantı kullanılır) ve uygulamaya
  kendiliğinden atanır; değişen / kaldırılan ayarlar uygulanır; değişiklik geçmişi, öneriler, yok say / tekrar ekle; deploy
  sırasında en yeni pod geçerlidir. Monitor verilerini MongoDB'de de tutabilir.

## 2.1.0

### English

**Pod resources, TLS certificates, DNS / latency, and clusters named after the pod network.**

- **Resources:** every report now carries the pod's resource usage (`AgentReport.Resources`): CPU (cores), memory
  (working set, GC heap, GC counts), threads and thread pool, handles; in a Linux container also the cgroup **CPU and
  memory limits, CPU throttling, OOM kills**, network bytes (`/proc/net/dev`) and TCP sockets (established, TIME_WAIT,
  local port range). Values that cannot be read on a platform stay empty; nothing ever throws.
- **TLS / certificate check:** for connections the Monitor marks as TLS (automatic for `https://` and ports 443, 8443,
  636, 993, 995, 465, 5671), the pod performs a TLS handshake after the TCP test and reports protocol, subject, issuer,
  expiry date and certificate errors (`AgentResult.Tls`).
- **DNS time** is measured separately (`ProbeReport.DnsMs`).
- Every pod now reports its own IP address (`PodIdentity.PrimaryAddress`): `POD_IP` (Kubernetes Downward API) if set,
  otherwise the local address used to reach the Monitor, otherwise the first IPv4 address of the machine name.
- ConnectivityProbe Monitor groups pods by that address's network (IPv4 `/16`, IPv6 `/64`) and names the group after it,
  e.g. `10.42.0.0/16`, instead of "Cluster 1". In Kubernetes the cluster CA fingerprint still separates two clusters that
  use the same pod network.
- Monitor UI: the separate Versions tab is gone. The application's details window shows a versions × networks table, the
  connection matrix with pods grouped by network, and the pods grouped by network (renamable). The whole UI is available
  in Turkish and English. Clicking an application opens its details in a new browser tab (`/?app=<id>`).
- Monitor: new **Resources** table (CPU, memory with limit, throttling, threads, TCP sockets, network rate, restarts, with
  ~10-minute charts); TLS result and days to certificate expiry in the connection matrix; slow connections (3× slower than
  usual) and changed DNS addresses are marked. Pod restarts (same pod, new process) are detected.
- Monitor alerts (`Monitor:Alerts`): memory ≥ 90 % of the limit, restart or OOM kill in the last 60 minutes, TCP sockets ≥
  70 % of the local port range, certificate expiring within 14 days → application "degraded"; CPU throttling ≥ 25 %, slow
  connections and changed IPs are shown as notes.
- Monitor definitions: one connection pool, independent of units and teams (team pools are merged into it; every
  connection can be attached to every application), ranked search in the pool, usage count and status per connection,
  applications moved between teams by drag and drop.
- Monitor: **Teams notifications**. Each application selects which situations notify (15 rules in 5 categories, sensible
  defaults on registration); people subscribe with one click ("Notify me") after a one-time Teams Workflows setup.
  Notifications are confirmed, sent once per incident, grouped per application, protected against flapping and quiet
  during deploys.
- Monitor: optional **Microsoft (Entra ID) sign-in** and a **central Teams workflow**: "Notify me" signs the person in and
  subscribes them automatically (no client secret, only basic sign-in). Without sign-in, people type their e-mail once.
  Setup: docs/teams-setup.md.
- No breaking API change; 2.0 pods keep working (without resource and TLS data).

### Türkçe

**Pod kaynakları, TLS sertifikaları, DNS / gecikme ve pod ağıyla adlandırılan cluster'lar.**

- **Kaynaklar:** her bildirim artık pod'un kaynak kullanımını taşır (`AgentReport.Resources`): CPU (çekirdek), bellek
  (working set, GC heap, GC sayıları), thread'ler ve thread pool, handle'lar; Linux container'da ayrıca cgroup **CPU ve
  bellek limitleri, CPU throttling, OOM kill**, ağ trafiği (`/proc/net/dev`) ve TCP soketleri (kurulu, TIME_WAIT, yerel port
  aralığı). Bir platformda okunamayan değerler boş kalır; hiçbir koşulda hata fırlatılmaz.
- **TLS / sertifika kontrolü:** Monitor'ün TLS olarak işaretlediği bağlantılarda (`https://` ve 443, 8443, 636, 993, 995,
  465, 5671 portlarında otomatik) pod TCP testinden sonra TLS el sıkışması yapar; protokol, sertifika sahibi, veren, bitiş
  tarihi ve sertifika hatalarını bildirir (`AgentResult.Tls`).
- **DNS süresi** ayrıca ölçülür (`ProbeReport.DnsMs`).
- Her pod artık kendi IP adresini bildirir (`PodIdentity.PrimaryAddress`): `POD_IP` (Kubernetes Downward API) verilmişse
  o, yoksa Monitor'e giderken kullanılan yerel adres, o da yoksa makine adının ilk IPv4 adresi.
- ConnectivityProbe Monitor pod'ları bu adresin ağına göre (IPv4 `/16`, IPv6 `/64`) gruplar ve grubu "Cluster 1" yerine
  ağın adıyla gösterir, ör. `10.42.0.0/16`. Kubernetes'te aynı pod ağını kullanan iki cluster yine CA parmak iziyle ayrılır.
- Monitor arayüzü: ayrı Sürümler sekmesi kalktı. Uygulamanın detay penceresinde sürümler × ağlar tablosu, pod'ların ağa göre
  gruplandığı bağlantı matrisi ve ağa göre gruplanmış pod'lar (ad verilebilir) bulunur. Arayüzün tamamı Türkçe ve
  İngilizce kullanılabilir. Uygulamaya tıklayınca ayrıntıları yeni tarayıcı sekmesinde açılır (`/?app=<kimlik>`).
- Monitor: yeni **Kaynaklar** tablosu (CPU, limitle birlikte bellek, throttling, thread'ler, TCP soketleri, ağ hızı,
  yeniden başlamalar; ~10 dakikalık grafiklerle); bağlantı matrisinde TLS sonucu ve sertifikanın bitişine kalan gün;
  yavaşlayan bağlantılar (olağanın 3 katı) ve değişen DNS adresleri işaretlenir. Pod'un yeniden başlaması (aynı pod, yeni
  süreç) tespit edilir.
- Monitor uyarıları (`Monitor:Alerts`): bellek limitin %90'ı, son 60 dakikada yeniden başlama veya OOM kill, TCP soketleri
  yerel port aralığının %70'i, sertifika bitişine 14 gün → uygulama "sorunlu"; CPU throttling %25, yavaş bağlantılar ve
  değişen IP'ler not olarak gösterilir.
- Monitor tanımları: birim ve ekiplerden bağımsız tek bağlantı havuzu (ekip havuzları buna katılır; her bağlantı her
  uygulamaya atanabilir), havuzda alaka sıralı arama, bağlantı başına kullanım sayısı ve durum, uygulamaların ekipler arasında
  sürükle-bırak ile taşınması.
- Monitor: **Teams bildirimleri**. Her uygulama hangi durumlarda bildirim gönderileceğini seçer (5 kategoride 15 kural,
  kayıtta makul varsayılanlar); kişiler bir kerelik Teams Workflows kurulumundan sonra tek tıkla abone olur ("Bana haber
  ver"). Bildirimler doğrulandıktan sonra, olay başına bir kez, uygulama başına tek mesajda gönderilir; gidip gelmeye karşı
  korumalıdır ve deploy sırasında susar.
- Monitor: isteğe bağlı **Microsoft (Entra ID) ile giriş** ve **merkezi Teams iş akışı**: "Bana haber ver" kişiyi Microsoft
  hesabıyla girdirip otomatik abone eder (client secret yok, yalnızca temel oturum açma). Giriş yoksa kişi bir kez
  e-postasını yazar. Kurulum: docs/teams-kurulum.md.
- Kıran API değişikliği yok; 2.0 pod'ları çalışmaya devam eder (kaynak ve TLS bilgisi olmadan).

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
