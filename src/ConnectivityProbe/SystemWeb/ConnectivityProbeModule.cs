using System;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using ConnectivityProbe;

// IIS / klasik ASP.NET (MVC 5, Web API 2, WebForms, WCF, IIS'te OWIN): ASP.NET, bin klasöründeki her DLL'de bu niteliği arar
// ve uygulama başlarken çağırır. Böylece modül hiçbir kod veya web.config değişikliği olmadan kendini kaydeder.
[assembly: PreApplicationStartMethod(typeof(ConnectivityProbeModule), nameof(ConnectivityProbeModule.RegisterAutomatically))]

namespace ConnectivityProbe
{
    /// <summary>
    /// IIS / System.Web adaptörü. Ayarlar web.config appSettings'ten ("ConnectivityProbe:AccessKey" ...) veya ortam
    /// değişkenlerinden okunur. Otomatik kayıt IIS'in Integrated pipeline modunda çalışır; Classic modda bu modülü web.config
    /// &lt;system.web&gt;&lt;httpModules&gt; altına elle ekleyin ve "ConnectivityProbe:AutoRegister" değerini false yapın.
    /// </summary>
    public sealed class ConnectivityProbeModule : IHttpModule
    {
        private const string ItemKey = "ConnectivityProbe.Kind";

        // Ayarlar uygulama ömrü boyunca bir kez okunur (web.config değişince IIS uygulamayı zaten yeniden başlatır).
        private static readonly Lazy<ProbeEngine> Engine =
            new Lazy<ProbeEngine>(() => new ProbeEngine(ConnectivityProbeOptions.FromEnvironment()), LazyThreadSafetyMode.ExecutionAndPublication);

        private static int _registered;

        /// <summary>ASP.NET tarafından uygulama başlarken çağrılır (PreApplicationStartMethod).</summary>
        public static void RegisterAutomatically()
        {
            // step 1: Kapalıysa veya otomatik kayıt istenmiyorsa hiçbir şey yapmıyoruz.
            var settings = ProbeSettings.FromEnvironmentAndAppSettings();
            if (settings.TryGetValue("AutoRegister", out var auto) && bool.TryParse(auto, out var enabled) && !enabled) return;
            if (!Engine.Value.Options.Enabled) return;

            // step 2: Modülü bir kez kaydediyoruz.
            if (Interlocked.Exchange(ref _registered, 1) == 1) return;
            HttpApplication.RegisterModule(typeof(ConnectivityProbeModule));
        }

        public void Init(HttpApplication application)
        {
            application.PostResolveRequestCache += OnPostResolveRequestCache;
            application.PostMapRequestHandler += OnPostMapRequestHandler;
        }

        public void Dispose() { }

        // step 3: İstek bizim uçlarımızdan biriyse işleyiciyi kendimize yönlendiriyoruz. RemapHandler, IIS'in yerel
        //         işleyicisi (ör. statik dosya) yerine yönetilen işleyicimizin çalışmasını sağlar.
        private static void OnPostResolveRequestCache(object sender, EventArgs e)
        {
            var context = ((HttpApplication)sender).Context;
            var path = context.Request.AppRelativeCurrentExecutionFilePath; // "~/connectivity-probe/identity"
            if (path == null || !Engine.Value.TryMatch(path.TrimStart('~'), context.Request.HttpMethod, out var kind)) return;

            context.Items[ItemKey] = kind;
            context.RemapHandler(new ProbeHttpHandler(Engine.Value, kind));
        }

        // step 4: MVC / Web API rotası (UrlRoutingModule) bizden sonra işleyiciyi değiştirdiyse, eşleme bittikten sonra geri alıyoruz.
        private static void OnPostMapRequestHandler(object sender, EventArgs e)
        {
            var context = ((HttpApplication)sender).Context;
            if (context.Items[ItemKey] is ProbeEndpointKind kind && !(context.Handler is ProbeHttpHandler))
                context.Handler = new ProbeHttpHandler(Engine.Value, kind);
        }

        private sealed class ProbeHttpHandler : HttpTaskAsyncHandler
        {
            private readonly ProbeEngine _engine;
            private readonly ProbeEndpointKind _kind;

            public ProbeHttpHandler(ProbeEngine engine, ProbeEndpointKind kind)
            {
                _engine = engine;
                _kind = kind;
            }

            public override bool IsReusable => false;

            public override async Task ProcessRequestAsync(HttpContext context)
            {
                // step 5: İsteği motorun ortak biçimine çevirip işletiyoruz.
                var request = new ProbeRequest
                {
                    Query = key => context.Request.QueryString[key],
                    Header = key => context.Request.Headers[key],
                    RemoteIp = context.Request.UserHostAddress,
                    Scheme = context.Request.Url.Scheme,
                    Host = context.Request.Url.Authority,
                    Aborted = ClientDisconnected(context)
                };

                // Bilerek ConfigureAwait(false) kullanmıyoruz: yanıtı ASP.NET'in istek bağlamında yazmalıyız.
                var response = await _engine.HandleAsync(request, _kind);

                // step 6: JSON yanıtı yazıyoruz. TrySkipIisCustomErrors: IIS 4xx/5xx yanıtlarımızı kendi HTML hata sayfasıyla
                //         değiştirmesin, Monitor açıklamalı JSON'u görebilsin.
                var r = context.Response;
                r.TrySkipIisCustomErrors = true;
                r.StatusCode = response.StatusCode;
                r.ContentType = "application/json";
                r.Charset = "utf-8";
                r.Cache.SetCacheability(HttpCacheability.NoCache);
                r.Cache.SetNoStore();
                r.AppendHeader(ConnectivityProbeOptions.MarkerHeader, ProbeResponse.MarkerValue);
                r.BinaryWrite(response.Body);
            }

            // İstemci bağlantıyı keserse işi durdurmak için; Classic pipeline'da desteklenmez, o zaman iptal edilmez.
            private static CancellationToken ClientDisconnected(HttpContext context)
            {
                try { return context.Response.ClientDisconnectedToken; }
                catch (PlatformNotSupportedException) { return CancellationToken.None; }
            }
        }
    }
}
