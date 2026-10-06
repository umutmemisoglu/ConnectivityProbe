# ConnectivityProbe NuGet paketini yayınlama

Pakete yalnızca kütüphane girer: `src/ConnectivityProbe` (discover + identity uçları, Strict agent ve platform adaptörleri).
Monitor, örnek uygulama ve testler pakete girmez.

## Kısa yol (kullanılan yöntem): GitHub Actions

1. `src/ConnectivityProbe/CHANGELOG.md` dosyasına yeni sürümün bölümünü ekleyin (`## 1.2.0`, altında `### English` ve `### Türkçe`).
2. `ConnectivityProbe.csproj` içindeki `<Version>` değerini artırın, commit edip `main`'e gönderin.
3. Etiketi gönderin:
   ```bash
   git tag -a v1.2.0 -m "ConnectivityProbe 1.2.0"
   git push origin v1.2.0
   ```

`.github/workflows/publish.yml` testleri çalıştırır, paketi etiketteki sürümle üretir ve nuget.org'a API anahtarı olmadan
(Trusted Publishing) yükler. Ardından GitHub Releases sayfasında CHANGELOG'daki bölümden sürüm notunu oluşturur.
Aşağıdaki adımlar bu yöntemin ayrıntıları ve elle yayınlama içindir.

## 1. Yayından önce kontrol

```bash
dotnet test tests/ConnectivityProbe.Tests
```

Testlerin hepsi geçmeli.

Sonra `src/ConnectivityProbe/ConnectivityProbe.csproj` içindeki paket bilgilerini kontrol edin:

| Alan | Şu anki değer | Not |
|---|---|---|
| `PackageId` | `ConnectivityProbe` | nuget.org'da paket adı ilk yükleyen hesaba bağlanır; ilk yüklemeden sonra bu ad yalnızca sizin hesabınızla güncellenebilir. |
| `Version` | `1.0.0` | Her yayında artırın (aşağıya bakın). Aynı sürüm ikinci kez yüklenemez. |
| `Authors` / `Copyright` | `Fatih Umut Memişoğlu` | Paket sayfasında yazar ve telif sahibi olarak görünür. |
| Lisans | `MIT` | Açık kaynak. Pakette `PackageLicenseExpression`, depoda kökteki `LICENSE` dosyası. |
| `RepositoryUrl` / `PackageProjectUrl` | tanımlı değil | Kaynak kodu GitHub gibi herkese açık bir yere koyduğunuzda ekleyin; nuget.org sayfasında "Source repository" bağlantısı olarak görünür (bkz. 8. adım). |

> **Önemli:** nuget.org herkese açıktır. Paket ve içindeki README herkes tarafından indirilebilir ve **silinemez**, yalnızca
> listeden kaldırılabilir (unlist). Kod iş kapsamında yazıldığı için telif hakkı işverene ait olabilir; herkese açık ve MIT
> lisansıyla yayınlamadan önce gerekirse şirketinizden onay alın.

### MIT lisansı ne anlama gelir?

- Herkes paketi ücretsiz kullanabilir, kodu değiştirebilir, dağıtabilir; ticari projelerde de kullanabilir.
- Tek şart: telif satırı ve lisans metni kopyalarda korunur.
- Yazılım "olduğu gibi" verilir; kullanımdan doğan sorunlarda yazara sorumluluk yüklenmez.
- Lisansı ileride değiştirebilirsiniz, ama daha önce yayınlanan sürümler MIT olarak kalır.

## 2. Paketi üretme

```bash
dotnet pack src/ConnectivityProbe/ConnectivityProbe.csproj -c Release -o artifacts -p:ContinuousIntegrationBuild=true
```

Çıktılar:

- `artifacts/ConnectivityProbe.<sürüm>.nupkg`: paketin kendisi. İçinde `lib/net462`, `lib/netstandard2.0`, `lib/net8.0`, README ve CHANGELOG bulunur.
- `artifacts/ConnectivityProbe.<sürüm>.snupkg`: hata ayıklama sembolleri. nuget.org bunu otomatik olarak sembol sunucusuna alır.

## 3. Yayından önce yerelde deneme (isteğe bağlı)

`artifacts` klasörünü paket kaynağı yapıp gerçek bir uygulamada kurun:

```bash
dotnet nuget add source <repo>/artifacts -n localcp
dotnet add package ConnectivityProbe --version 1.0.0
```

## 4. nuget.org'a yükleme

1. https://www.nuget.org adresinde oturum açın. Hesap yoksa Microsoft hesabıyla oluşturun. Kurum adına yayınlanacaksa bir
   organizasyon hesabı açıp paketi onun altında yayınlayın; böylece paket kişiye bağlı kalmaz.
2. **API anahtarı oluşturun:** sağ üstte kullanıcı adı → *API Keys* → *Create*.
   - Key name: ör. `connectivityprobe-publish`
   - Scope: *Push new packages and package versions*
   - Glob pattern: `ConnectivityProbe`
   - Süre: en fazla 365 gün. Anahtar yalnızca bir kez gösterilir; kopyalayın.
3. **Yükleyin.** `.snupkg` dosyası aynı klasörde olduğu için semboller de otomatik yüklenir:

   ```bash
   dotnet nuget push artifacts/ConnectivityProbe.1.0.0.nupkg --api-key <API_ANAHTARI> --source https://api.nuget.org/v3/index.json
   ```

   Anahtarı komut geçmişinde bırakmamak için ortam değişkeniyle de verebilirsiniz:
   - PowerShell: `$env:NUGET_API_KEY = "..."`, sonra komutta `--api-key $env:NUGET_API_KEY`
   - bash: `export NUGET_API_KEY=...`, sonra komutta `--api-key "$NUGET_API_KEY"`
4. nuget.org paketi doğrular ve indeksler. Bu genellikle 5–15 dakika sürer; o sırada sayfada "validating" görünür. Sonra
   `dotnet add package ConnectivityProbe` ile her yerden kurulabilir.

**Geri alma:** yanlış bir sürüm yüklendiyse silinemez; paket sayfası → *Manage package* → *Listing* ile listeden kaldırın
(unlist) ve düzeltilmiş sürümü yeni bir sürüm numarasıyla yükleyin.

## 5. Alternatif: şirket içi feed (bu proje için kullanılmıyor; paket herkese açık yayınlanıyor)

Paket yalnızca şirket içinde kullanılacaksa nuget.org yerine iç feed'e yükleyin (Nexus, Azure Artifacts, GitHub Packages,
ProGet...):

```bash
dotnet nuget push artifacts/ConnectivityProbe.1.0.0.nupkg --api-key <ANAHTAR> --source <İÇ_FEED_URL>
```

Tüketen projelere feed'i ekleyin (`nuget.config`):

```xml
<configuration>
  <packageSources>
    <add key="company" value="<İÇ_FEED_URL>" />
  </packageSources>
</configuration>
```

> Docker build'leri (CI içindeki `dotnet restore`) iç feed'e erişebilmeli; `nuget.config` dosyasını Dockerfile'da
> restore'dan önce kopyalayın.

## 6. Yeni sürüm çıkarma

1. Değişiklikleri yapın ve testleri çalıştırın.
2. `src/ConnectivityProbe/CHANGELOG.md` dosyasına yeni sürümün başlığını ve değişikliklerini ekleyin.
3. `ConnectivityProbe.csproj` içindeki `<Version>` değerini artırın
   ([SemVer](https://semver.org/lang/tr/)):
   - **Yama** `1.0.1`: hata düzeltmesi.
   - **Minor** `1.1.0`: geriye uyumlu yeni özellik (yeni ayar, yanıta yeni alan).
   - **Major** `2.0.0`: kıran değişiklik (uç adı/yolu, yanıt alanlarının anlamı, varsayılan güvenlik davranışı).
4. 2. ve 4. adımları tekrarlayın.

Yüklü sürüm her yanıtta `probeVersion` olarak döner; hangi uygulamanın hangi sürümü kullandığı uzaktan görülebilir.

## 7. Kaynak kopyası yerine pakete geçiş

Kaynak kopyası (`src/ConnectivityProbe` projesi) yerine paketi kullanmak için:

1. Solution'dan `src/ConnectivityProbe` projesini kaldırın ve klasörü silin.
2. Merkezi paket yönetimi (Central Package Management) kullanılıyorsa:
   - `Directory.Packages.props` dosyasına ekleyin:
     ```xml
     <PackageVersion Include="ConnectivityProbe" Version="1.0.0" />
     ```
   - `Web.csproj` içinde `ProjectReference` yerine:
     ```xml
     <PackageReference Include="ConnectivityProbe" />
     ```
3. Dockerfile'dan `src/ConnectivityProbe/ConnectivityProbe.csproj` için eklenen `COPY` satırını kaldırın.
4. `Program.cs` değişmez: `using ConnectivityProbe;` ve `app.UseConnectivityProbe(...)` aynen çalışır.

## 8. Kaynak kodu açık kaynak olarak paylaşma (önerilir)

Açık kaynak bir paketin kaynak kodu da herkese açık olmalıdır; nuget.org sayfasından koda ulaşılabilmesi güven verir.

1. GitHub'da `ConnectivityProbe` adında **public** bir depo açın.
2. Bu klasörü Git deposu yapıp gönderin. `bin/`, `obj/`, `artifacts/`, `.vs/` ve Monitor'ün `data/` klasörünü
   `.gitignore` ile dışarıda bırakın: `data/` içinde Monitor'e girdiğiniz iç sunucu adları var.
3. `ConnectivityProbe.csproj` içine ekleyin:
   ```xml
   <PackageProjectUrl>https://github.com/<kullanıcı>/ConnectivityProbe</PackageProjectUrl>
   <RepositoryUrl>https://github.com/<kullanıcı>/ConnectivityProbe</RepositoryUrl>
   <RepositoryType>git</RepositoryType>
   <PublishRepositoryUrl>true</PublishRepositoryUrl>
   ```
4. Yeni bir sürüm (ör. `1.0.1`) üretip yükleyin; bağlantılar paket sayfasında görünür.
