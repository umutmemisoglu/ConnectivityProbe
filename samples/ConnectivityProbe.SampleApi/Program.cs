// ConnectivityProbe örneği: uygulama başlarken tek satır.
//
// Uygulama ilk açılışta Monitor'e anahtarıyla kendini kaydeder; her pod bağlantılarını Monitor'den alır, kendi içinden test eder ve
// sonuçları, uygulama sürümü ve cluster bilgisiyle birlikte gönderir. Monitor'e ulaşılamazsa uygulama etkilenmez; konsola kısa bir
// İngilizce mesaj yazılır.
//
// Değerler burada yapılandırmadan okunuyor (appsettings.json -> "ConnectivityProbe" veya ortam değişkeni
// ConnectivityProbe__MonitorUrl / ConnectivityProbe__AppKey); doğrudan metin olarak da verilebilir.
// Aynı makinede birden fazla pod taklit etmek için farklı POD_NAME ile çalıştırın:
//   set POD_NAME=sample-a & dotnet run --no-launch-profile --urls http://localhost:5101

using ConnectivityProbe;

var builder = WebApplication.CreateBuilder(args);

ConnectivityProbeAgent.Start(
    monitorUrl: builder.Configuration["ConnectivityProbe:MonitorUrl"] ?? "",
    appKey: builder.Configuration["ConnectivityProbe:AppKey"] ?? "",
    appName: "ConnectivityProbe Sample API");

var app = builder.Build();
app.MapGet("/", () => "ConnectivityProbe.SampleApi");
app.Run();
