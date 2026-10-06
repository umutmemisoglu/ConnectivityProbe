using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>Kütüphanenin sunduğu iki endpoint türü.</summary>
    internal enum ProbeEndpointKind
    {
        /// <summary>{Path}/identity: bu instance'ın kimliği.</summary>
        Identity,
        /// <summary>{Path}/discover: hedefe telnet; hedef de ConnectivityProbe kullanıyorsa ayrıca hedefin pod keşfi.</summary>
        Discover
    }

    /// <summary>
    /// Web altyapısından bağımsız istek: her adaptör (ASP.NET Core, IIS, OWIN, dinleyici) kendi isteğini buna çevirir.
    /// </summary>
    internal sealed class ProbeRequest
    {
        public Func<string, string?> Query { get; set; } = _ => null;
        public Func<string, string?> Header { get; set; } = _ => null;
        public string? RemoteIp { get; set; }
        public string? Scheme { get; set; }
        public string? Host { get; set; }
        public CancellationToken Aborted { get; set; }
    }

    /// <summary>Adaptörün istemciye yazacağı yanıt (her zaman JSON, önbelleğe alınmaz).</summary>
    internal sealed class ProbeResponse
    {
        public const string ContentType = "application/json; charset=utf-8";
        public const string CacheControl = "no-store";
        /// <summary>Her yanıta eklenen işaret başlığının değeri (bkz. ConnectivityProbeOptions.MarkerHeader).</summary>
        public const string MarkerValue = "1";

        public int StatusCode { get; set; }
        public string Json { get; set; } = "";
        public byte[] Body => Encoding.UTF8.GetBytes(Json);
    }

    /// <summary>
    /// İki endpoint'in asıl işini yapar. Bütün adaptörler aynı motoru çağırdığı için davranış (yanıtlar, hata mesajları,
    /// sınırlar, güvenlik) her platformda birebir aynıdır.
    /// </summary>
    internal sealed class ProbeEngine
    {
        private readonly ConnectivityProbeOptions _options;
        private readonly string _identityPath;
        private readonly string _discoverPath;
        /// <summary>Aynı anda işlenen discover isteklerini sınırlar (MaxConcurrentDiscover); null: sınırsız.</summary>
        private readonly SemaphoreSlim? _discoverGate;

        public ProbeEngine(ConnectivityProbeOptions options)
        {
            _options = options;
            var basePath = "/" + options.Path.Trim().Trim('/');
            _identityPath = basePath + "/identity";
            _discoverPath = basePath + "/discover";
            if (options.MaxConcurrentDiscover > 0) _discoverGate = new SemaphoreSlim(options.MaxConcurrentDiscover, options.MaxConcurrentDiscover);
        }

        public ConnectivityProbeOptions Options => _options;

        /// <summary>
        /// İstek yolu bizim uçlarımızdan biri mi? (Uygulama köküne göre yol; büyük/küçük harf ve sondaki "/" fark etmez.)
        /// Yalnızca GET kabul edilir; diğer yöntemler ve yollar uygulamanın kendisine bırakılır.
        /// </summary>
        public bool TryMatch(string? path, string? method, out ProbeEndpointKind kind)
        {
            kind = ProbeEndpointKind.Identity;
            if (!_options.Enabled || !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return false;

            var p = (path ?? "").TrimEnd('/');
            if (_options.EnableIdentity && p.Equals(_identityPath, StringComparison.OrdinalIgnoreCase)) { kind = ProbeEndpointKind.Identity; return true; }
            if (p.Equals(_discoverPath, StringComparison.OrdinalIgnoreCase)) { kind = ProbeEndpointKind.Discover; return true; }
            return false;
        }

        public async Task<ProbeResponse> HandleAsync(ProbeRequest request, ProbeEndpointKind kind)
        {
            // step 1: Güvenli varsayılan. Paket referans verilince kendiliğinden devreye girdiği için, ne anahtar ne de açık
            //         "anonim izin" verilmemişse hiçbir şey yapmıyoruz; yanlışlıkla açık kalmış bir ağ tarama ucu olmasın.
            if (string.IsNullOrEmpty(_options.AccessKey) && !_options.AllowAnonymous)
            {
                return Rejected(request, 403, "ConnectivityProbe is not configured: set " + ConnectivityProbeOptions.SectionName
                                  + ":AccessKey (recommended) or " + ConnectivityProbeOptions.SectionName + ":AllowAnonymous=true");
            }

            // step 2: Erişim anahtarı tanımlıysa istekte doğru anahtar olmalı.
            if (!KeyMatches(_options.AccessKey, request.Header(ConnectivityProbeOptions.AccessKeyHeader)))
                return Rejected(request, 401, "missing or invalid " + ConnectivityProbeOptions.AccessKeyHeader + " header");

            // step 3: /identity ise parametre yok; bu instance'ın kimliğini üretip dönüyoruz.
            if (kind == ProbeEndpointKind.Identity)
                return Ok(InstanceIdentityBuilder.Build(request, _options));

            // ---------------------------------------------------------------- /discover

            // step 3b: Eşzamanlılık sınırı. Her discover isteği hedefe çok sayıda bağlantı açabildiği için aynı anda işlenen
            //          istek sayısını sınırlıyoruz; dolduysa beklemeden 429 dönüyoruz (istemci biraz sonra tekrar dener).
            if (_discoverGate != null && !_discoverGate.Wait(0))
                return Rejected(request, 429, "too many concurrent discover requests (limit " + _options.MaxConcurrentDiscover + "), retry later");

            try
            {
                return await DiscoverAsync(request).ConfigureAwait(false);
            }
            finally
            {
                _discoverGate?.Release();
            }
        }

        private async Task<ProbeResponse> DiscoverAsync(ProbeRequest request)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // step 4: Hedefi okuyoruz.
            //
            // host      (zorunlu) Test edilecek sunucu/servis adı, IP veya tam URL. Kabul edilen biçimler:
            //             sql01 | sql01:1433 | 10.0.0.5 | ::1 | [::1]:5078 | https://orders.example.com/ | https://x.com:8443/
            //           URL verilirse host, şema ve port URL'den ayrıştırılır (yol kısmı yok sayılır).
            // port      (host'ta yoksa zorunlu) Hedef TCP portu, 1-65535. Verilirse host içindeki portu ezer.
            if (!ProbeTarget.TryParse(request.Query("host"), request.Query("port"), out var host, out var port, out var urlScheme, out var targetError))
                return Error(400, targetError ?? "invalid target");
            // step 5: İzin listesi doluysa yalnızca listedeki hedeflere izin veriyoruz ("host:port", "host:*", "*.alan:port").
            if (!IsTargetAllowed(_options.AllowedTargets, host, port))
                return Rejected(request, 403, "target not allowed: " + host + ":" + port);

            // step 6: Parametreleri okuyup sunucu tarafı üst sınırlarla kırpıyoruz.
            //
            // usesConnectivityProbe  (true/1) Hedef de ConnectivityProbe kullanıyor mu? Değilse (DB, Redis, dış servis) yalnızca
            //                        telnet yapılır ve hedefe hiç HTTP isteği gönderilmez. Varsayılan: false.
            var usesText = request.Query("usesConnectivityProbe") ?? "";
            bool usesConnectivityProbe = usesText.Equals("true", StringComparison.OrdinalIgnoreCase) || usesText == "1";

            // timeoutMs  Tek bir bağlantının / isteğin (ve DNS'in) zaman aşımı (ms). Varsayılan: DefaultTimeout. Üst sınır: MaxTimeout.
            var timeout = _options.DefaultTimeout;
            if (TryGetPositive(request.Query("timeoutMs"), out int ms))
                timeout = Min(TimeSpan.FromMilliseconds(ms), _options.MaxTimeout);

            // attempts   Yalnızca pod keşfi için: hedefe en fazla kaç identity isteği atılacağı. Varsayılan: MaxAttempts
            //            (adaptive olduğu için genelde çok daha azında durur). Üst sınır: MaxAttempts.
            int attempts = _options.MaxAttempts;
            if (TryGetPositive(request.Query("attempts"), out int a)) attempts = Math.Min(a, _options.MaxAttempts);

            // confidence Yalnızca pod keşfi için: "başka pod yok" olasılığı hedefi (0.5-0.999, varsayılan 0.99).
            double confidence = 0.99;
            var confidenceText = request.Query("confidence") ?? "";
            if (confidenceText.Length > 0 &&
                (!double.TryParse(confidenceText, NumberStyles.Float, CultureInfo.InvariantCulture, out confidence)
                 || confidence < 0.5 || confidence > 0.999))
                return Error(400, "confidence must be between 0.5 and 0.999");

            // scheme     Yalnızca pod keşfi için: http veya https. Verilmezse host bir URL ise onun şeması, değilse http.
            var scheme = (request.Query("scheme") ?? "").ToLowerInvariant();
            if (scheme.Length == 0) scheme = urlScheme ?? "http";
            if (scheme != "http" && scheme != "https")
                return Error(400, "scheme must be http or https");

            // Toplam süre sınırı: dolunca kalan denemeler yapılmaz ve yanıt "truncated: true" olur.
            TimeSpan? maxDuration = _options.MaxRequestDuration > TimeSpan.Zero ? _options.MaxRequestDuration : (TimeSpan?)null;

            // step 7: HER DURUMDA önce telnet (TCP). İsim çözülür; her IP ve ismin kendisi denenir. Port kapalıysa / firewall
            //         engelliyorsa hedefe HTTP isteği hiç göndermiyoruz; nedeni (timeout, reddedildi, DNS) telnet raporunda yazar.
            var tcp = await TcpProbe.ProbeAllAsync(
                host, port, 1, timeout, TimeSpan.Zero, _options.MaxAddresses, maxDuration, request.Aborted).ConfigureAwait(false);
            tcp.ExecutedByInstanceId = InstanceIdentityBuilder.GetInstanceId(_options);
            tcp.ExecutedByMachineName = Environment.MachineName;
            bool tcpOk = tcp.HostnameAttempts.Any(r => r.Success);

            DiscoverReport report;
            if (!tcpOk || !usesConnectivityProbe)
            {
                // step 8a: Telnet başarısız ("unreachable") veya hedef ConnectivityProbe kullanmıyor ("tcp"): burada bitiyor.
                report = new DiscoverReport { TargetKind = tcpOk ? ProbeTargetKind.Tcp : ProbeTargetKind.Unreachable };
            }
            else
            {
                // step 8b: Telnet açık ve hedef de ConnectivityProbe kullanıyor: identity ucuna, her seferinde yeni bağlantıyla,
                //          pod sayısından çok daha fazla istek atıp tekrar eden kimlikleri ayıklayarak hedefin pod'larını buluyoruz.
                //          Hedefin yolu bizimkiyle aynı varsayılır. Anahtar: istek hedefin anahtarını taşıyorsa o, yoksa bizimki.
                var targetKey = request.Header(ConnectivityProbeOptions.TargetAccessKeyHeader);
                report = await InstanceCollector.CollectAsync(
                    scheme, host, port, _identityPath, attempts, timeout, TimeSpan.Zero, adaptive: true, confidence: confidence,
                    maxDuration: maxDuration, accessKey: string.IsNullOrEmpty(targetKey) ? _options.AccessKey : targetKey,
                    cancellationToken: request.Aborted).ConfigureAwait(false);

                report.TargetKind = report.Succeeded > 0 ? ProbeTargetKind.ConnectivityProbe
                    : report.ConnectivityProbeDetected ? ProbeTargetKind.ConnectivityProbeError
                    : ProbeTargetKind.Other;

                // Bütün cevaplar tek bir pod'dan geldiyse: uygulama gerçekten tek pod olabilir, ama session affinity (sticky
                // session) veya bağlantıları tek pod'a sabitleyen bir proxy de aynı sonucu verir. Sessizce "1 pod" demek yerine not düşüyoruz.
                if (report.DistinctInstances == 1 && report.Succeeded >= SingleInstanceNoteThreshold)
                    report.Notes.Add("All " + report.Succeeded + " responses came from a single instance. If the target runs more than one "
                                     + "instance, the load balancer may not be spreading new connections (e.g. session affinity is enabled).");
            }

            // step 9: "Ben şu makinedeki şu pod olarak test ettim" bilgisini ve telnet sonucunu ekliyoruz.
            report.ExecutedByInstanceId = tcp.ExecutedByInstanceId;
            report.ExecutedByMachineName = tcp.ExecutedByMachineName;
            report.ProbeVersion = ProbeInfo.Version;
            report.Tcp = tcp;

            _options.Log?.Invoke(ProbeLogLevel.Information, "ConnectivityProbe discover " + host + ":" + port + " -> " + report.TargetKind
                + (report.DistinctInstances > 0 ? ", " + report.DistinctInstances + " instance(s)" : "")
                + " in " + clock.ElapsedMilliseconds + " ms (caller " + (request.RemoteIp ?? "?") + ")");
            return Ok(report);
        }

        /// <summary>Tek pod uyarısı için en az kaç başarılı yanıt gerektiği (az sayıda istekle karar vermemek için).</summary>
        private const int SingleInstanceNoteThreshold = 10;

        /// <summary>
        /// Hedef izin listesine uyuyor mu? Liste boşsa her hedef serbesttir. Biçimler (büyük/küçük harf duyarsız):
        /// "host:port" tam eşleşme, "host:*" her port, "*.alan:port" alan adının alt adları, "*.alan:*".
        /// </summary>
        internal static bool IsTargetAllowed(ICollection<string> allowed, string host, int port)
        {
            if (allowed.Count == 0) return true;
            foreach (var entry in allowed)
            {
                // IPv6 adresleri de ":" içerdiği için port ayracı olarak son ":"yi kullanıyoruz ("[::1]:80" ve "::1:80" ikisi de olur).
                var sep = entry.LastIndexOf(':');
                if (sep <= 0) continue;
                var patternHost = entry.Substring(0, sep).Trim().Trim('[', ']');
                var patternPort = entry.Substring(sep + 1).Trim();

                bool portOk = patternPort == "*" || patternPort == port.ToString(CultureInfo.InvariantCulture);
                bool hostOk = patternHost.StartsWith("*.", StringComparison.Ordinal)
                    ? host.EndsWith(patternHost.Substring(1), StringComparison.OrdinalIgnoreCase) && host.Length > patternHost.Length - 1
                    : string.Equals(patternHost, host, StringComparison.OrdinalIgnoreCase);
                if (portOk && hostOk) return true;
            }
            return false;
        }

        // Reddedilen isteği loglayıp hata yanıtı döner (kim, neden).
        private ProbeResponse Rejected(ProbeRequest request, int status, string message)
        {
            _options.Log?.Invoke(ProbeLogLevel.Warning, "ConnectivityProbe rejected request from " + (request.RemoteIp ?? "?") + ": " + status + " " + message);
            return Error(status, message);
        }

        /// <summary>Kendi dinleyicisi gibi tüm yolları karşılayan adaptörler için: bizim uçlarımız dışındaki istekler.</summary>
        public static ProbeResponse NotFound() => Error(404, "not found");

        private static ProbeResponse Ok(object body) => new ProbeResponse { StatusCode = 200, Json = Json.Serialize(body) };

        private static ProbeResponse Error(int status, string message) =>
            new ProbeResponse { StatusCode = status, Json = Json.Serialize(new { error = message }) };

        // Beklenen anahtar boşsa her isteği kabul eder; doluysa sabit sürede karşılaştırır (zamanlama saldırısına karşı).
        private static bool KeyMatches(string? expected, string? provided)
        {
            if (string.IsNullOrEmpty(expected)) return true;

            var a = Encoding.UTF8.GetBytes(expected);
            var b = Encoding.UTF8.GetBytes(provided ?? "");
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                diff |= (i < a.Length ? a[i] : 0) ^ (i < b.Length ? b[i] : 0);
            return diff == 0;
        }

        // Pozitif tamsayı okur; boş, negatif, sıfır veya geçersizse false döner (varsayılan değer kullanılır).
        private static bool TryGetPositive(string? value, out int result) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0;

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    }
}
