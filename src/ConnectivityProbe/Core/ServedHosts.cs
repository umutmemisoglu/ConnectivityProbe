using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;

namespace ConnectivityProbe
{
    /// <summary>
    /// Uygulamanın hangi adreslerden çağrıldığını (gelen isteklerin Host başlığı, ör. <c>orders.example.com</c>) kendiliğinden öğrenir;
    /// Monitor bu adresleri, başka uygulamaların bağlantı adresleriyle birebir eşleştirerek bağlantının hedef uygulamasını bulur.
    /// Servis ingress arkasında olsa da (yalnızca web portu açık) çalışır; uygulamada hiçbir ayar ya da kod gerekmez.
    /// <para>
    /// Nasıl: ASP.NET Core her isteğin başında "Microsoft.AspNetCore" DiagnosticListener'ı üzerinden bir olay yayınlar. Bu sınıf
    /// yalnızca o olayı dinler ve isteğin Host değerini okur; isteğe, cevaba ve uygulamanın akışına dokunmaz. Kütüphane
    /// ASP.NET Core'a bağımlı olmadığı için tipler yansıma (reflection) ile okunur; ASP.NET Core olmayan uygulamalarda (worker,
    /// konsol, klasik ASP.NET) hiçbir şey yapmaz.
    /// </para>
    /// En fazla <see cref="MaxHosts"/> adres tutulur; IP adresleri ve localhost alınmaz. Saniyede en fazla birkaç istek incelenir.
    /// </summary>
    internal sealed class ServedHosts : IObserver<object>, IDisposable
    {
        public const int MaxHosts = 20;
        private const string ListenerName = "Microsoft.AspNetCore";
        private const string BeginRequest = "Microsoft.AspNetCore.Hosting.BeginRequest";
        private static readonly long SampleTicks = Stopwatch.Frequency / 20;   // en fazla saniyede ~20 istek

        private readonly ConcurrentDictionary<string, byte> _hosts = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        private readonly ConcurrentDictionary<Type, Func<object, object?>?> _contextGetters = new ConcurrentDictionary<Type, Func<object, object?>?>();
        private long _lastSample;
        private int _failures;
        private volatile bool _disposed;

        private ServedHosts() { }

        /// <summary>Dinlemeyi başlatır; DiagnosticListener yoksa (eski .NET Framework) null.</summary>
        public static ServedHosts? Start()
        {
            try
            {
                var type = Type.GetType("System.Diagnostics.DiagnosticListener, System.Diagnostics.DiagnosticSource", throwOnError: false);
                // AllListeners: IObservable<DiagnosticListener>. IObservable<out T> kovaryant olduğu için IObservable<object> olarak alınır.
                if (type?.GetProperty("AllListeners", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not IObservable<object> all) return null;
                var hosts = new ServedHosts();
                var sub = all.Subscribe(hosts);
                lock (hosts._subscriptions) hosts._subscriptions.Add(sub);
                return hosts;
            }
            catch (Exception)
            {
                return null;   // dinlenemiyorsa uygulama etkilenmez; eşleştirme Monitor'deki diğer yollarla yapılır
            }
        }

        /// <summary>Şimdiye kadar görülen adresler (küçük harf, sıralı).</summary>
        public List<string> Snapshot() => _hosts.Keys.OrderBy(h => h, StringComparer.Ordinal).ToList();

        /// <summary>Gelen bir isteğin Host değerini ekler (testler ve dinleyici için).</summary>
        internal void Add(string? host)
        {
            var h = Normalize(host);
            if (h != null && _hosts.Count < MaxHosts) _hosts.TryAdd(h, 0);
        }

        /// <summary>Küçük harf, sondaki nokta ve port atılmış; IP adresi, localhost ya da geçersizse null.</summary>
        internal static string? Normalize(string? host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            var h = host!.Trim().TrimEnd('.').ToLowerInvariant();
            if (h.StartsWith("[", StringComparison.Ordinal)) return null;                          // IPv6
            var colon = h.IndexOf(':');
            if (colon >= 0) h = h.Substring(0, colon);
            if (h.Length == 0 || h.Length > 253 || h == "localhost" || h.EndsWith(".localhost", StringComparison.Ordinal)) return null;
            if (IPAddress.TryParse(h, out _)) return null;
            foreach (var c in h)
                if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '.' || c == '_')) return null;
            return h;
        }

        // ---------------------------------------------------------------- DiagnosticListener.AllListeners

        public void OnNext(object listener)
        {
            if (_disposed) return;
            try
            {
                var type = listener.GetType();
                if (!string.Equals(type.GetProperty("Name")?.GetValue(listener) as string, ListenerName, StringComparison.Ordinal)) return;
                // Yalnızca BeginRequest olayını açıyoruz (Predicate<string>); diğer olaylar ve Activity oluşturma etkilenmez.
                var subscribe = type.GetMethod("Subscribe", new[] { typeof(IObserver<KeyValuePair<string, object>>), typeof(Predicate<string>) });
                if (subscribe == null) return;
                var observer = new EventObserver(this);
                if (subscribe.Invoke(listener, new object[] { observer, new Predicate<string>(name => name == BeginRequest) }) is IDisposable sub)
                    lock (_subscriptions) _subscriptions.Add(sub);
            }
            catch (Exception)
            {
                // Beklenmeyen bir sürüm farkı: dinlemiyoruz, uygulama etkilenmez.
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }

        private void OnRequest(object? payload)
        {
            if (payload == null || _disposed || _failures > 10) return;
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref _lastSample);
            if (now - last < SampleTicks || Interlocked.CompareExchange(ref _lastSample, now, last) != last) return;
            try
            {
                var context = _contextGetters.GetOrAdd(payload.GetType(), ContextGetter)?.Invoke(payload);
                var request = Member(context, "Request");
                var hostString = Member(request, "Host");      // HostString
                Add(Member(hostString, "Host") as string);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _failures);         // sürekli hata veriyorsa bırakırız
            }
        }

        // Olayın içeriği: eski sürümlerde { httpContext, timestamp } anonim nesnesi (özellik), yenilerinde httpContext alanı.
        private static Func<object, object?>? ContextGetter(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
            if (type.GetProperty("httpContext", flags) is PropertyInfo p) return o => p.GetValue(o);
            if (type.GetField("httpContext", flags) is FieldInfo f) return o => f.GetValue(o);
            return null;
        }

        private static object? Member(object? target, string name) =>
            target?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

        public void Dispose()
        {
            _disposed = true;
            lock (_subscriptions)
            {
                foreach (var s in _subscriptions)
                    try { s.Dispose(); } catch (Exception) { /* zaten kapalı */ }
                _subscriptions.Clear();
            }
        }

        private sealed class EventObserver : IObserver<KeyValuePair<string, object>>
        {
            private readonly ServedHosts _owner;
            public EventObserver(ServedHosts owner) => _owner = owner;
            public void OnNext(KeyValuePair<string, object> e)
            {
                if (e.Key == BeginRequest) _owner.OnRequest(e.Value);
            }
            public void OnError(Exception error) { }
            public void OnCompleted() { }
        }
    }
}
