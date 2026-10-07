namespace ConnectivityProbe.Monitor;

/// <summary>Bildirim kuralının türü: durum (başlar ve biter) ya da tek seferlik olay.</summary>
public enum RuleKind { Condition, Event }

/// <summary>Bildirimin önemi (Teams kartının rengi).</summary>
public enum Severity { Info, Medium, High, Critical }

/// <param name="Code">Kural kodu (tanımlarda saklanır).</param>
/// <param name="Category">app | connections | certificate | resources | deploy | options</param>
/// <param name="Default">Uygulama ilk kaydolduğunda işaretli gelir.</param>
public sealed record NotifyRule(string Code, string Category, RuleKind Kind, Severity Severity, bool Default);

/// <summary>
/// Bildirim kuralları kataloğu. Her uygulama bu kurallardan hangilerinin bildirim üreteceğini kendisi seçer (kişiden bağımsız);
/// uygulamaya abone olan herkes seçili kurallar için bildirim alır. Doğrulama süreleri ve eşikler NotificationEngine'dedir.
/// Açıklama metinleri arayüzde (i18n.js, "rule.&lt;kod&gt;") ve Teams mesajlarında (NotificationText) bulunur.
/// </summary>
public static class NotifyRules
{
    public static readonly IReadOnlyList<NotifyRule> All = new[]
    {
        // Uygulama ve pod'lar
        new NotifyRule("appDown", "app", RuleKind.Condition, Severity.Critical, true),
        new NotifyRule("podMissing", "app", RuleKind.Condition, Severity.High, true),
        new NotifyRule("crashLoop", "app", RuleKind.Condition, Severity.High, true),
        new NotifyRule("podRestart", "app", RuleKind.Event, Severity.Medium, false),
        // Bağlantılar
        new NotifyRule("connDown", "connections", RuleKind.Condition, Severity.Critical, true),
        new NotifyRule("connPartial", "connections", RuleKind.Condition, Severity.High, true),
        new NotifyRule("connSlow", "connections", RuleKind.Condition, Severity.Medium, false),
        new NotifyRule("ipChanged", "connections", RuleKind.Event, Severity.Info, false),
        // Sertifika
        new NotifyRule("certExpiring", "certificate", RuleKind.Event, Severity.Medium, true),
        new NotifyRule("certInvalid", "certificate", RuleKind.Condition, Severity.Critical, true),
        // Kaynaklar
        new NotifyRule("memHigh", "resources", RuleKind.Condition, Severity.High, true),
        new NotifyRule("throttled", "resources", RuleKind.Condition, Severity.Medium, false),
        new NotifyRule("portsHigh", "resources", RuleKind.Condition, Severity.High, false),
        // Sürüm / deploy
        new NotifyRule("deployDone", "deploy", RuleKind.Event, Severity.Info, false),
        new NotifyRule("deployStuck", "deploy", RuleKind.Condition, Severity.Medium, false),
        // Seçenek: durum düzelince de haber ver
        new NotifyRule("resolved", "options", RuleKind.Event, Severity.Info, true),
    };

    public static readonly IReadOnlyDictionary<string, NotifyRule> ByCode = All.ToDictionary(r => r.Code);

    /// <summary>Uygulama ilk kaydolduğunda seçili gelen kurallar.</summary>
    public static List<string> Defaults() => All.Where(r => r.Default).Select(r => r.Code).ToList();

    /// <summary>Bilinmeyen kodları atar, sırayı katalog sırasına getirir.</summary>
    public static List<string> Normalize(IEnumerable<string>? codes)
    {
        var set = (codes ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.Ordinal);
        return All.Where(r => set.Contains(r.Code)).Select(r => r.Code).ToList();
    }
}

/// <summary>
/// Bildirim alan kişi. "Bana haber ver" ile kaydolur; bildirimin nereye gideceği Monitor'ün moduna göre değişir
/// (bkz. MonitorOptions.NotifyMode):
/// <list type="bullet">
/// <item>microsoft / email: merkezi iş akışı, kişinin <see cref="Email"/> adresine özel mesaj gönderir.</item>
/// <item>webhook: kişinin kendi iş akışının adresi (<see cref="WebhookUrl"/>). Gizli tutulur; arayüze geri gönderilmez.</item>
/// </list>
/// </summary>
public sealed class PersonDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Teams'teki e-posta / kullanıcı adı (merkezi iş akışı bu adrese gönderir).</summary>
    public string Email { get; set; } = "";
    /// <summary>Kişinin kendi Teams Workflows iş akışının adresi (yalnızca "webhook" modunda).</summary>
    public string WebhookUrl { get; set; } = "";
    /// <summary>Kaydın kaynağı: microsoft (Entra ID girişi) | email | webhook.</summary>
    public string Source { get; set; } = "webhook";
    /// <summary>Mesajların dili: tr | en (kayıt olurken arayüzde seçili olan).</summary>
    public string Lang { get; set; } = "tr";
    /// <summary>Mesajdaki "Detayı aç" bağlantısı için Monitor'ün adresi (kişinin kayıt olduğu adres).</summary>
    public string MonitorUrl { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}
