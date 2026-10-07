using System.Net;
using System.Security.Claims;

namespace ConnectivityProbe.Monitor;

/// <summary>
/// Microsoft (Entra ID) ile giriş: "Bana haber ver"e tıklayan kişiyi tanımak için. Giriş sonrası kişi (adı, e-postası) bildirim
/// alanlara kendiliğinden eklenir ve tıkladığı uygulamaya abone edilir.
/// </summary>
public static class MicrosoftLogin
{
    /// <summary>Bildirim kişisinin oturum çerezi (admin girişinden ayrı).</summary>
    public const string PersonScheme = "cpperson";
    /// <summary>Microsoft giriş akışı (OpenID Connect).</summary>
    public const string MicrosoftScheme = "microsoft";

    /// <summary>Girişten gelen kimlik: Entra ID nesne kimliği, ad ve e-posta (yoksa kullanıcı adı).</summary>
    public static (string Id, string Name, string Email)? PersonOf(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;
        string? Claim(string type) => user.FindFirst(type)?.Value;
        var oid = Claim("oid") ?? Claim("sub");
        if (string.IsNullOrEmpty(oid)) return null;
        var email = Claim("email") ?? Claim("preferred_username") ?? Claim("upn") ?? "";
        var name = Claim("name") ?? email;
        return ("ms-" + oid, name, email);
    }

    /// <summary>
    /// Girişten dönüş adresi yalnızca bu sitedeki bir yol olabilir ("/..."); başka siteye yönlendirme yapılmaz.
    /// </summary>
    public static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    /// <summary>
    /// Sayfanın kendisinden yapılan yönlendirme. Microsoft'tan dönüş zinciri site dışından başladığı için doğrudan 302 ile
    /// dönülürse tarayıcı arayüzün (SameSite=Strict) admin çerezini göndermez; küçük bir sayfa üzerinden dönmek bunu önler.
    /// </summary>
    public static IResult PageRedirect(string url)
    {
        var target = WebUtility.HtmlEncode(SafeReturnUrl(url));
        return Results.Content(
            $"<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url={target}\"><title>Connectivity Monitor</title>",
            "text/html; charset=utf-8");
    }

    /// <summary>Giriş başlatılamadığında gösterilen sayfa (ana sayfaya ve Ayarlar'a bağlantıyla).</summary>
    public static IResult ErrorPage(string message) => Results.Content(
        $"<!doctype html><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Connectivity Monitor</title>" +
        "<body style=\"font-family:system-ui,sans-serif;background:#141414;color:#e5e5e5;max-width:640px;margin:60px auto;padding:0 16px;line-height:1.5\">" +
        $"<p>{WebUtility.HtmlEncode(message)}</p><p><a style=\"color:#e50914\" href=\"/?settings=notifications\">{WebUtility.HtmlEncode(Lang.T("Ayarlar'a git", "Go to Settings"))}</a> · " +
        $"<a style=\"color:#e5e5e5\" href=\"/\">{WebUtility.HtmlEncode(Lang.T("Ana sayfa", "Home"))}</a></p></body>",
        "text/html; charset=utf-8", statusCode: 502);
}

/// <summary>
/// Microsoft girişinin bilgileri (tenant, client) Ayarlar'dan canlı okunur: arayüzde kaydedilince Monitor'ü yeniden başlatmak
/// gerekmez (SettingsStore.Changed giriş yapılandırmasını yeniler). Kurulum yapılmamışken geçici değerler kullanılır; o
/// durumda "Bana haber ver" girişe hiç gitmez.
/// </summary>
public sealed class MicrosoftLoginOptions : Microsoft.Extensions.Options.IConfigureNamedOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>
{
    private readonly SettingsStore _settings;
    public MicrosoftLoginOptions(SettingsStore settings) => _settings = settings;

    public void Configure(string? name, Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions options)
    {
        if (name != MicrosoftLogin.MicrosoftScheme) return;
        var s = _settings.Current;
        options.Authority = $"https://login.microsoftonline.com/{(s.SignInConfigured ? s.TenantId : "common")}/v2.0";
        options.ClientId = s.SignInConfigured ? s.ClientId : "not-configured";
    }

    public void Configure(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions options) =>
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
}
