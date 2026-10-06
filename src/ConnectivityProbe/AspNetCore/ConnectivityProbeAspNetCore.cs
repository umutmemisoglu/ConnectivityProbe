using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ConnectivityProbe;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ASP.NET Core'un "hosting startup" mekanizması: uygulama kodu değişmeden, yalnızca şu ortam değişkeni verilerek devreye girer:
//   ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=ConnectivityProbe
// (Kubernetes manifest'i, IIS'te web.config <environmentVariables>, launchSettings.json ...). ASP.NET Core 2.1 - 10 destekler.
[assembly: HostingStartup(typeof(ConnectivityProbeHostingStartup))]

namespace ConnectivityProbe
{
    /// <summary>
    /// Kodsuz aktivasyon: ayarları uygulamanın yapılandırmasından ("ConnectivityProbe" bölümü, ortam değişkenleri) okur ve
    /// middleware'i pipeline'ın en başına ekler. Uygulamanın kendi yetkilendirmesinden önce çalışır; koruma AccessKey ile sağlanır.
    /// </summary>
    public sealed class ConnectivityProbeHostingStartup : IHostingStartup
    {
        public void Configure(IWebHostBuilder builder)
        {
            builder.ConfigureServices((context, services) =>
            {
                var options = AspNetCoreAdapter.ReadOptions(context.Configuration);
                // İşaret: uygulama ayrıca UseConnectivityProbe() çağırırsa ikinci kez eklenmesin.
                services.AddSingleton(new AspNetCoreAdapter.AutoRegistered());
                services.AddSingleton<IStartupFilter>(new ProbeStartupFilter(options));
            });
        }

        private sealed class ProbeStartupFilter : IStartupFilter
        {
            private readonly ConnectivityProbeOptions _options;
            public ProbeStartupFilter(ConnectivityProbeOptions options) => _options = options;

            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                AspNetCoreAdapter.Register(app, _options);
                next(app);
            };
        }
    }

    public static class ConnectivityProbeApplicationBuilderExtensions
    {
        /// <summary>
        /// İsteğe bağlı: hosting startup (ortam değişkeni) yerine kodla eklemek isteyenler için. Ayarlar yine yapılandırmadan
        /// okunur; <paramref name="configure"/> ile üzerine yazılabilir. Hosting startup zaten etkinse hiçbir şey yapmaz.
        /// </summary>
        public static IApplicationBuilder UseConnectivityProbe(this IApplicationBuilder app, Action<ConnectivityProbeOptions>? configure = null)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            if (app.ApplicationServices.GetService(typeof(AspNetCoreAdapter.AutoRegistered)) != null) return app;

            var configuration = app.ApplicationServices.GetService(typeof(IConfiguration)) as IConfiguration;
            var options = configuration != null ? AspNetCoreAdapter.ReadOptions(configuration) : ConnectivityProbeOptions.FromEnvironment();
            configure?.Invoke(options);
            AspNetCoreAdapter.Register(app, options);
            return app;
        }
    }

    internal static class AspNetCoreAdapter
    {
        private const string RegisteredKey = "ConnectivityProbe.Registered";

        /// <summary>Hosting startup'ın etkin olduğunu DI üzerinden belirten işaret.</summary>
        internal sealed class AutoRegistered { }

        /// <summary>"ConnectivityProbe" bölümünü (appsettings, ortam değişkenleri, komut satırı...) ayarlara çevirir.</summary>
        public static ConnectivityProbeOptions ReadOptions(IConfiguration configuration)
        {
            var settings = new List<KeyValuePair<string, string?>>();
            Flatten(configuration.GetSection(ConnectivityProbeOptions.SectionName), "", settings);
            return ConnectivityProbeOptions.FromSettings(settings);
        }

        private static void Flatten(IConfiguration section, string prefix, List<KeyValuePair<string, string?>> into)
        {
            foreach (var child in section.GetChildren())
            {
                var key = prefix + child.Key;
                if (child.Value != null) into.Add(new KeyValuePair<string, string?>(key, child.Value));
                Flatten(child, key + ":", into);
            }
        }

        public static void Register(IApplicationBuilder app, ConnectivityProbeOptions options)
        {
            // Aynı pipeline'a iki kez eklenmesin.
            if (app.Properties.ContainsKey(RegisteredKey)) return;
            app.Properties[RegisteredKey] = true;

            // Log çıkışı verilmemişse uygulamanın kendi log altyapısına ("ConnectivityProbe" kategorisi) bağlıyoruz.
            if (options.Log == null && app.ApplicationServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory loggerFactory)
            {
                var logger = loggerFactory.CreateLogger("ConnectivityProbe");
                options.Log = (level, message) =>
                {
                    var mapped = level == ProbeLogLevel.Error ? LogLevel.Error
                        : level == ProbeLogLevel.Warning ? LogLevel.Warning
                        : level == ProbeLogLevel.Information ? LogLevel.Information
                        : LogLevel.Debug;
                    // Mesaj şablon olarak yorumlanmasın diye ({...} içerebilir) doğrudan durum + biçimlendirici veriyoruz.
                    logger.Log(mapped, default(EventId), message, null, (state, _) => state);
                };
            }

            // Strict mod (MonitorUrl + AppKey): uygulama başlayınca agent'ı başlatıp kapanırken durduruyoruz (Monitor'e
            // "kapanıyorum" bildirimi gider). Uçların açık/kapalı olmasından (Enabled) bağımsızdır.
            if (options.StrictEnabled) StartAgentWithApplication(app, options);

            if (!options.Enabled) return;
            var engine = new ProbeEngine(options);
            app.Use(next => context => InvokeAsync(context, next, engine));
        }

        private static void StartAgentWithApplication(IApplicationBuilder app, ConnectivityProbeOptions options)
        {
#if NET8_0_OR_GREATER
            var lifetime = app.ApplicationServices.GetService(typeof(Microsoft.Extensions.Hosting.IHostApplicationLifetime))
                as Microsoft.Extensions.Hosting.IHostApplicationLifetime;
#else
            // ASP.NET Core 2.1 - 7 için ortak en düşük sürümdeki karşılığı (yeni sürümlerde de hâlâ kayıtlıdır).
            var lifetime = app.ApplicationServices.GetService(typeof(IApplicationLifetime)) as IApplicationLifetime;
#endif
            if (lifetime == null)
            {
                ConnectivityProbeAgent.Start(options);
                return;
            }
            lifetime.ApplicationStarted.Register(() => ConnectivityProbeAgent.Start(options));
            lifetime.ApplicationStopping.Register(() => ConnectivityProbeAgent.Current?.Stop());
        }

        private static async Task InvokeAsync(HttpContext context, RequestDelegate next, ProbeEngine engine)
        {
            // step 1: Bizim uçlarımızdan biri değilse uygulamaya bırakıyoruz (Path, PathBase'i içermez; uygulama köküne göredir).
            if (!engine.TryMatch(context.Request.Path.Value, context.Request.Method, out var kind))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            // step 2: İsteği motorun ortak biçimine çevirip işletiyoruz.
            var request = new ProbeRequest
            {
                Query = key => { var v = context.Request.Query[key]; return v.Count == 0 ? null : v[0]; },
                Header = key => { var v = context.Request.Headers[key]; return v.Count == 0 ? null : v[0]; },
                RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
                Scheme = context.Request.Scheme,
                Host = context.Request.Host.ToString(),
                Aborted = context.RequestAborted
            };
            var response = await engine.HandleAsync(request, kind).ConfigureAwait(false);

            // step 3: JSON yanıtı yazıyoruz.
            var body = response.Body;
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = ProbeResponse.ContentType;
            context.Response.Headers["Cache-Control"] = ProbeResponse.CacheControl;
            context.Response.Headers[ConnectivityProbeOptions.MarkerHeader] = ProbeResponse.MarkerValue;
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body, 0, body.Length, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
