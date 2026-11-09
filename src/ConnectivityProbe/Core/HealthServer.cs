using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>
    /// ConnectivityProbe'un kendi health ucu: uygulamanın HTTP sunucusundan bağımsız, ayrı bir portta çok küçük bir HTTP
    /// dinleyicisi (yalnızca <c>GET /connectivityprobe/health</c>). Ham TCP üzerinde çalıştığı için ASP.NET Core, klasik ASP.NET /
    /// IIS, Worker Service, konsol ve Windows servislerinde ve tüm .NET sürümlerinde aynıdır; uygulamanın kendi /health ucuyla
    /// çakışmaz. Cevap ConnectivityProbe imzası taşır (X-ConnectivityProbe başlığı ve "connectivityProbe" alanı): başka
    /// uygulamaların pod'ları bu servisin DLL'i gerçekten kullanıp kullanmadığını buradan kesin olarak anlar.
    /// </summary>
    internal sealed class HealthServer : IDisposable
    {
        /// <summary>Health ucunun yolu.</summary>
        public const string Path = "/connectivityprobe/health";
        /// <summary>Cevaptaki imza başlığı (değeri ConnectivityProbe sürümü).</summary>
        public const string SignatureHeader = "X-ConnectivityProbe";
        /// <summary>
        /// Varsayılan port. İşletim sistemlerinin giden bağlantılara verdiği aralıkların (Linux 32768-60999, Windows 49152-65535)
        /// dışında ve yaygın servislerin kullanmadığı bir port.
        /// </summary>
        public const int DefaultPort = 29470;

        private readonly TcpListener _listener;
        private readonly Func<string> _body;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        private HealthServer(TcpListener listener, Func<string> body)
        {
            _listener = listener;
            _body = body;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run(() => AcceptLoopAsync(_stop.Token));
        }

        /// <summary>Dinlenen port.</summary>
        public int Port { get; }

        /// <summary>
        /// Health ucunu açar: önce istenen port, doluysa sonraki 9 port, o da olmazsa işletim sisteminin verdiği boş bir port.
        /// Hiç açılamazsa null (uygulama etkilenmez).
        /// </summary>
        public static HealthServer? Start(int port, Func<string> body)
        {
            for (int p = port; p < port + 10 && p <= 65535; p++)
                if (TryListen(p) is TcpListener listener) return new HealthServer(listener, body);
            return TryListen(0) is TcpListener any ? new HealthServer(any, body) : null;
        }

        private static TcpListener? TryListen(int port)
        {
            // IPv4 ve IPv6'yı birlikte dinlemeyi deneriz; IPv6 kapalı ortamlarda yalnızca IPv4. Port "özel kullanım"la açılır:
            // Windows'ta aksi halde başka bir programın dinlediği port paylaşılabilir; böylece port doluysa açılmaz, sıradakine geçilir.
            try
            {
                var dual = new TcpListener(IPAddress.IPv6Any, port);
                Exclusive(dual);
                dual.Server.DualMode = true;
                dual.Start();
                return dual;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.SocketErrorCode == SocketError.AccessDenied)
            {
                return null;   // port dolu ya da ayrılmış
            }
            catch (Exception)
            {
                try
                {
                    var v4 = new TcpListener(IPAddress.Any, port);
                    Exclusive(v4);
                    v4.Start();
                    return v4;
                }
                catch (SocketException)
                {
                    return null;
                }
            }
        }

        private static void Exclusive(TcpListener listener)
        {
            try { listener.ExclusiveAddressUse = true; }
            catch (Exception) { /* bazı platformlarda desteklenmez; varsayılan zaten paylaşımsızdır */ }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (ct.IsCancellationRequested) return; continue; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    var stream = client.GetStream();

                    // İstek satırı ve başlıklar (en fazla 8 KB, 5 sn); gövde okunmaz.
                    var buffer = new byte[8192];
                    int total = 0;
                    using (var timer = new CancellationTokenSource(5000))
                    {
                        while (total < buffer.Length)
                        {
                            var read = stream.ReadAsync(buffer, total, buffer.Length - total, timer.Token);
                            if (await Task.WhenAny(read, Task.Delay(Timeout.Infinite, timer.Token)).ConfigureAwait(false) != read) return;
                            int n = await read.ConfigureAwait(false);
                            if (n <= 0) break;
                            total += n;
                            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n")) break;
                        }
                    }
                    var request = Encoding.ASCII.GetString(buffer, 0, total);
                    var line = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
                    var method = line.Length > 0 ? line[0] : "";
                    var path = line.Length > 1 ? line[1].Split('?')[0] : "";

                    byte[] response;
                    if ((method == "GET" || method == "HEAD") && string.Equals(path.TrimEnd('/'), Path, StringComparison.OrdinalIgnoreCase))
                        response = Response("200 OK", "application/json; charset=utf-8", _body(), method == "HEAD");
                    else
                        response = Response("404 Not Found", "text/plain; charset=utf-8", "Not found. ConnectivityProbe health: " + Path, method == "HEAD");
                    await stream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Bozuk istek, kopan bağlantı: uygulama etkilenmez.
                }
            }
        }

        private static byte[] Response(string status, string contentType, string body, bool headOnly)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = "HTTP/1.1 " + status + "\r\n"
                       + "Content-Type: " + contentType + "\r\n"
                       + "Content-Length: " + bytes.Length + "\r\n"
                       + SignatureHeader + ": " + ProbeInfo.Version + "\r\n"
                       + "Cache-Control: no-store\r\n"
                       + "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            if (headOnly) return headBytes;
            var all = new byte[headBytes.Length + bytes.Length];
            Buffer.BlockCopy(headBytes, 0, all, 0, headBytes.Length);
            Buffer.BlockCopy(bytes, 0, all, headBytes.Length, bytes.Length);
            return all;
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch (Exception) { /* zaten kapalı */ }
        }
    }
}
