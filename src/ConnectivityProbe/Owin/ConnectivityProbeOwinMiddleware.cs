using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectivityProbe
{
    /// <summary>
    /// OWIN adaptörü (Katana self-host, Web API 2 self-host, Nancy...). Hiçbir OWIN paketine bağımlı değildir; standart OWIN
    /// ortam sözlüğüyle çalışır. Startup'ta tek satır: <c>app.Use(typeof(ConnectivityProbeOwinMiddleware));</c>
    /// (ayarlar ortam değişkenlerinden / app.config appSettings'ten okunur) veya
    /// <c>app.Use(typeof(ConnectivityProbeOwinMiddleware), options);</c>.
    /// IIS üzerinde çalışan OWIN uygulamalarında buna gerek yoktur; IIS modülü kendiliğinden devrededir.
    /// </summary>
    public sealed class ConnectivityProbeOwinMiddleware
    {
        private readonly Func<IDictionary<string, object>, Task> _next;
        private readonly ProbeEngine _engine;

        public ConnectivityProbeOwinMiddleware(Func<IDictionary<string, object>, Task> next)
            : this(next, ConnectivityProbeOptions.FromEnvironment())
        {
        }

        public ConnectivityProbeOwinMiddleware(Func<IDictionary<string, object>, Task> next, ConnectivityProbeOptions options)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _engine = new ProbeEngine(options ?? throw new ArgumentNullException(nameof(options)));
        }

        public async Task Invoke(IDictionary<string, object> environment)
        {
            // step 1: Bizim uçlarımızdan biri değilse sonraki middleware'e bırakıyoruz (owin.RequestPath uygulama köküne göredir).
            var path = Get<string>(environment, "owin.RequestPath");
            var method = Get<string>(environment, "owin.RequestMethod");
            if (!_engine.TryMatch(path, method, out var kind))
            {
                await _next(environment).ConfigureAwait(false);
                return;
            }

            // step 2: İsteği motorun ortak biçimine çevirip işletiyoruz.
            var query = QueryString.Parse(Get<string>(environment, "owin.RequestQueryString"));
            var headers = Get<IDictionary<string, string[]>>(environment, "owin.RequestHeaders");
            var request = new ProbeRequest
            {
                Query = key => query.TryGetValue(key, out var v) ? v : null,
                Header = key => FirstHeader(headers, key),
                RemoteIp = Get<string>(environment, "server.RemoteIpAddress"),
                Scheme = Get<string>(environment, "owin.RequestScheme"),
                Host = FirstHeader(headers, "Host"),
                Aborted = environment.TryGetValue("owin.CallCancelled", out var ct) && ct is CancellationToken token ? token : CancellationToken.None
            };
            var response = await _engine.HandleAsync(request, kind).ConfigureAwait(false);

            // step 3: JSON yanıtı yazıyoruz.
            var body = response.Body;
            environment["owin.ResponseStatusCode"] = response.StatusCode;
            if (Get<IDictionary<string, string[]>>(environment, "owin.ResponseHeaders") is { } responseHeaders)
            {
                responseHeaders["Content-Type"] = new[] { ProbeResponse.ContentType };
                responseHeaders["Cache-Control"] = new[] { ProbeResponse.CacheControl };
                responseHeaders[ConnectivityProbeOptions.MarkerHeader] = new[] { ProbeResponse.MarkerValue };
                responseHeaders["Content-Length"] = new[] { body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            }
            if (Get<Stream>(environment, "owin.ResponseBody") is { } stream)
                await stream.WriteAsync(body, 0, body.Length, request.Aborted).ConfigureAwait(false);
        }

        private static T? Get<T>(IDictionary<string, object> environment, string key) where T : class =>
            environment.TryGetValue(key, out var value) ? value as T : null;

        private static string? FirstHeader(IDictionary<string, string[]>? headers, string name)
        {
            if (headers == null) return null;
            // OWIN sunucularının çoğu büyük/küçük harf duyarsız sözlük verir; vermeyenler için elle de arıyoruz.
            if (headers.TryGetValue(name, out var values) || (values = headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value) != null)
                return values.Length > 0 ? values[0] : null;
            return null;
        }
    }
}
