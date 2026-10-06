using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>
    /// Web sunucusu olmayan uygulamalar (Windows Service, console, .NET worker service) için kendi küçük HTTP dinleyicisi.
    /// Uygulama başlarken tek satır: <c>var probe = ConnectivityProbeListener.Start();</c> ve kapanırken <c>probe.Dispose();</c>.
    /// Ayarlar ortam değişkenlerinden (ve .NET Framework'te app.config appSettings'ten) okunur. Varsayılan adres: http://+:8099/
    /// </summary>
    public sealed class ConnectivityProbeListener : IDisposable
    {
        /// <summary>ListenerPrefixes verilmediğinde dinlenen adres.</summary>
        public const string DefaultPrefix = "http://+:8099/";

        private readonly HttpListener _listener = new HttpListener();
        private readonly ProbeEngine _engine;
        private readonly Task _loop;
        private int _disposed;

        private ConnectivityProbeListener(ConnectivityProbeOptions options)
        {
            _engine = new ProbeEngine(options);
            Prefixes = options.ListenerPrefixes.Count > 0 ? options.ListenerPrefixes.ToList() : new List<string> { DefaultPrefix };
            foreach (var prefix in Prefixes) _listener.Prefixes.Add(prefix.EndsWith("/", StringComparison.Ordinal) ? prefix : prefix + "/");

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                // Windows'ta "http://+:port/" gibi tüm adresleri dinlemek yönetici yetkisi veya URL ayrımı ister.
                throw new InvalidOperationException(
                    "ConnectivityProbe: '" + string.Join(", ", Prefixes) + "' dinlenemedi (erişim reddedildi). Yönetici olarak bir kez " +
                    "'netsh http add urlacl url=" + Prefixes[0] + " user=<servis hesabı>' çalıştırın veya ConnectivityProbe:ListenerPrefixes " +
                    "ile 'http://localhost:<port>/' kullanın.", ex);
            }

            _loop = Task.Run(AcceptLoopAsync);
        }

        /// <summary>Dinlenen adresler.</summary>
        public IReadOnlyList<string> Prefixes { get; }

        /// <summary>
        /// Dinleyiciyi başlatır. <paramref name="options"/> verilmezse ayarlar ortam değişkenlerinden / appSettings'ten okunur.
        /// ConnectivityProbe:Enabled=false ise hiçbir şey dinlemeden boş bir nesne döner (Dispose güvenle çağrılabilir).
        /// </summary>
        public static ConnectivityProbeListener? Start(ConnectivityProbeOptions? options = null)
        {
            options ??= ConnectivityProbeOptions.FromEnvironment();
            return options.Enabled ? new ConnectivityProbeListener(options) : null;
        }

        private async Task AcceptLoopAsync()
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (Volatile.Read(ref _disposed) == 1 || !_listener.IsListening)
                {
                    return; // durduruluyor
                }
                catch (HttpListenerException)
                {
                    continue; // tek bir bağlantının hatası dinleyiciyi durdurmasın
                }

                // Her istek ayrı işlenir; yavaş bir test diğerlerini bekletmez.
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                // step 1: Bizim uçlarımızdan biri değilse 404 dönüyoruz (bu dinleyicide başka bir şey yok).
                var request = context.Request;
                ProbeResponse response;
                if (!_engine.TryMatch(request.Url?.AbsolutePath, request.HttpMethod, out var kind))
                {
                    response = ProbeEngine.NotFound();
                }
                else
                {
                    // step 2: İsteği motorun ortak biçimine çevirip işletiyoruz.
                    response = await _engine.HandleAsync(new ProbeRequest
                    {
                        Query = key => request.QueryString[key],
                        Header = key => request.Headers[key],
                        RemoteIp = request.RemoteEndPoint?.Address.ToString(),
                        Scheme = request.Url?.Scheme,
                        Host = request.Url?.Authority
                    }, kind).ConfigureAwait(false);
                }

                // step 3: JSON yanıtı yazıyoruz.
                var body = response.Body;
                context.Response.StatusCode = response.StatusCode;
                context.Response.ContentType = ProbeResponse.ContentType;
                context.Response.Headers["Cache-Control"] = ProbeResponse.CacheControl;
                context.Response.Headers[ConnectivityProbeOptions.MarkerHeader] = ProbeResponse.MarkerValue;
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // İstemci bağlantıyı kesmiş olabilir; dinleyiciyi etkilemesin.
                try { context.Response.StatusCode = 500; } catch (Exception) { /* yanıt zaten başlamış olabilir */ }
            }
            finally
            {
                try { context.Response.Close(); } catch (Exception) { /* bağlantı kapanmış olabilir */ }
            }
        }

        /// <summary>Dinleyiciyi durdurur.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }
}
