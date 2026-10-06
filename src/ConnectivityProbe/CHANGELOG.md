# Değişiklik geçmişi

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
