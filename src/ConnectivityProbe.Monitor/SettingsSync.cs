namespace ConnectivityProbe.Monitor;

/// <summary>
/// Pod'un ayarlarından bulduğu bağımlılıkları uygulamanın tanımlarına uygular.
/// <list type="bullet">
/// <item>Yeni ayar: havuzda aynı adrese giden bağlantı varsa o kullanılır, yoksa bağlantı oluşturulur ("⚙ ayarlardan") ve
/// uygulamaya atanır.</item>
/// <item>Adresi değişen ayar: eski bağlantı uygulamadan çıkarılır, yenisi atanır.</item>
/// <item>Kaldırılan ayar: bağlantı uygulamadan çıkarılır.</item>
/// <item>Ayarlardan oluşturulmuş ve artık kimsenin kullanmadığı bağlantılar havuzdan silinir; elle eklenmişlere dokunulmaz.</item>
/// <item>Kullanıcının kaldırdığı / yok saydığı ayarlar bir daha eklenmez; düşük güvenliler öneri olarak bekler.</item>
/// </list>
/// </summary>
public static class SettingsSync
{
    public const string Origin = "settings";
    private const int MaxChanges = 100;

    /// <summary>Pod'dan gelen sonucu uygular; değişiklik sayısını döner (0: değişen bir şey yok).</summary>
    public static int Apply(DefinitionData d, AppDefinition app, DiscoveredSettings s, DateTime now)
    {
        var st = app.Discovery ??= new SettingsState();
        st.ReadAtUtc = s.ReadAtUtc;
        st.Error = s.Error;
        if (s.Signature == null || st.Signature == s.Signature) return 0;   // hiç okunamadı ya da değişmedi

        var before = st.Links.ToDictionary(l => l.Key);
        var links = new List<SettingsLink>();
        var suggestions = new List<SettingsLink>();
        var changes = 0;

        foreach (var e in s.Endpoints.GroupBy(x => x.Key).Select(g => g.First()))
        {
            if (st.IgnoredKeys.Contains(e.Key)) continue;
            var link = new SettingsLink { Key = e.Key, Kind = e.Kind, Host = e.Host, Port = e.Port, Tls = e.Tls, Verified = e.Verified };
            if (e.Confidence != "high" && !st.AcceptedKeys.Contains(e.Key))
            {
                suggestions.Add(link);
                continue;
            }

            before.TryGetValue(e.Key, out var old);
            var oldConnection = old?.ConnectionId is { } oldId ? d.Connections.FirstOrDefault(c => c.Id == oldId) : null;
            if (old != null && oldConnection != null && SameEndpoint(old, link))
            {
                link.ConnectionId = oldConnection.Id;   // aynı adres: tür / doğrulama bilgisi güncellenir
                link.PreAttached = old.PreAttached;
                if (!app.ConnectionIds.Contains(oldConnection.Id)) app.ConnectionIds.Add(oldConnection.Id);
            }
            else
            {
                var conn = FindOrCreate(d, link);
                link.ConnectionId = conn.Id;
                link.PreAttached = app.ConnectionIds.Contains(conn.Id);
                if (!app.ConnectionIds.Contains(conn.Id)) app.ConnectionIds.Add(conn.Id);
                Record(st, now, old == null ? "added" : "changed", e.Key, old == null ? null : Describe(old), Describe(link));
                changes++;
            }
            links.Add(link);
        }

        // Artık ayarlarda olmayanlar (ya da yalnızca öneriye düşenler): uygulamadan çıkarılır.
        foreach (var old in before.Values.Where(o => links.All(l => l.Key != o.Key)))
        {
            Record(st, now, "removed", old.Key, Describe(old), null);
            changes++;
        }
        // Elle atanmış olanlar (PreAttached) uygulamada kalır.
        var previousConnections = before.Values.Where(l => !l.PreAttached).Select(l => l.ConnectionId)
            .Where(id => id != null).Select(id => id!).Distinct().ToList();

        st.Links = links;
        st.Suggestions = suggestions;
        st.Signature = s.Signature;
        st.Verified = s.Verified;
        if (changes > 0) st.UpdatedAtUtc = now;

        // Önceki bağlantılardan artık bir ayara karşılık gelmeyenleri uygulamadan çıkar (elle eklenmiş başka ayar yoksa).
        foreach (var id in previousConnections.Where(id => links.All(l => l.ConnectionId != id)))
            Detach(d, app, id);
        return changes;
    }

    /// <summary>
    /// Kullanıcı bir ayarı yok saydı ya da ayarlardan gelen bağlantıyı uygulamadan çıkardı: bir daha eklenmez, bağlantısı
    /// uygulamadan çıkarılır. Döner: etkilenen ayar var mıydı.
    /// </summary>
    public static bool Ignore(DefinitionData d, AppDefinition app, string key, DateTime now)
    {
        var st = app.Discovery ??= new SettingsState();
        if (!st.IgnoredKeys.Contains(key)) st.IgnoredKeys.Add(key);
        st.AcceptedKeys.Remove(key);
        st.Suggestions.RemoveAll(l => l.Key == key);
        var link = st.Links.FirstOrDefault(l => l.Key == key);
        if (link == null) return false;
        st.Links.Remove(link);
        if (!link.PreAttached && link.ConnectionId is { } id && st.Links.All(l => l.ConnectionId != id)) Detach(d, app, id);
        Record(st, now, "ignored", key, Describe(link), null);
        return true;
    }

    /// <summary>Bağlantı uygulamadan elle çıkarıldı: ona karşılık gelen ayarlar yok sayılır.</summary>
    public static void ConnectionDetached(DefinitionData d, AppDefinition app, string connectionId, DateTime now)
    {
        if (app.Discovery == null) return;
        foreach (var key in app.Discovery.Links.Where(l => l.ConnectionId == connectionId).Select(l => l.Key).ToList())
            Ignore(d, app, key, now);
    }

    /// <summary>Öneriyi onayla ya da yok sayılanı geri al: pod'un bir sonraki bildiriminde yeniden uygulanır.</summary>
    public static void Accept(AppDefinition app, string key)
    {
        var st = app.Discovery ??= new SettingsState();
        st.IgnoredKeys.Remove(key);
        if (!st.AcceptedKeys.Contains(key)) st.AcceptedKeys.Add(key);
        st.Signature = null;
    }

    public static void Restore(AppDefinition app, string key)
    {
        var st = app.Discovery ??= new SettingsState();
        st.IgnoredKeys.Remove(key);
        st.Signature = null;
    }

    // ------------------------------------------------------------------ yardımcılar

    private static bool SameEndpoint(SettingsLink a, SettingsLink b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port && a.Tls == b.Tls;

    private static string Describe(SettingsLink l) => $"{l.Kind} {l.Host}:{l.Port}";

    private static void Record(SettingsState st, DateTime now, string type, string key, string? from, string? to)
    {
        st.Changes.Add(new SettingsChange { AtUtc = now, Type = type, Key = key, From = from, To = to });
        if (st.Changes.Count > MaxChanges) st.Changes.RemoveRange(0, st.Changes.Count - MaxChanges);
    }

    // Havuzda aynı adrese giden bağlantı (elle eklenmiş olsa da) kullanılır; yoksa oluşturulur.
    private static ConnectionDefinition FindOrCreate(DefinitionData d, SettingsLink link)
    {
        if (ConnectionRules.DuplicateOf(d, link.Host, link.Port, exceptId: null) is { } existing) return existing;
        var name = $"{Label(link.Kind)} · {link.Host}";
        if (d.Connections.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) name += $":{link.Port}";
        var conn = new ConnectionDefinition
        {
            Id = Guid.NewGuid().ToString("N"), Name = name, Host = link.Host, Port = link.Port,
            TlsCheck = link.Tls ? "on" : null, Origin = Origin
        };
        d.Connections.Add(conn);
        return conn;
    }

    // Bağlantıyı uygulamadan çıkarır; ayarlardan oluşturulmuşsa ve artık hiçbir uygulama kullanmıyorsa havuzdan da siler.
    private static void Detach(DefinitionData d, AppDefinition app, string connectionId)
    {
        app.ConnectionIds.Remove(connectionId);
        var conn = d.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (conn?.Origin == Origin && d.Apps.All(a => !a.ConnectionIds.Contains(connectionId)))
            d.Connections.Remove(conn);
    }

    /// <summary>Türün görünen adı (havuzdaki bağlantı adı için).</summary>
    public static string Label(string kind) => kind switch
    {
        "postgresql" => "PostgreSQL", "sqlserver" => "SQL Server", "mysql" => "MySQL", "oracle" => "Oracle",
        "mongodb" => "MongoDB", "redis" => "Redis", "rabbitmq" => "RabbitMQ", "kafka" => "Kafka",
        "elasticsearch" => "Elasticsearch", "kibana" => "Kibana", "smtp" => "SMTP", "ldap" => "LDAP", "nats" => "NATS",
        "memcached" => "Memcached", "zookeeper" => "ZooKeeper", "consul" => "Consul", "ssh" => "SSH", "ftp" => "FTP",
        "http" => "HTTP", _ => "TCP"
    };
}
