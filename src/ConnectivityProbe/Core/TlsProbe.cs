using System;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>Bir hedefe TLS el sıkışmasının sonucu ve sunucu sertifikası.</summary>
    public sealed class TlsReport
    {
        /// <summary>El sıkışma başarılı ve sertifika geçerli (güvenilir, adı uyuyor, süresi dolmamış).</summary>
        public bool Success { get; set; }
        /// <summary>El sıkışma tamamlandı mı (sertifika geçersiz olsa bile).</summary>
        public bool Handshake { get; set; }
        public string? Protocol { get; set; }
        public string? Subject { get; set; }
        public string? Issuer { get; set; }
        public DateTime? NotBeforeUtc { get; set; }
        public DateTime? NotAfterUtc { get; set; }
        /// <summary>Sertifika sorunları: None, RemoteCertificateNameMismatch, RemoteCertificateChainErrors...</summary>
        public string? CertificateErrors { get; set; }
        public long ElapsedMs { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// TLS kontrolü: hedefe bağlanıp el sıkışır ve sunucu sertifikasını okur (bitiş tarihi, ad uyumu, güven zinciri).
    /// Sertifika geçersiz olsa da bağlantı reddedilmez; amaç sorunu raporlamaktır. Hiçbir veri gönderilmez.
    /// </summary>
    public static class TlsProbe
    {
        public static async Task<TlsReport> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var report = new TlsReport();
            var sw = Stopwatch.StartNew();
            var policyErrors = SslPolicyErrors.None;

            try
            {
                using (var client = new TcpClient())
                {
                    // step 1: TCP bağlantısı (zaman aşımıyla).
                    var connect = client.ConnectAsync(host, port);
                    if (await Task.WhenAny(connect, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) != connect)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Observe(connect);
                        report.Error = "Timeout after " + (int)timeout.TotalMilliseconds + " ms";
                        return report;
                    }
                    await connect.ConfigureAwait(false);

                    // step 2: El sıkışma. Sertifika doğrulaması sonucu kaydedilir ama bağlantı kabul edilir (raporlamak için).
                    //         Sertifika bilgileri burada okunur: .NET 5+ sertifikayı SslStream ile birlikte serbest bırakır.
                    using (var ssl = new SslStream(client.GetStream(), false, (sender, cert, chain, errors) =>
                    {
                        policyErrors = errors;
                        if (cert != null)
                        {
                            using (var cert2 = new X509Certificate2(cert))
                            {
                                report.Subject = cert2.Subject;
                                report.Issuer = cert2.Issuer;
                                report.NotBeforeUtc = cert2.NotBefore.ToUniversalTime();
                                report.NotAfterUtc = cert2.NotAfter.ToUniversalTime();
                            }
                        }
                        return true;
                    }))
                    {
                        var handshake = ssl.AuthenticateAsClientAsync(host, null, Protocols, false);
                        if (await Task.WhenAny(handshake, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) != handshake)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            Observe(handshake);
                            report.Error = "TLS handshake timeout after " + (int)timeout.TotalMilliseconds + " ms";
                            return report;
                        }
                        await handshake.ConfigureAwait(false);
                        report.Handshake = true;
                        report.Protocol = ssl.SslProtocol.ToString();
                    }
                }

                // step 3: Sertifika doğrulamasının sonucu.
                report.CertificateErrors = policyErrors.ToString();
                report.Success = policyErrors == SslPolicyErrors.None;
                if (!report.Success) report.Error = "Certificate: " + policyErrors;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // TLS konuşmayan bir port, desteklenmeyen protokol, bağlantı reddi...
                while (ex.InnerException != null && !(ex is SocketException)) ex = ex.InnerException;
                report.Error = ex is SocketException se ? se.SocketErrorCode + ": " + se.Message : ex.Message;
            }
            finally
            {
                report.ElapsedMs = sw.ElapsedMilliseconds;
            }
            return report;
        }

#if NETFRAMEWORK
        // .NET Framework 4.6.2'de varsayılan protokoller eski (SSL3/TLS1.0); güncel sunucularla konuşabilmek için açıkça veriyoruz.
        private static readonly SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls;
#else
        // İşletim sisteminin varsayılanı (TLS 1.3 dahil).
        private static readonly SslProtocols Protocols = SslProtocols.None;
#endif

        private static void Observe(Task task) =>
            task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
}
