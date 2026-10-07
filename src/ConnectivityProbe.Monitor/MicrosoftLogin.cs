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
}
