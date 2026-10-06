using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary><see cref="DiscoverReport.TargetKind"/> değerleri: hedefin ne olduğu ve ne kadar test edilebildiği.</summary>
    public static class ProbeTargetKind
    {
        /// <summary>Telnet (TCP) başarısız; hedefe hiç HTTP isteği gönderilmedi.</summary>
        public const string Unreachable = "unreachable";
        /// <summary>
        /// Telnet başarılı; pod keşfi istenmedi (hedef ConnectivityProbe kullanmıyor: DB, Redis, dış servis...).
        /// Hedefe hiç HTTP isteği gönderilmedi.
        /// </summary>
        public const string Tcp = "tcp";
        /// <summary>Hedef ConnectivityProbe kullanıyor ve pod'ları keşfedildi.</summary>
        public const string ConnectivityProbe = "connectivityProbe";
        /// <summary>Hedefte ConnectivityProbe var ama istekleri reddetti (ör. anahtar yanlış: 401, yapılandırılmamış: 403).</summary>
        public const string ConnectivityProbeError = "connectivityProbeError";
        /// <summary>TCP açık ama hedefte ConnectivityProbe yok (başka bir HTTP servisi veya HTTP konuşmayan bir servis).</summary>
        public const string Other = "other";
    }

    public sealed class InstanceSummary
    {
        public string InstanceId { get; set; } = "";
        /// <summary>Bu instance'ın kaç istekte cevap verdiği.</summary>
        public int Hits { get; set; }
        /// <summary>The most recent identity this instance returned.</summary>
        public InstanceIdentity Identity { get; set; } = new InstanceIdentity();
    }

    public sealed class DiscoverReport
    {
        /// <summary>Testi yapan instance (pod): "ben şu makinedeki şu pod olarak test ettim". discover ucu doldurur.</summary>
        public string? ExecutedByInstanceId { get; set; }
        public string? ExecutedByMachineName { get; set; }
        /// <summary>Testi yapan uygulamadaki ConnectivityProbe sürümü. discover ucu doldurur.</summary>
        public string? ProbeVersion { get; set; }

        /// <summary>Hedefin identity adresi (yalnızca pod keşfi yapıldıysa).</summary>
        public string Url { get; set; } = "";

        /// <summary>
        /// Hedefin türü (bkz. <see cref="ProbeTargetKind"/>): unreachable | tcp | connectivityProbe | connectivityProbeError | other.
        /// discover ucu doldurur.
        /// </summary>
        public string? TargetKind { get; set; }

        /// <summary>Her durumda önce yapılan telnet (TCP) testinin sonucu. TCP başarısızsa keşif yapılmaz. discover ucu doldurur.</summary>
        public ProbeReport? Tcp { get; set; }

        /// <summary>Hedefin en az bir yanıtında ConnectivityProbe işareti (X-ConnectivityProbe başlığı) görüldü mü.</summary>
        public bool ConnectivityProbeDetected { get; set; }

        /// <summary>
        /// true: ilk istekler hiç başarılı olmadığı ve bu düzelmeyecek bir durum olduğu için (ConnectivityProbe yok veya
        /// anahtar/izin hatası) keşif erken bırakıldı.
        /// </summary>
        public bool StoppedEarly { get; set; }

        /// <summary>Upper bound of attempts that was requested.</summary>
        public int Attempts { get; set; }
        /// <summary>Attempts actually made (less than Attempts when adaptive mode converged early).</summary>
        public int AttemptsMade { get; set; }
        public bool Adaptive { get; set; }
        /// <summary>Adaptive only: the confidence level the request asked for.</summary>
        public double? RequestedConfidence { get; set; }
        /// <summary>Adaptive only: true if the confidence was reached before running out of attempts.</summary>
        public bool? Converged { get; set; }
        /// <summary>Adaptive only: successful attempts at the end that showed no new instance.</summary>
        public int? ConsecutiveWithoutNew { get; set; }
        /// <summary>
        /// Adaptive only: estimated probability that no further instance exists, assuming requests are spread
        /// roughly evenly. An estimate, not a guarantee.
        /// </summary>
        public double? Confidence { get; set; }
        /// <summary>true: istenen denemeler bitmeden süre sınırına (MaxRequestDuration) ulaşıldı, sonuç eksik olabilir.</summary>
        public bool Truncated { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int DistinctInstances { get; set; }
        public List<InstanceSummary> Instances { get; set; } = new List<InstanceSummary>();
        /// <summary>
        /// Hata nedenleri ve kaç kez oldukları. Load balancer üzerinden atılan başarısız istekler cevap vermediği için
        /// hangi pod'a gittikleri bilinemez; bu yüzden pod'a değil buraya yazılır.
        /// </summary>
        public Dictionary<string, int> Errors { get; set; } = new Dictionary<string, int>();
        /// <summary>Sonucu yorumlarken dikkat edilmesi gerekenler (ör. tüm yanıtlar tek pod'dan geldi: session affinity olabilir).</summary>
        public List<string> Notes { get; set; } = new List<string>();
    }

    public static class InstanceCollector
    {
        /// <summary>İlk bu kadar istekte ne kimlik ne de ConnectivityProbe işareti gelirse keşif bırakılır.</summary>
        private const int GiveUpAfterAttempts = 3;

        /// <summary>
        /// Calls the identity endpoint <paramref name="attempts"/> times, each over a brand-new connection
        /// so the load balancer / kube-proxy can pick a different instance every time, and groups the answers.
        /// Coverage is probabilistic: to see all N instances use noticeably more than N attempts (~3N is a good start).
        /// </summary>
        /// <param name="scheme">"http" veya "https": hedefe hangi protokolle gidileceği.</param>
        /// <param name="host">Pod'lara dağıtım yapan servis adı veya IP (IPv6 köşeli parantezsiz yazılabilir).</param>
        /// <param name="port">Hedef port.</param>
        /// <param name="identityPath">Hedefteki identity yolu (ör. /connectivity-probe/identity).</param>
        /// <param name="attempts">En fazla kaç istek atılacağı (üst sınır). Adaptive kapalıysa tam bu kadar atılır.</param>
        /// <param name="timeout">Tek bir isteğin zaman aşımı; cevap vermeyen pod'lar hata olarak sayılır.</param>
        /// <param name="delayBetweenAttempts">İki istek arası bekleme.</param>
        /// <param name="adaptive">true ise yeterli güvene ulaşınca attempts dolmadan durur.</param>
        /// <param name="confidence">Adaptive için "başka instance yok" olasılığı hedefi (0.5-0.999).</param>
        /// <param name="maxDuration">Toplam süre sınırı; dolunca yeni istek atılmaz ve rapor Truncated olarak döner. null: sınır yok.</param>
        /// <param name="accessKey">Hedefte erişim anahtarı (AccessKey) tanımlıysa gönderilecek anahtar. null: gönderilmez.</param>
        /// <param name="cancellationToken">İstemci bağlantıyı keserse toplamayı durdurur.</param>
        public static async Task<DiscoverReport> CollectAsync(
            string scheme, string host, int port, string identityPath, int attempts,
            TimeSpan timeout, TimeSpan delayBetweenAttempts, bool adaptive = false, double confidence = 0.99,
            TimeSpan? maxDuration = null, string? accessKey = null, CancellationToken cancellationToken = default)
        {
            // step 1: Hedef URL'yi ve boş raporu hazırlıyoruz. UriBuilder IPv6 adresleri köşeli paranteze alır.
            //         byId, her instanceId'yi tek kez tutan sözlük.
            var url = new UriBuilder(scheme, host, port, identityPath).Uri;
            var report = new DiscoverReport { Url = url.ToString(), Attempts = attempts, Adaptive = adaptive };
            var byId = new Dictionary<string, InstanceSummary>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int streak = 0;       // üst üste kaç başarılı istek yeni instance göstermedi
            int clientErrors = 0; // 4xx yanıtlar (anahtar yanlış, izin yok...): tekrar denemekle düzelmez

            if (adaptive)
            {
                report.RequestedConfidence = confidence;
                report.Converged = false;
            }

            for (int i = 0; i < attempts; i++)
            {
                // step 2: Süre sınırı dolduysa yeni istek atmıyoruz (ilk istek her zaman atılır).
                if (i > 0 && maxDuration.HasValue && clock.Elapsed >= maxDuration.Value)
                {
                    report.Truncated = true;
                    break;
                }

                // step 3: İlk birkaç istekte hiç başarı yoksa ve bu düzelmeyecek bir durumsa keşfi bırakıyoruz, hedefe boşuna
                //         istek yağdırmıyoruz:
                //           - ne kimlik ne de ConnectivityProbe işareti geldi: hedef bizim uygulamamız değil (başka bir servis);
                //           - hepsi 4xx (anahtar yanlış, izin yok): tekrar denemek sonucu değiştirmez.
                //         Timeout / 5xx gibi geçici hatalarda devam ediyoruz; bozuk birkaç pod'a denk gelinmiş olabilir.
                if (i >= GiveUpAfterAttempts && report.Succeeded == 0
                    && (!report.ConnectivityProbeDetected || clientErrors == report.Failed))
                {
                    report.StoppedEarly = true;
                    break;
                }

                // İlk istekten sonra, istenmişse istekler arasında bekliyoruz.
                if (i > 0 && delayBetweenAttempts > TimeSpan.Zero)
                    await Task.Delay(delayBetweenAttempts, cancellationToken).ConfigureAwait(false);

                report.AttemptsMade++;

                try
                {
                    // step 4: Her denemede YENİ HttpClient/handler açıyoruz ve "Connection: close" gönderiyoruz.
                    //         Bağlantı yeniden kullanılırsa (keep-alive) hep aynı pod'a düşeriz; yeni bağlantı
                    //         load balancer'ın her seferinde yeniden pod seçmesini sağlar.
                    using (var handler = new HttpClientHandler { UseProxy = false })
                    using (var client = new HttpClient(handler) { Timeout = timeout })
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        request.Headers.ConnectionClose = true;
                        if (!string.IsNullOrEmpty(accessKey))
                            request.Headers.TryAddWithoutValidation(ConnectivityProbeOptions.AccessKeyHeader, accessKey);

                        using (var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                        {
                            // step 5: Yanıtta ConnectivityProbe işareti var mı? Hata yanıtında bile bakıyoruz; böylece
                            //         "ConnectivityProbe var ama anahtar yanlış" ile "burada ConnectivityProbe yok" ayrılır.
                            if (response.Headers.Contains(ConnectivityProbeOptions.MarkerHeader))
                                report.ConnectivityProbeDetected = true;

                            // Hata yanıtında açıklama varsa ({"error": "..."}) mesaja ekliyoruz; ör. "HTTP 401: ... key".
                            if (!response.IsSuccessStatusCode)
                            {
                                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500) clientErrors++;
                                throw new HttpRequestException("HTTP " + (int)response.StatusCode
                                    + await ErrorDetailAsync(response).ConfigureAwait(false));
                            }

                            // step 6: Cevabı kimlik nesnesine çeviriyoruz; instanceId yoksa bu bizim endpoint'imiz değildir.
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            InstanceIdentity? identity;
                            try { identity = InstanceIdentity.FromJson(Json.Parse(body)); }
                            catch (FormatException) { identity = null; }
                            if (identity == null)
                                throw new InvalidOperationException("Yanıt bir ConnectivityProbe identity yanıtı değil");

                            report.ConnectivityProbeDetected = true;

                            // step 7: Bu instanceId'yi ilk kez görüyorsak listeye ekleyip seriyi sıfırlıyoruz
                            //         (yeni pod bulundu, şimdiye kadarki kanıt geçersiz). Daha önce görmüşsek seriyi artırıyoruz.
                            if (!byId.TryGetValue(identity.InstanceId, out var summary))
                            {
                                byId[identity.InstanceId] = summary = new InstanceSummary { InstanceId = identity.InstanceId };
                                streak = 0;
                            }
                            else
                            {
                                streak++;
                            }

                            // step 8: İsabet sayısını ve son gelen kimliği kaydediyoruz.
                            summary.Hits++;
                            summary.Identity = identity;
                            report.Succeeded++;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // İstemci vazgeçtiyse hata sayıp devam etmiyoruz, işlemi bırakıyoruz.
                    throw;
                }
                catch (Exception ex)
                {
                    // step 9: Hata (timeout, bağlantı reddi, HTTP hatası...) sebebini sayarak kaydediyoruz.
                    //         Başarısız istek cevap vermediği için hangi pod'a gittiği bilinemez; seriye dahil etmeyip devam ediyoruz.
                    var message = ex is TaskCanceledException ? "Timeout after " + (int)timeout.TotalMilliseconds + " ms" : (ex.InnerException?.Message ?? ex.Message);
                    report.Failed++;
                    report.Errors.TryGetValue(message, out int n);
                    report.Errors[message] = n + 1;
                    continue;
                }

                // step 10: Adaptive modda, görülen pod sayısına göre gereken seriye ulaştıysak istenen güvene erişmişizdir; duruyoruz.
                if (adaptive && byId.Count > 0 && streak >= RequiredStreak(byId.Count, confidence))
                {
                    report.Converged = true;
                    break;
                }
            }

            // step 11: Sonuçları instanceId'ye göre sıralayıp farklı instance sayısını yazıyoruz.
            report.Instances = byId.Values.OrderBy(s => s.InstanceId, StringComparer.Ordinal).ToList();
            report.DistinctInstances = report.Instances.Count;

            // step 12: Adaptive modda, elimizdeki seriye göre "başka instance yok" olasılığını tahmin edip rapora ekliyoruz.
            if (adaptive)
            {
                report.ConsecutiveWithoutNew = streak;
                report.Confidence = byId.Count == 0 ? 0 : Math.Round(1 - Math.Pow(byId.Count / (byId.Count + 1.0), streak), 4);
            }

            return report;
        }

        // ConnectivityProbe'un {"error": "..."} hata gövdesinden açıklamayı alır; yoksa boş döner.
        private static async Task<string> ErrorDetailAsync(HttpResponseMessage response)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var error = Json.GetString(Json.Parse(body) as IDictionary<string, object?>, "error");
                return string.IsNullOrEmpty(error) ? "" : ": " + error;
            }
            catch (Exception)
            {
                return ""; // gövde JSON değil (ör. HTML hata sayfası)
            }
        }

        /// <summary>
        /// With d instances seen, one more hidden instance would be missed with probability (d/(d+1))^k after k
        /// attempts in a row, so k = ln(1 - confidence) / ln(d/(d+1)) gives the requested confidence.
        /// </summary>
        internal static int RequiredStreak(int distinct, double confidence) =>
            (int)Math.Ceiling(Math.Log(1 - confidence) / Math.Log(distinct / (distinct + 1.0)));
    }
}
