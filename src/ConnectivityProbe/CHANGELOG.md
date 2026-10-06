# Değişiklik geçmişi

## 1.1.0

**Strict mod.** Uygulamanın her pod'u Monitor'e kendini bildirir; pod sayısı ve her pod'un sonucu kesindir.

- `ConnectivityProbe:MonitorUrl` ve `ConnectivityProbe:AppKey` verilince arka planda bir iş (agent) kendiliğinden başlar.
  ASP.NET Core'da uygulamayla birlikte, IIS'te uygulama başlarken başlar; diğer uygulamalarda `ConnectivityProbeAgent.Start()`.
- Agent her `Strict:CommandPollSeconds` (varsayılan 10 sn) Monitor'e bildirim gönderir ve uygulamanın bağlantı tanımlarını
  alır. Test zamanı gelince (varsayılan Monitor'ün aralığı, 30 sn; `Strict:IntervalSeconds` ile değiştirilebilir) veya
  Monitor'de "Şimdi test et"e basılınca her bağlantıyı pod'un içinden test edip sonuçları gönderir. Testler discover ucuyla
  aynı mantıkta çalışır.
- Uygulama düzgün kapanırken Monitor'e "kapanıyorum" bildirilir; deploy ve scale-down alarm üretmez.
- Yeni uç yoktur; mevcut `discover` / `identity` uçları ve Discover modu aynen çalışır.

## 1.0.0

İlk NuGet sürümü.

- `{Path}/discover`: uygulamanın kendi içinden hedefe telnet (TCP); `usesConnectivityProbe=true` ile hedefin pod keşfi.
- `{Path}/identity`: instance (pod) kimliği.
- Platformlar: ASP.NET Core 2.1 – 10 (hosting startup ile kodsuz veya `app.UseConnectivityProbe()`), IIS / klasik ASP.NET
  (kodsuz IHttpModule), OWIN, web sunucusu olmayan uygulamalar (`ConnectivityProbeListener`).
- Güvenli varsayılan: `AccessKey` veya `AllowAnonymous=true` verilmeden uçlar 403 döner.
- `MaxConcurrentDiscover` (varsayılan 20): aynı anda işlenen discover isteği sınırı; aşılırsa 429.
- `AllowedTargets` joker karakter destekler: `host:*`, `*.alan:port`, `*.alan:*`.
- Yanıtlarda `probeVersion`; discover yanıtında `notes` (ör. tüm yanıtlar tek pod'dan geldiyse session affinity uyarısı).
- ASP.NET Core'da reddedilen ve tamamlanan istekler uygulamanın `ILogger`'ına ("ConnectivityProbe" kategorisi) yazılır;
  diğer platformlarda `ConnectivityProbeOptions.Log` ile bağlanabilir.
- `hostNetwork: true` pod'larında kimlik çakışması giderildi: `POD_NAME` makine adından farklıysa kimliğe eklenir.
