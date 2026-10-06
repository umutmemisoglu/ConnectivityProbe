using System.Text.Json;

namespace ConnectivityProbe.Monitor;

/// <summary>İzlenen uygulamanın ConnectivityProbe uçlarını çağırıp sonucu okur.</summary>
public static class RemoteProbe
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Uygulamanın /connectivity-probe/discover ucunu çağırır: uygulama kendi içinden hedefe önce telnet atar; hedef de
    /// ConnectivityProbe kullanıyorsa (<paramref name="usesConnectivityProbe"/>) ayrıca hedefin pod'larını keşfeder.
    /// İstek her seferinde yeni bağlantıyla gider ki load balancer farklı pod seçebilsin; yanıt testi yapan pod'u da içerir.
    /// </summary>
    /// <param name="baseUrl">İzlenen uygulamanın adresi.</param>
    /// <param name="probePath">Uygulamadaki probe yolu (ör. /connectivity-probe).</param>
    /// <param name="host">Test edilecek hedef (ad, IP, ad:port veya URL).</param>
    /// <param name="port">Hedef port; host içinde varsa null olabilir.</param>
    /// <param name="timeout">Uygulamanın hedefe bağlanırken kullanacağı zaman aşımı.</param>
    /// <param name="accessKey">Ortak erişim anahtarı; null ise gönderilmez.</param>
    /// <param name="usesConnectivityProbe">Hedef de ConnectivityProbe kullanıyor mu (pod keşfi yapılsın mı).</param>
    /// <param name="attempts">Pod keşfinde hedefe en fazla kaç identity isteği atılacağı (adaptive; genelde çok daha azında durur).</param>
    /// <param name="confidence">Hedef pod sayımı için istenen güven.</param>
    public static Task<(DiscoverReport? Report, string? Error)> CallDiscoverAsync(
        string baseUrl, string probePath, string host, int? port, TimeSpan timeout, string? accessKey,
        bool usesConnectivityProbe, int attempts, double confidence, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + probePath.TrimEnd('/') + "/discover"
                  + "?host=" + Uri.EscapeDataString(host) + (port.HasValue ? "&port=" + port.Value : "")
                  + "&timeoutMs=" + (int)timeout.TotalMilliseconds;
        if (usesConnectivityProbe)
            url += "&usesConnectivityProbe=true&attempts=" + attempts
                   + "&confidence=" + confidence.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Uygulama en kötü durumda önce DNS'i, sonra bağlantıyı bekler (~2 x timeout); pod keşfi varsa uygulamada
        // MaxRequestDuration ile sınırlı ek süre (varsayılan 60 sn). Bizim HTTP zaman aşımımız bundan uzun olmalı; yoksa
        // erişilemeyen bir hedefte "hedefe erişilemiyor" yerine "uygulama zaman aşımı" görürdük.
        var httpTimeout = timeout * 2 + TimeSpan.FromSeconds(usesConnectivityProbe ? 70 : 10);
        return GetAsync<DiscoverReport>(url, httpTimeout, accessKey, null, ct);
    }

    private static async Task<(T? Report, string? Error)> GetAsync<T>(
        string url, TimeSpan httpTimeout, string? accessKey, string? targetAccessKey, CancellationToken ct) where T : class
    {
        try
        {
            // step 1: Her çağrıda yeni handler + "Connection: close"; bağlantı yeniden kullanılırsa hep aynı pod'a düşeriz.
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = httpTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.ConnectionClose = true;
            if (!string.IsNullOrEmpty(accessKey))
                request.Headers.TryAddWithoutValidation(ConnectivityProbeOptions.AccessKeyHeader, accessKey);
            if (!string.IsNullOrEmpty(targetAccessKey))
                request.Headers.TryAddWithoutValidation(ConnectivityProbeOptions.TargetAccessKeyHeader, targetAccessKey);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // step 2: Hata yanıtlarında (400 geçersiz hedef, 401 anahtar, 403 izin listesi...) mesajı okunur hale getiriyoruz.
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}: {ExtractError(body)}");

            // step 3: Başarılıysa raporu nesneye çeviriyoruz.
            var report = JsonSerializer.Deserialize<T>(body, Json);
            return report == null ? (null, "Boş yanıt") : (report, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return (null, "Uygulama zaman aşımına uğradı");
        }
        catch (Exception ex)
        {
            return (null, ex.InnerException?.Message ?? ex.Message);
        }
    }

    // {"error":"..."} gövdesinden mesajı alır; olmazsa gövdenin ilk kısmını döner.
    private static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? body;
        }
        catch (JsonException)
        {
            // JSON değil (ör. HTML hata sayfası), aşağıda ham metni kırpıp döneceğiz.
        }

        return body.Length > 120 ? body[..120] : body;
    }
}
