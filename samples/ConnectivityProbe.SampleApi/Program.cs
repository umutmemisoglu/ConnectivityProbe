// Bu örnek uygulamada ConnectivityProbe'a dair TEK SATIR KOD YOKTUR.
//
// Uçlar (/connectivity-probe/discover, /connectivity-probe/identity) şu iki şeyle devreye girer:
//   1) Proje ConnectivityProbe'u referans alır (gerçek uygulamalarda NuGet paketi).
//   2) Ortam değişkeni: ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=ConnectivityProbe   (bkz. Properties/launchSettings.json)
// Ayarlar appsettings.json içindeki "ConnectivityProbe" bölümünden okunur.
//
// Aynı makinede birden fazla pod taklit etmek için farklı seed ile çalıştırın:
//   dotnet run --no-launch-profile --urls http://localhost:5101 --ConnectivityProbe:InstanceIdSeed=pod-a
//   (bu durumda ortam değişkenini kendiniz verin: set ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=ConnectivityProbe)

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Text(
    "ConnectivityProbe.SampleApi\n\n" +
    "  GET /connectivity-probe/discover?host=localhost:5078                                  (yalnızca telnet)\n" +
    "  GET /connectivity-probe/discover?host=http://localhost:5078&usesConnectivityProbe=true (telnet + hedefin pod keşfi)\n" +
    "  GET /connectivity-probe/identity\n"));

app.Run();
