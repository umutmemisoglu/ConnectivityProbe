namespace ConnectivityProbe.Monitor;

/// <summary>
/// Arayüze dönen hata mesajlarının dili. Arayüz seçili dili her istekte "X-Lang" başlığıyla (giriş sayfası "cp-lang"
/// çereziyle) bildirir; varsayılan Türkçe.
/// </summary>
public static class Lang
{
    public const string Header = "X-Lang";
    public const string Cookie = "cp-lang";

    private static readonly AsyncLocal<bool> English = new();

    /// <summary>İsteğin dilini belirler (istek başında bir kez çağrılır).</summary>
    public static void Set(HttpContext ctx)
    {
        var lang = ctx.Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(lang)) lang = ctx.Request.Cookies[Cookie] ?? "";
        English.Value = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Seçili dile göre Türkçe ya da İngilizce metni döner.</summary>
    public static string T(string tr, string en) => English.Value ? en : tr;
}
