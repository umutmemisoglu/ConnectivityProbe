using System.Globalization;

namespace ConnectivityProbe.Monitor;

/// <summary>
/// Bildirimlerin Teams mesajı (Adaptive Card): kişinin dilinde, uygulama başına tek kart; her gelişme bir satır ve
/// "Detayı aç" düğmesi uygulamanın Monitor'deki detay sayfasına gider.
/// </summary>
public static class NotificationText
{
    private static readonly Dictionary<string, (string Tr, string En)> Titles = new()
    {
        ["appDown"] = ("Uygulamaya erişilemiyor", "Application unreachable"),
        ["podMissing"] = ("Pod eksik", "Pod missing"),
        ["crashLoop"] = ("Sürekli yeniden başlama", "Crash loop"),
        ["podRestart"] = ("Pod yeniden başladı", "Pod restarted"),
        ["connDown"] = ("Bağlantı koptu", "Connection down"),
        ["connPartial"] = ("Bağlantı kısmen koptu", "Connection partly down"),
        ["connSlow"] = ("Bağlantı yavaş", "Connection slow"),
        ["ipChanged"] = ("IP adresi değişti", "IP address changed"),
        ["certExpiring"] = ("Sertifika bitiyor", "Certificate expiring"),
        ["certInvalid"] = ("Sertifika geçersiz", "Certificate invalid"),
        ["memHigh"] = ("Bellek limite yakın", "Memory close to the limit"),
        ["throttled"] = ("CPU yavaşlatılıyor", "CPU throttled"),
        ["portsHigh"] = ("Port tükeniyor", "Running out of ports"),
        ["deployDone"] = ("Yeni sürüm yayında", "New version deployed"),
        ["deployStuck"] = ("Deploy takıldı", "Deploy stuck"),
    };

    public static string Title(string rule, bool en) => Titles.TryGetValue(rule, out var t) ? (en ? t.En : t.Tr) : rule;

    /// <summary>Bir gelişmenin tek satırlık açıklaması.</summary>
    public static string Line(NotifyEvent e, bool en)
    {
        string D(string key) => e.Data.TryGetValue(key, out var v) ? v : "";
        string Err() => string.IsNullOrEmpty(D("error")) ? "" : (en ? " Error: " : " Hata: ") + D("error");
        var s = e.Subject;

        if (e.Kind == NotifyEventKind.Resolved)
            return (en ? $"✅ Resolved: {Title(e.Rule, true)} – {s}" : $"✅ Düzeldi: {Title(e.Rule, false)} – {s}")
                   + (e.SinceUtc is { } since ? (en ? $" (lasted {Duration(e.AtUtc - since, true)})." : $" ({Duration(e.AtUtc - since, false)} sürdü).") : ".");
        if (e.Kind == NotifyEventKind.Flapping)
            return en
                ? $"⚠ Unstable: {Title(e.Rule, true)} – {s} opened and closed several times in the last hour. No new messages for this for an hour."
                : $"⚠ Kararsız: {Title(e.Rule, false)} – {s} son bir saatte defalarca açılıp kapandı. Bir saat boyunca bunun için yeni mesaj gönderilmeyecek.";

        var icon = e.Severity switch { Severity.Critical => "🔴", Severity.High => "🟠", Severity.Medium => "🟡", _ => "🔵" };
        var text = e.Rule switch
        {
            "appDown" => en ? "Application unreachable: no pod is reporting." : "Uygulamaya erişilemiyor: hiçbir pod bildirim göndermiyor.",
            "podMissing" => en ? $"Pod missing: {s} has stopped reporting." : $"Pod eksik: {s} bildirim göndermiyor.",
            "crashLoop" => en ? $"{s} keeps restarting ({D("count")} times in the last 30 minutes)." : $"{s} sürekli yeniden başlıyor (son 30 dakikada {D("count")} kez).",
            "podRestart" => (en ? $"{s} restarted." : $"{s} yeniden başladı.")
                            + (D("oom") == "1" ? (en ? " A process was killed for running out of memory (OOM)." : " Bellek yetmediği için süreç öldürüldü (OOM).") : ""),
            "connDown" => (en ? $"Connection down: {s} ({D("target")}) is unreachable from every pod ({D("failed")})." : $"Bağlantı koptu: {s} ({D("target")}) hiçbir pod'dan erişilemiyor ({D("failed")}).") + Err(),
            "connPartial" => (en ? $"Connection partly down: {s} ({D("target")}) is unreachable from {D("failed")} pods: {D("pods")}." : $"Bağlantı kısmen koptu: {s} ({D("target")}) {D("failed")} pod'dan erişilemiyor: {D("pods")}.") + Err(),
            "connSlow" => en ? $"{s} ({D("target")}) is slow: usually {D("baseline")} ms, now {D("now")} ms." : $"{s} ({D("target")}) yavaşladı: olağan {D("baseline")} ms, şu an {D("now")} ms.",
            "ipChanged" => en ? $"{s} ({D("target")}) IP address changed: {D("from")} → {D("to")}." : $"{s} ({D("target")}) IP adresi değişti: {D("from")} → {D("to")}.",
            "certExpiring" => en ? $"The certificate of {s} ({D("target")}) expires in {D("days")} days ({Date(D("end"), true)})." : $"{s} ({D("target")}) sertifikası {D("days")} gün sonra bitiyor ({Date(D("end"), false)}).",
            "certInvalid" => (en ? $"The certificate of {s} ({D("target")}) is invalid ({D("failed")} pods)." : $"{s} ({D("target")}) sertifikası geçersiz ({D("failed")} pod).") + Err(),
            "memHigh" => en ? $"{s} memory is close to the limit: {D("percent")}%." : $"{s} belleği limite yakın: %{D("percent")}.",
            "throttled" => en ? $"{s} is throttled by the CPU limit: {D("percent")}%." : $"{s} CPU limiti yüzünden yavaşlatılıyor: %{D("percent")}.",
            "portsHigh" => en ? $"{s} is running out of TCP ports: {D("percent")}%." : $"{s} TCP portları tükeniyor: %{D("percent")}.",
            "deployDone" => en ? $"New version deployed: {D("from")} → {D("to")} ({D("pods")} pods)." : $"Yeni sürüm yayında: {D("from")} → {D("to")} ({D("pods")} pod).",
            "deployStuck" => en ? $"Deploy stuck: different versions have been running together for 30 minutes ({D("versions")})." : $"Deploy takıldı: 30 dakikadır farklı sürümler birlikte çalışıyor ({D("versions")}).",
            _ => $"{Title(e.Rule, en)}: {s}"
        };
        return icon + " " + text;
    }

    /// <summary>Bildirim kartı (Adaptive Card içeriği; gönderim zarfı Delivery.For'da).</summary>
    public static Dictionary<string, object?> Card(NotifyBatch batch, PersonDefinition person, DateTime now)
    {
        var en = person.Lang == "en";
        var worst = batch.Events.Where(e => e.Kind != NotifyEventKind.Resolved).Select(e => e.Severity).DefaultIfEmpty(Severity.Info).Max();
        var allResolved = batch.Events.All(e => e.Kind == NotifyEventKind.Resolved);
        var style = allResolved ? "good" : worst switch { Severity.Critical or Severity.High => "attention", Severity.Medium => "warning", _ => "accent" };
        var url = string.IsNullOrEmpty(person.MonitorUrl) ? null : person.MonitorUrl.TrimEnd('/') + "/?app=" + Uri.EscapeDataString(batch.AppId);

        var body = new List<object>
        {
            new
            {
                type = "Container", style, bleed = true,
                items = new object[]
                {
                    new { type = "TextBlock", text = batch.AppName, weight = "Bolder", size = "Medium", wrap = true },
                    new { type = "TextBlock", text = "Connectivity Monitor · " + Date(now.ToString("O"), en), isSubtle = true, spacing = "None", size = "Small" }
                }
            }
        };
        body.AddRange(batch.Events.Select(e => (object)new { type = "TextBlock", text = Line(e, en), wrap = true, spacing = "Small" }));

        var card = new Dictionary<string, object?>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
            ["actions"] = url == null ? Array.Empty<object>() : new object[] { new { type = "Action.OpenUrl", title = en ? "Open details" : "Detayı aç", url } }
        };
        return card;
    }

    /// <summary>Kayıt sırasında gönderilen deneme mesajı: bildirimlerin bu sohbete geleceğini gösterir.</summary>
    public static Dictionary<string, object?> Welcome(PersonDefinition person)
    {
        var en = person.Lang == "en";
        var text = en
            ? $"Hello {person.Name}. Connectivity Monitor notifications for the applications you subscribe to will arrive in this chat."
            : $"Merhaba {person.Name}. Abone olduğun uygulamaların Connectivity Monitor bildirimleri bu sohbete gelecek.";
        var card = new Dictionary<string, object?>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = new object[] { new { type = "TextBlock", text = "🔔 " + text, wrap = true } }
        };
        return card;
    }

    private static string Duration(TimeSpan d, bool en)
    {
        if (d.TotalMinutes < 1) return en ? "under a minute" : "1 dakikadan az";
        if (d.TotalHours < 1) return en ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalMinutes} dk";
        if (d.TotalDays < 1) return en ? $"{(int)d.TotalHours} h {d.Minutes} min" : $"{(int)d.TotalHours} sa {d.Minutes} dk";
        return en ? $"{(int)d.TotalDays} days" : $"{(int)d.TotalDays} gün";
    }

    // Monitor'ün çalıştığı makinenin saatine göre tarih.
    private static string Date(string iso, bool en) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d.ToLocalTime().ToString(en ? "yyyy-MM-dd HH:mm" : "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
            : iso;
}

/// <summary>
/// Bir kişiye giden mesajın adresi ve zarfı:
/// <list type="bullet">
/// <item>Merkezi iş akışı (microsoft / email modu): <c>{ recipient, card }</c> merkezi adrese; iş akışı kartı alıcıya Flow bot ile
/// özel mesaj olarak gönderir (Power Automate: "Post card in a chat or channel", Recipient = triggerBody()?['recipient'],
/// Adaptive Card = string(triggerBody()?['card'])).</item>
/// <item>Kişinin kendi iş akışı (webhook modu): Teams'in "Send webhook alerts to a chat" şablonunun beklediği mesaj zarfı.</item>
/// </list>
/// </summary>
public static class Delivery
{
    public static (string Url, object Payload)? For(PersonDefinition person, NotificationOptions options, Dictionary<string, object?> card)
    {
        if (options.Central && !string.IsNullOrWhiteSpace(person.Email))
            return (options.WorkflowUrl!.Trim(), new { recipient = person.Email, card });
        if (!string.IsNullOrWhiteSpace(person.WebhookUrl))
            return (person.WebhookUrl, new
            {
                type = "message",
                attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive", contentUrl = (string?)null, content = card } }
            });
        return null; // kişiye ulaşılacak bir yol yok (ör. mod değişti, e-posta yok)
    }
}
