namespace ConnectivityProbe.Monitor;

/// <summary>Bağlantı havuzunun kuralları.</summary>
public static class ConnectionRules
{
    /// <summary>
    /// Bağlantının test edilen adresi: "host:port" (küçük harf, sondaki nokta atılır). Testler host ve port üzerinden yapıldığı
    /// için "sql01:1433" ile "SQL01" + 1433, "https://github.com/" ile "https://github.com/login" aynı adrestir.
    /// Adres geçersizse null.
    /// </summary>
    public static string? EndpointKey(string? host, int? port) =>
        ProbeTarget.TryParse(host, port?.ToString(), out var h, out var p, out _, out _) ? h.Trim().TrimEnd('.').ToLowerInvariant() + ":" + p : null;

    /// <summary>Havuzda aynı adresi test eden başka bir bağlantı (yoksa null). exceptId: düzenlenen bağlantının kendisi.</summary>
    public static ConnectionDefinition? DuplicateOf(DefinitionData d, string? host, int? port, string? exceptId)
    {
        var key = EndpointKey(host, port);
        return key == null ? null : d.Connections.FirstOrDefault(c => c.Id != exceptId && EndpointKey(c.Host, c.Port) == key);
    }
}
