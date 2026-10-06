using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>Result of a single TCP connection attempt.</summary>
    public sealed class ProbeResult
    {
        /// <summary>What was dialed (hostname or IP).</summary>
        public string Target { get; set; } = "";
        public int Port { get; set; }
        public bool Success { get; set; }
        /// <summary>The IP the connection actually landed on (set only on success).</summary>
        public string? RemoteAddress { get; set; }
        public long ElapsedMs { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>All attempts made against one resolved IP.</summary>
    public sealed class AddressReport
    {
        public string Address { get; set; } = "";
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public List<ProbeResult> Results { get; set; } = new List<ProbeResult>();
    }

    public sealed class ProbeReport
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public int Attempts { get; set; }
        /// <summary>Hangi instance'ın (pod'un) bu testi yaptığı; merkezi monitörün sonuçları pod'a göre gruplaması için. Handler doldurur.</summary>
        public string? ExecutedByInstanceId { get; set; }
        public string? ExecutedByMachineName { get; set; }
        public string? ResolveError { get; set; }
        /// <summary>
        /// true: istenen denemelerin hepsi yapılmadan süre sınırına (MaxRequestDuration) ulaşıldı, sonuç eksik.
        /// </summary>
        public bool Truncated { get; set; }
        /// <summary>Every IP the name resolved to, each probed directly (pod IPs for a headless service).</summary>
        public List<AddressReport> ResolvedAddresses { get; set; } = new List<AddressReport>();
        /// <summary>Repeated connections made via the hostname, showing where each one was routed.</summary>
        public List<ProbeResult> HostnameAttempts { get; set; } = new List<ProbeResult>();
        /// <summary>Distinct IPs reached through the hostname attempts, with hit counts.</summary>
        public Dictionary<string, int> ReachedAddresses { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>Telnet-style checks: open a TCP connection to host:port and close it.</summary>
    public static class TcpProbe
    {
        /// <summary>Tek bir TCP bağlantısı dener (telnet atmak gibi) ve sonucu döner.</summary>
        /// <param name="host">Bağlanılacak hostname veya IP.</param>
        /// <param name="port">Hedef TCP portu.</param>
        /// <param name="timeout">Bu süre içinde bağlantı kurulamazsa "Timeout" hatası olarak döner (isim çözümleme dahil).</param>
        /// <param name="cancellationToken">İptal edilirse deneme durdurulur ve OperationCanceledException fırlatılır.</param>
        public static async Task<ProbeResult> ProbeAsync(
            string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            // step 1: Sonuç nesnesini ve süre ölçümü için kronometreyi hazırlıyoruz.
            var result = new ProbeResult { Target = host, Port = port };
            var sw = Stopwatch.StartNew();

            using (var client = new TcpClient())
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    // step 2: Bağlantıyı başlatıyoruz ve "bağlantı" ile "zaman aşımı" görevlerinden hangisi önce biterse onu bekliyoruz
                    //         (netstandard2.0'da ConnectAsync iptal/timeout desteklemediği için bu yöntemi kullanıyoruz).
                    var connect = client.ConnectAsync(host, port);
                    var finished = await Task.WhenAny(connect, Task.Delay(timeout, timer.Token)).ConfigureAwait(false);

                    if (finished != connect)
                    {
                        // step 3a: Zaman aşımı önce bitti. İptal istendiyse onu fırlatıyoruz, yoksa Timeout hatası yazıyoruz.
                        cancellationToken.ThrowIfCancellationRequested();
                        result.Error = "Timeout after " + (int)timeout.TotalMilliseconds + " ms";
                        // Bekleyen bağlantı denemesinin ileride fırlatacağı hata "gözlemlenmemiş" kalmasın.
                        Observe(connect);
                    }
                    else
                    {
                        // Bağlantı önce bitti: bekleyen zamanlayıcıyı iptal edip hemen serbest bırakıyoruz.
                        timer.Cancel();

                        // step 3b: Hata varsa burada fırlar (reddedildi, host bulunamadı...).
                        await connect.ConfigureAwait(false);
                        result.Success = client.Connected;

                        // step 4: Gerçekte hangi IP'ye bağlandığımızı kaydediyoruz (isimle bağlanınca hangi pod/IP'ye gittiğimizi gösterir).
                        if (client.Client.RemoteEndPoint is IPEndPoint ep)
                        {
                            var ip = ep.Address;
                            result.RemoteAddress = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // İptal bir test sonucu değildir, yukarıya iletiyoruz.
                    throw;
                }
                catch (Exception ex)
                {
                    // step 5: Bağlantı hatasını okunur bir mesaja çeviriyoruz (örn. "ConnectionRefused: ...").
                    result.Error = Describe(ex);
                }
            }

            // step 6: Geçen süreyi yazıp sonucu dönüyoruz. using bloğu bittiği için bağlantı kapanmış olur.
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// Resolves <paramref name="host"/> to all of its IPs and probes each one <paramref name="attempts"/> times,
        /// and at the same time makes <paramref name="attempts"/> fresh connections through the hostname itself to show
        /// where the platform routes them.
        /// </summary>
        /// <param name="host">DNS ile çözülecek isim (veya IP). Dönen her IP ayrı test edilir.</param>
        /// <param name="port">Hedef TCP portu.</param>
        /// <param name="attempts">Her IP için ve isim üzerinden kaç kez bağlanılacağı.</param>
        /// <param name="timeout">Tek bir bağlantı denemesinin (ve DNS çözümlemesinin) zaman aşımı.</param>
        /// <param name="delayBetweenAttempts">Aynı hedefe yapılan denemeler arası bekleme.</param>
        /// <param name="maxAddresses">Test edilecek en fazla IP sayısı (aşırı büyük DNS cevaplarına karşı koruma).</param>
        /// <param name="maxDuration">
        /// Toplam süre sınırı. Dolunca yeni deneme başlatılmaz, o ana kadarki sonuç <see cref="ProbeReport.Truncated"/> ile döner.
        /// null: sınır yok.
        /// </param>
        /// <param name="cancellationToken">İptal edilirse kalan denemeler yapılmaz.</param>
        public static async Task<ProbeReport> ProbeAllAsync(
            string host, int port, int attempts, TimeSpan timeout, TimeSpan delayBetweenAttempts,
            int maxAddresses = 64, TimeSpan? maxDuration = null, CancellationToken cancellationToken = default)
        {
            var report = new ProbeReport { Host = host, Port = port, Attempts = attempts };
            var clock = Stopwatch.StartNew();
            bool Expired() => maxDuration.HasValue && clock.Elapsed >= maxDuration.Value;

            // step 1: İsmi DNS ile çözüp dönen TÜM IP'leri alıyoruz (headless service'te her pod'un IP'si gelir).
            //         DNS de zaman aşımına bağlı; yavaş bir DNS sunucusu isteği asılı bırakmasın.
            var addresses = await ResolveAsync(host, timeout, maxAddresses, report, cancellationToken).ConfigureAwait(false);

            // step 2: Her IP'yi doğrudan test ediyoruz. IP'ler birbirine paralel, bir IP'nin denemeleri ardışık çalışır.
            var ipTask = Task.WhenAll(addresses.Select(async ip =>
            {
                var ar = new AddressReport { Address = ip.ToString() };
                for (int i = 0; i < attempts; i++)
                {
                    // Süre dolduysa yeni deneme başlatmıyoruz (ilk deneme her zaman yapılır).
                    if (i > 0 && Expired()) { report.Truncated = true; break; }
                    if (i > 0 && delayBetweenAttempts > TimeSpan.Zero)
                        await Task.Delay(delayBetweenAttempts, cancellationToken).ConfigureAwait(false);
                    var r = await ProbeAsync(ar.Address, port, timeout, cancellationToken).ConfigureAwait(false);
                    ar.Results.Add(r);
                    if (r.Success) ar.Succeeded++; else ar.Failed++;
                }
                return ar;
            }));

            // step 3: Aynı anda (paralel) deneme de İSİM üzerinden yapıyoruz; her bağlantı yeni olduğu için DNS dönüşü /
            //         load balancer / kube-proxy hedefi yeniden seçer. Hangi IP'ye gidildiğini RemoteAddress gösterir.
            //         Paralel çalıştığı için toplam süre "IP testleri + isim testleri" değil, ikisinden uzun olanı kadardır.
            var hostTask = Task.Run(async () =>
            {
                var list = new List<ProbeResult>();
                for (int i = 0; i < attempts; i++)
                {
                    if (i > 0 && Expired()) { report.Truncated = true; break; }
                    if (i > 0 && delayBetweenAttempts > TimeSpan.Zero)
                        await Task.Delay(delayBetweenAttempts, cancellationToken).ConfigureAwait(false);
                    list.Add(await ProbeAsync(host, port, timeout, cancellationToken).ConfigureAwait(false));
                }
                return list;
            }, cancellationToken);

            await Task.WhenAll(ipTask, hostTask).ConfigureAwait(false);
            report.ResolvedAddresses = ipTask.Result.ToList();
            report.HostnameAttempts = hostTask.Result;

            // step 4: Ulaşılan IP'lerin isabet sayısını topluyoruz (hangi IP'ye kaç kez gidildi).
            foreach (var r in report.HostnameAttempts.Where(r => r.RemoteAddress != null))
            {
                report.ReachedAddresses.TryGetValue(r.RemoteAddress!, out int n);
                report.ReachedAddresses[r.RemoteAddress!] = n + 1;
            }

            return report;
        }

        // İsmi IP'lere çözer; hata veya zaman aşımında ResolveError'u doldurup boş liste döner.
        private static async Task<IPAddress[]> ResolveAsync(
            string host, TimeSpan timeout, int maxAddresses, ProbeReport report, CancellationToken cancellationToken)
        {
            // IP adresi verildiyse DNS'e gitmeye gerek yok.
            if (IPAddress.TryParse(host, out var literal)) return new[] { literal };

            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                try
                {
                    var dns = Dns.GetHostAddressesAsync(host);
                    var finished = await Task.WhenAny(dns, Task.Delay(timeout, timer.Token)).ConfigureAwait(false);
                    if (finished != dns)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Observe(dns);
                        report.ResolveError = "DNS timeout after " + (int)timeout.TotalMilliseconds + " ms";
                        return new IPAddress[0];
                    }

                    timer.Cancel();
                    return (await dns.ConfigureAwait(false)).Distinct().Take(maxAddresses).ToArray();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Çözülemezse hatayı rapora yazıyoruz; isim üzerinden deneme yine yapılır ve aynı hatayı gösterir.
                    report.ResolveError = Describe(ex);
                    return new IPAddress[0];
                }
            }
        }

        // Arka planda kalan bir görevin hatası "gözlemlenmemiş" kalmasın (UnobservedTaskException'a düşmesin).
        private static void Observe(Task task) =>
            task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        private static string Describe(Exception ex) =>
            ex is SocketException se ? se.SocketErrorCode + ": " + se.Message : ex.Message;
    }
}
