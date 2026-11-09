using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>Bir servisin ConnectivityProbe health ucunun sorgu sonucu.</summary>
    public sealed class HealthCheckResult
    {
        /// <summary>Sorgulanan adres (host:port).</summary>
        public string Target { get; set; } = "";
        /// <summary>Hedef pod (Monitor biliyorsa).</summary>
        public string? Instance { get; set; }
        /// <summary>
        /// probe: ConnectivityProbe cevap verdi (DLL var). refused: sunucuya ulaşıldı ama health portunu dinleyen yok (DLL yok).
        /// other: portta başka bir servis var (bizim DLL değil). timeout: ulaşılamadı (DLL yok ya da port dışarı açık değil).
        /// dns: ad çözülemedi. error: başka bir hata.
        /// </summary>
        public string Outcome { get; set; } = "";
        /// <summary>Servis ConnectivityProbe kullanıyor mu: true (kesin), false (kesin değil), null (anlaşılamadı).</summary>
        public bool? Implemented { get; set; }
        /// <summary>Servisin bildirdiği durum: Healthy, Degraded (yalnızca Outcome = probe).</summary>
        public string? Status { get; set; }
        public string? AppId { get; set; }
        public string? AppName { get; set; }
        public string? AppVersion { get; set; }
        public long ElapsedMs { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Bir servisin ConnectivityProbe health ucunu sorgular ve DLL'i kullanıp kullanmadığını anlar.</summary>
    public static class HealthProbe
    {
        public static async Task<HealthCheckResult> CheckAsync(string host, int port, int timeoutMs, CancellationToken ct)
        {
            var result = new HealthCheckResult { Target = (host.Contains(":") ? "[" + host.Trim('[', ']') + "]" : host) + ":" + port };
            var clock = Stopwatch.StartNew();
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var client = new TcpClient())
            {
                timer.CancelAfter(timeoutMs);
                try
                {
                    var connect = client.ConnectAsync(host.Trim('[', ']'), port);
                    if (await Task.WhenAny(connect, Task.Delay(Timeout.Infinite, timer.Token)).ConfigureAwait(false) != connect)
                        return Done(result, clock, "timeout", null, "connection timed out");
                    await connect.ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    switch (ex.SocketErrorCode)
                    {
                        case SocketError.ConnectionRefused: return Done(result, clock, "refused", false, "nothing listens on the health port");
                        case SocketError.HostNotFound: case SocketError.NoData: case SocketError.TryAgain:
                            return Done(result, clock, "dns", null, ex.Message);
                        case SocketError.TimedOut: return Done(result, clock, "timeout", null, ex.Message);
                        default: return Done(result, clock, "error", null, ex.Message);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    return Done(result, clock, "error", null, ex.Message);
                }

                string response;
                try
                {
                    var stream = client.GetStream();
                    var request = Encoding.ASCII.GetBytes("GET " + HealthServer.Path + " HTTP/1.1\r\nHost: " + host + "\r\nUser-Agent: ConnectivityProbe/"
                                                          + ProbeInfo.Version + "\r\nAccept: application/json\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(request, 0, request.Length, timer.Token).ConfigureAwait(false);
                    var buffer = new byte[64 * 1024];
                    int total = 0;
                    while (total < buffer.Length)
                    {
                        var read = stream.ReadAsync(buffer, total, buffer.Length - total, timer.Token);
                        if (await Task.WhenAny(read, Task.Delay(Timeout.Infinite, timer.Token)).ConfigureAwait(false) != read) break;
                        int n = await read.ConfigureAwait(false);
                        if (n <= 0) break;
                        total += n;
                    }
                    response = Encoding.UTF8.GetString(buffer, 0, total);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Bağlandı ama cevap alınamadı: portta konuşmayan başka bir şey var.
                    return Done(result, clock, "other", false, "no HTTP answer on the health port: " + ex.Message);
                }

                if (response.Length == 0) return Done(result, clock, "other", false, "the health port closed the connection without answering");
                var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var head = split >= 0 ? response.Substring(0, split) : response;
                var body = split >= 0 ? response.Substring(split + 4) : "";
                if (!head.StartsWith("HTTP/", StringComparison.Ordinal))
                    return Done(result, clock, "other", false, "another service answers on the health port (not HTTP)");
                if (head.IndexOf("\n" + HealthServer.SignatureHeader + ":", StringComparison.OrdinalIgnoreCase) < 0)
                    return Done(result, clock, "other", false, "another HTTP service answers on the health port (no ConnectivityProbe signature)");

                // ConnectivityProbe: durumu ve uygulama bilgisini okuyoruz.
                Done(result, clock, "probe", true, null);
                try
                {
                    if (Json.Parse(body) is IDictionary<string, object?> o)
                    {
                        result.Status = Json.GetString(o, "status");
                        result.AppId = Json.GetString(o, "appId");
                        result.AppName = Json.GetString(o, "appName");
                        result.AppVersion = Json.GetString(o, "appVersion");
                    }
                }
                catch (FormatException)
                {
                    result.Error = "health answer could not be read";
                }
                return result;
            }
        }

        private static HealthCheckResult Done(HealthCheckResult r, Stopwatch clock, string outcome, bool? implemented, string? error)
        {
            r.Outcome = outcome;
            r.Implemented = implemented;
            r.Error = error;
            r.ElapsedMs = clock.ElapsedMilliseconds;
            return r;
        }
    }
}
