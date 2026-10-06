using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>
    /// Bir hedefin testini bu instance'ın içinden yapar: her durumda önce telnet (TCP), hedef de ConnectivityProbe kullanıyorsa
    /// ardından hedefin pod keşfi. discover ucu (Discover mod) ve Strict modun arka plan işi aynı kodu kullanır; böylece iki
    /// modun sonuçları birebir aynı anlamı taşır.
    /// </summary>
    internal static class DiscoverRunner
    {
        /// <summary>Tek pod uyarısı için en az kaç başarılı yanıt gerektiği (az sayıda istekle karar vermemek için).</summary>
        private const int SingleInstanceNoteThreshold = 10;

        /// <param name="options">Path (hedefin identity yolu), MaxAddresses, MaxRequestDuration ve kimlik bilgisi için.</param>
        /// <param name="host">Test edilecek host (ProbeTarget ile ayrıştırılmış).</param>
        /// <param name="port">Hedef port.</param>
        /// <param name="scheme">Pod keşfinde kullanılacak şema (http/https).</param>
        /// <param name="usesConnectivityProbe">Hedef de ConnectivityProbe kullanıyorsa true: telnetten sonra pod keşfi yapılır.</param>
        /// <param name="timeout">Tek bağlantının / isteğin zaman aşımı.</param>
        /// <param name="attempts">Pod keşfinde en fazla identity isteği.</param>
        /// <param name="confidence">Pod keşfinde "başka pod yok" olasılığı hedefi.</param>
        /// <param name="targetAccessKey">Hedefin identity ucuna gönderilecek anahtar (null: gönderilmez).</param>
        /// <param name="cancellationToken">İptal.</param>
        public static async Task<DiscoverReport> RunAsync(
            ConnectivityProbeOptions options, string host, int port, string scheme, bool usesConnectivityProbe,
            TimeSpan timeout, int attempts, double confidence, string? targetAccessKey, CancellationToken cancellationToken)
        {
            // Toplam süre sınırı: dolunca kalan denemeler yapılmaz ve rapor "truncated: true" olur.
            TimeSpan? maxDuration = options.MaxRequestDuration > TimeSpan.Zero ? options.MaxRequestDuration : (TimeSpan?)null;

            // step 1: HER DURUMDA önce telnet (TCP). İsim çözülür; her IP ve ismin kendisi denenir. Port kapalıysa / firewall
            //         engelliyorsa hedefe HTTP isteği hiç göndermiyoruz; nedeni (timeout, reddedildi, DNS) telnet raporunda yazar.
            var tcp = await TcpProbe.ProbeAllAsync(
                host, port, 1, timeout, TimeSpan.Zero, options.MaxAddresses, maxDuration, cancellationToken).ConfigureAwait(false);
            tcp.ExecutedByInstanceId = InstanceIdentityBuilder.GetInstanceId(options);
            tcp.ExecutedByMachineName = Environment.MachineName;
            bool tcpOk = tcp.HostnameAttempts.Any(r => r.Success);

            DiscoverReport report;
            if (!tcpOk || !usesConnectivityProbe)
            {
                // step 2a: Telnet başarısız ("unreachable") veya hedef ConnectivityProbe kullanmıyor ("tcp"): burada bitiyor.
                report = new DiscoverReport { TargetKind = tcpOk ? ProbeTargetKind.Tcp : ProbeTargetKind.Unreachable };
            }
            else
            {
                // step 2b: Telnet açık ve hedef de ConnectivityProbe kullanıyor: identity ucuna, her seferinde yeni bağlantıyla,
                //          pod sayısından çok daha fazla istek atıp tekrar eden kimlikleri ayıklayarak hedefin pod'larını buluyoruz.
                //          Hedefin yolu bizimkiyle aynı varsayılır.
                var identityPath = "/" + options.Path.Trim().Trim('/') + "/identity";
                report = await InstanceCollector.CollectAsync(
                    scheme, host, port, identityPath, attempts, timeout, TimeSpan.Zero, adaptive: true, confidence: confidence,
                    maxDuration: maxDuration, accessKey: targetAccessKey, cancellationToken: cancellationToken).ConfigureAwait(false);

                report.TargetKind = report.Succeeded > 0 ? ProbeTargetKind.ConnectivityProbe
                    : report.ConnectivityProbeDetected ? ProbeTargetKind.ConnectivityProbeError
                    : ProbeTargetKind.Other;

                // Bütün cevaplar tek bir pod'dan geldiyse: uygulama gerçekten tek pod olabilir, ama session affinity (sticky
                // session) veya bağlantıları tek pod'a sabitleyen bir proxy de aynı sonucu verir. Sessizce "1 pod" demek yerine not düşüyoruz.
                if (report.DistinctInstances == 1 && report.Succeeded >= SingleInstanceNoteThreshold)
                    report.Notes.Add("All " + report.Succeeded + " responses came from a single instance. If the target runs more than one "
                                     + "instance, the load balancer may not be spreading new connections (e.g. session affinity is enabled).");
            }

            // step 3: "Ben şu makinedeki şu pod olarak test ettim" bilgisini ve telnet sonucunu ekliyoruz.
            report.ExecutedByInstanceId = tcp.ExecutedByInstanceId;
            report.ExecutedByMachineName = tcp.ExecutedByMachineName;
            report.ProbeVersion = ProbeInfo.Version;
            report.Tcp = tcp;
            return report;
        }
    }
}
