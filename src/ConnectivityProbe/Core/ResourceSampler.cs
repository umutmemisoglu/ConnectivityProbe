using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ConnectivityProbe
{
    /// <summary>
    /// Pod'un kaynak kullanımı (her bildirimde gönderilir). Okunamayan değerler null kalır: ör. Kubernetes / Linux container dışında
    /// cgroup limitleri ve /proc bilgileri yoktur, yalnızca süreç ölçümleri gelir.
    /// </summary>
    public sealed class ResourceSample
    {
        public DateTime SampledAtUtc { get; set; }
        public int ProcessorCount { get; set; }

        /// <summary>Sürecin son ölçümden bu yana kullandığı CPU (çekirdek; 0,5 = yarım çekirdek). İlk ölçümde null.</summary>
        public double? CpuCores { get; set; }
        /// <summary>Container'ın CPU limiti (çekirdek; cgroup cpu.max). Limit yoksa null.</summary>
        public double? CpuLimitCores { get; set; }
        /// <summary>Son ölçümden bu yana CPU limiti yüzünden yavaşlatılan dönemlerin oranı (%; cgroup cpu.stat).</summary>
        public double? CpuThrottledPercent { get; set; }

        /// <summary>Sürecin fiziksel bellekte kapladığı alan.</summary>
        public long WorkingSetBytes { get; set; }
        public long PrivateBytes { get; set; }
        /// <summary>.NET yönetilen heap'i (GC).</summary>
        public long GcHeapBytes { get; set; }
        /// <summary>Container'ın bellek kullanımı ve limiti (cgroup memory.current / memory.max). Limit yoksa null.</summary>
        public long? MemoryBytes { get; set; }
        public long? MemoryLimitBytes { get; set; }
        /// <summary>Container'da bellek yetmediği için öldürülen süreç sayısı (cgroup memory.events oom_kill; birikimli).</summary>
        public long? OomKills { get; set; }

        /// <summary>GC toplama sayıları (süreç başından beri).</summary>
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }

        public int Threads { get; set; }
        /// <summary>ThreadPool'da meşgul worker thread sayısı ve üst sınırı. Üst sınıra yaklaşmak "thread pool starvation" demektir.</summary>
        public int ThreadPoolBusy { get; set; }
        public int ThreadPoolMax { get; set; }
        public int? Handles { get; set; }

        /// <summary>Pod'un ağ arayüzlerinden alınan / gönderilen toplam byte (/proc/net/dev, loopback hariç; birikimli).</summary>
        public long? NetRxBytes { get; set; }
        public long? NetTxBytes { get; set; }

        /// <summary>Pod'daki TCP soketleri (/proc/net/tcp, tcp6): kurulu, TIME_WAIT ve toplam.</summary>
        public int? TcpEstablished { get; set; }
        public int? TcpTimeWait { get; set; }
        public int? TcpTotal { get; set; }
        /// <summary>Giden bağlantılar için kullanılabilen yerel port sayısı (ip_local_port_range).</summary>
        public int? EphemeralPorts { get; set; }
    }

    /// <summary>
    /// Kaynak ölçümlerini toplar. CPU ve throttling iki ölçüm arasındaki farktan hesaplandığı için önceki değerleri tutar.
    /// Her bölüm ayrı korunur: biri okunamazsa diğerleri yine gelir, hiçbir koşulda hata fırlatmaz.
    /// </summary>
    internal sealed class ResourceSampler
    {
        internal static string CgroupDirectory =>
            Environment.GetEnvironmentVariable("CONNECTIVITYPROBE_CGROUP_DIR") ?? "/sys/fs/cgroup";
        internal static string ProcDirectory =>
            Environment.GetEnvironmentVariable("CONNECTIVITYPROBE_PROC_DIR") ?? "/proc";

        private TimeSpan? _lastCpu;
        private DateTime _lastAtUtc;
        private long? _lastPeriods, _lastThrottled;

        public ResourceSample Sample()
        {
            var now = DateTime.UtcNow;
            var s = new ResourceSample { SampledAtUtc = now, ProcessorCount = Environment.ProcessorCount };

            Try(() =>
            {
                using (var p = Process.GetCurrentProcess())
                {
                    var cpu = p.TotalProcessorTime;
                    if (_lastCpu != null && now > _lastAtUtc)
                        s.CpuCores = Math.Round(Math.Max(0, (cpu - _lastCpu.Value).TotalMilliseconds / (now - _lastAtUtc).TotalMilliseconds), 3);
                    _lastCpu = cpu;
                    _lastAtUtc = now;
                    s.WorkingSetBytes = p.WorkingSet64;
                    s.PrivateBytes = p.PrivateMemorySize64;
                    s.Threads = p.Threads.Count;
                    Try(() => s.Handles = p.HandleCount);
                }
            });

            Try(() =>
            {
                s.GcHeapBytes = GC.GetTotalMemory(false);
                s.Gen0Collections = GC.CollectionCount(0);
                s.Gen1Collections = GC.CollectionCount(1);
                s.Gen2Collections = GC.CollectionCount(2);
                ThreadPool.GetAvailableThreads(out var available, out _);
                ThreadPool.GetMaxThreads(out var max, out _);
                s.ThreadPoolMax = max;
                s.ThreadPoolBusy = Math.Max(0, max - available);
            });

            Try(() => ReadCgroup(CgroupDirectory, s));
            Try(() => ReadNetwork(ProcDirectory, s));
            Try(() => ReadTcp(ProcDirectory, s));
            return s;
        }

        // ------------------------------------------------------------------ cgroup (container limitleri)

        private void ReadCgroup(string root, ResourceSample s)
        {
            long? periods = null, throttled = null;

            if (File.Exists(Path.Combine(root, "cgroup.controllers")))
            {
                // cgroup v2 (güncel Kubernetes / Docker)
                s.MemoryBytes = ReadLong(Path.Combine(root, "memory.current"));
                s.MemoryLimitBytes = ReadLimit(Path.Combine(root, "memory.max"));
                s.OomKills = ReadKey(Path.Combine(root, "memory.events"), "oom_kill");

                // cpu.max: "<kota> <dönem>" ya da "max <dönem>"
                var cpuMax = ReadText(Path.Combine(root, "cpu.max"))?.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (cpuMax != null && cpuMax.Length == 2 && long.TryParse(cpuMax[0], out var quota) && long.TryParse(cpuMax[1], out var period) && period > 0)
                    s.CpuLimitCores = Math.Round((double)quota / period, 3);

                periods = ReadKey(Path.Combine(root, "cpu.stat"), "nr_periods");
                throttled = ReadKey(Path.Combine(root, "cpu.stat"), "nr_throttled");
            }
            else if (Directory.Exists(Path.Combine(root, "memory")))
            {
                // cgroup v1 (eski çekirdekler)
                s.MemoryBytes = ReadLong(Path.Combine(root, "memory", "memory.usage_in_bytes"));
                s.MemoryLimitBytes = ReadLimit(Path.Combine(root, "memory", "memory.limit_in_bytes"));
                s.OomKills = ReadKey(Path.Combine(root, "memory", "memory.oom_control"), "oom_kill");

                var cpuDir = Directory.Exists(Path.Combine(root, "cpu")) ? Path.Combine(root, "cpu") : Path.Combine(root, "cpu,cpuacct");
                var quota = ReadLong(Path.Combine(cpuDir, "cpu.cfs_quota_us"));
                var period = ReadLong(Path.Combine(cpuDir, "cpu.cfs_period_us"));
                if (quota > 0 && period > 0) s.CpuLimitCores = Math.Round((double)quota.Value / period.Value, 3);

                periods = ReadKey(Path.Combine(cpuDir, "cpu.stat"), "nr_periods");
                throttled = ReadKey(Path.Combine(cpuDir, "cpu.stat"), "nr_throttled");
            }

            // Throttling: son ölçümden bu yana CPU dönemlerinin yüzde kaçında limit yüzünden bekletildi.
            if (periods != null && throttled != null)
            {
                if (_lastPeriods != null && _lastThrottled != null && periods > _lastPeriods)
                    s.CpuThrottledPercent = Math.Round(100.0 * (throttled.Value - _lastThrottled.Value) / (periods.Value - _lastPeriods.Value), 1);
                _lastPeriods = periods;
                _lastThrottled = throttled;
            }
        }

        // ------------------------------------------------------------------ /proc (ağ)

        // /proc/net/dev: "  eth0: <rx bytes> ... (8 alan) <tx bytes> ..." (loopback hariç toplanır).
        internal static void ReadNetwork(string proc, ResourceSample s)
        {
            var path = Path.Combine(proc, "net", "dev");
            if (!File.Exists(path)) return;
            long rx = 0, tx = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var name = line.Substring(0, colon).Trim();
                if (name == "lo") continue;
                var fields = line.Substring(colon + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 9) continue;
                if (long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) rx += r;
                if (long.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)) tx += t;
            }
            s.NetRxBytes = rx;
            s.NetTxBytes = tx;
        }

        // /proc/net/tcp(6): 4. sütun bağlantı durumu (hex): 01 ESTABLISHED, 06 TIME_WAIT. Container'da yalnızca pod'un soketleri görünür.
        internal static void ReadTcp(string proc, ResourceSample s)
        {
            int established = 0, timeWait = 0, total = 0;
            bool any = false;
            foreach (var file in new[] { "tcp", "tcp6" })
            {
                var path = Path.Combine(proc, "net", file);
                if (!File.Exists(path)) continue;
                any = true;
                foreach (var line in File.ReadLines(path).Skip(1))
                {
                    var fields = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 4) continue;
                    total++;
                    if (fields[3] == "01") established++;
                    else if (fields[3] == "06") timeWait++;
                }
            }
            if (!any) return;
            s.TcpEstablished = established;
            s.TcpTimeWait = timeWait;
            s.TcpTotal = total;

            var range = ReadText(Path.Combine(proc, "sys", "net", "ipv4", "ip_local_port_range"))
                ?.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (range != null && range.Length == 2 && int.TryParse(range[0], out var low) && int.TryParse(range[1], out var high) && high >= low)
                s.EphemeralPorts = high - low + 1;
        }

        // ------------------------------------------------------------------ yardımcılar

        private static string? ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch (Exception) { return null; }
        }

        private static long? ReadLong(string path) =>
            long.TryParse(ReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (long?)null;

        // "max" veya v1'deki çok büyük değer (limit yok) -> null.
        private static long? ReadLimit(string path)
        {
            var v = ReadLong(path);
            return v == null || v.Value >= (1L << 60) ? null : v;
        }

        // "anahtar değer" satırlarından birini okur (cpu.stat, memory.events, memory.oom_control).
        private static long? ReadKey(string path, string key)
        {
            var text = ReadText(path);
            if (text == null) return null;
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == key && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            }
            return null;
        }

        private static void Try(Action action)
        {
            try { action(); }
            catch (Exception) { /* bu ölçüm bu platformda okunamıyor; diğerleri gönderilir */ }
        }
    }
}
