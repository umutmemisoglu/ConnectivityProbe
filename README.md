# ConnectivityProbe

| Proje | Ne işe yarar |
|---|---|
| `src/ConnectivityProbe` | Kütüphane (`netstandard2.0` + `net462` + `net8.0`). IIS / klasik ASP.NET, ASP.NET Core 2.1–10, OWIN ve web sunucusu olmayan uygulamalarda **kod yazmadan** devreye girer; uygulamanın içinden TCP erişim testi yapar ve pod kimliğini sunar. Platform başına kurulum: [src/ConnectivityProbe/README.md](src/ConnectivityProbe/README.md) |
| `src/ConnectivityProbe.Monitor` | Merkezi izleme uygulaması. Kütüphaneyi yüklemiş uygulamaları kaydeder, ortak bir bağlantı havuzunu sürükle-bırakla uygulamalara atar ve periyodik olarak pod sayısını ve erişimleri izler. Uygulamalar **Discover** (Monitor uygulamaya gider) veya **Strict** (her pod Monitor'e kendini bildirir, kesin sonuç) modunda izlenir. |
| `samples/ConnectivityProbe.SampleApi` | Örnek ASP.NET Core uygulaması. İçinde ConnectivityProbe'a dair **hiç kod yok**; yalnızca ortam değişkeni (`launchSettings.json`) ve `appsettings.json` ile devreye girer. |
| `tests/ConnectivityProbe.Tests` | Kütüphanenin birim ve uçtan uca testleri (gerçek Kestrel sunucusuyla). `dotnet test tests/ConnectivityProbe.Tests` |

**NuGet:** Kütüphane `ConnectivityProbe` paketi olarak yayınlanır. Paketi üretme, nuget.org'a veya şirket içi feed'e yükleme
ve yeni sürüm çıkarma adımları için bkz. [PUBLISHING.md](PUBLISHING.md).

## Monitor nasıl çalışır

0. **Hiyerarşi: Birim → Ekip → Uygulama.** Tanımlar sekmesinde solda birimler (ör. Efatura) ve altlarındaki ekipler bulunur. Bir ekip seçildiğinde ortada o ekibin havuzu ile ortak havuz, sağda ise ekibin uygulamaları görünür.
   - **Ekip havuzu:** Bu bağlantılar yalnızca o ekibin uygulamalarına atanabilir (sunucu da bu kuralı kontrol eder).
   - **Ortak havuz:** Merkezi SQL, LDAP gibi herkesin kullandığı bağlantılar burada durur ve tüm uygulamalara atanabilir.
   - Bir uygulama başka ekibe taşınırsa eski ekibin havuzundan gelen bağlantılar uygulamadan çıkarılır, ortak bağlantılar kalır. İçinde ekip olan birim ya da içinde uygulama veya bağlantı olan ekip silinemez. Ekibi olmayan eski kayıtlar "Atanmamış uygulamalar" altında listelenir.
   - **Monitör ekranı (Netflix tarzı):** Üstteki büyük alanda en kritik sorunlu uygulama ve genel sayaçlar yer alır; hiç sorun yoksa "Tüm sistemler ayakta" yazar. Altta önce "Dikkat gerektirenler" satırı, ardından her birimin altında ekip başına yatay kayan bir kart satırı gelir.
     - **Kartta görünenler:** durum, pod sayısı, bağlantı özeti, sol üstte en önemli sorunu gösteren şerit ("1 POD EKSİK", "ERİŞİLEMİYOR") ve alt kenarda cevap veren pod oranını gösteren bir çubuk. Üzerine gelince URL, son kontrol zamanı ve son turların geçmişi de görünür.
     - **Karta tıklayınca** bir detay penceresi açılır: pod'lar bölüm listesi biçiminde, bağlantı × pod matrisi, geçmiş grafiği ve "Pod listesini sıfırla" düğmesi.
     - Arama (ad, URL, ekip, birim) veya "Sadece sorunlular" seçildiğinde sonuçlar ızgara halinde gösterilir.
1. **Uygulama kaydı ve mod:** Her uygulama iki moddan biriyle tanımlanır; kartlarda ve detay penceresinde **STRICT / DISCOVER** rozetiyle görünür, aramaya "strict" yazarak süzülebilir.

   | | Discover | Strict |
   |---|---|---|
   | Nasıl çalışır | Monitor uygulamanın adresine gider; istekler load balancer üzerinden pod'lara düşer. | Her pod, uygulama anahtarıyla Monitor'den kendi tanımlarını çeker, kendi içinde test eder ve sonucu gönderir. |
   | Pod sayısı | Olasılıksal (güven yüzdesiyle) | **Kesin** (rapor veren pod'lar) |
   | Uygulamada | ConnectivityProbe + ortak `AccessKey` | ConnectivityProbe 1.1+ + `ConnectivityProbe:MonitorUrl` + `ConnectivityProbe:AppKey` |
   | Uygulama adresi | Zorunlu | İsteğe bağlı; verilirse Monitor dışarıdan erişimi de kontrol eder (HTTP'si olmayan worker'lar da izlenebilir) |
   | Ağ yönü | Monitor → uygulama | Pod → Monitor |

   **Strict:** Uygulama kaydedilince bir anahtar üretilir. Anahtar Tanımlar ekranındaki uygulama kartında her zaman görünür;
   kopyala düğmesi, "Kurulum bilgisi" (hazır ortam değişkenleri) ve "yeni anahtar üret" düğmesi oradadır. Yeni anahtar
   üretilince eskisi hemen geçersiz olur. Pod'lar 10 sn'de bir bildirim gönderir; "Şimdi test et" en geç bu sürede pod'lara
   ulaşır. Düzgün kapanan pod "kapanıyorum" bildirir ve alarm vermeden listeden çıkar. Bildirimi kesilen pod önce "bildirim
   gecikti", `MissingAfterCycles` tur sonra "eksik" olur. Mod değiştirilince uygulamanın pod geçmişi sıfırlanır.

   **Discover:** Ad + URL (örn. `https://orders.example.com`). Uygulamada ConnectivityProbe devrede olmalı (bkz. kütüphane README'si).
   **Erişim anahtarı ortaktır:** tüm uygulamalarda aynı `ConnectivityProbe:AccessKey` kullanılır ve Monitor'e bir kez verilir (`Monitor:AccessKey`, tercihen ortam değişkeni `Monitor__AccessKey`). Monitor bu anahtarı hem izlenen uygulamalara hem de CP işaretli hedeflere gönderir.
2. **Bağlantı havuzu:** Ortak erişim hedefleri (`sql01` + port, `sql01:1433`, IP veya `https://...`). Havuzdan bir bağlantıyı bir uygulamanın üzerine sürükleyip bırakarak o uygulamayla ilişkilendirirsiniz. Aynı bağlantı birden çok uygulamaya atanabilir.
   **Checkpoint — "Bu hedef de ConnectivityProbe kullanıyor mu?"** Her bağlantıda işaretlenir:
   - **Hayır** (DB, Redis, SMTP, dış API...): yalnızca **telnet (TCP)**. Hedefe hiç HTTP isteği gönderilmez.
   - **Evet** (kütüphaneyi yüklemiş başka bir uygulama; URL olarak yazın): önce **telnet**, açıksa hedefin pod'ları keşfedilir. Hücrede ör. "✓ 10 pod" görünür; üzerine gelince hedefin her pod'u (adı, IP'leri) listelenir. Havuzda bu bağlantılar **CP** rozetiyle görünür.
3. **Periyodik test (varsayılan 30 sn):** Her uygulama için
   - **Önce telnet:** Uygulamanın kendi portu kapalıysa identity'ye hiç istek atılmaz; uygulama "Erişilemiyor" görünür ve nedeni (timeout = firewall, reddedildi = servis kapalı, DNS) yazar.
   - Port açıksa uygulamanın `/connectivity-probe/identity` ucuna yeni bağlantılarla istek atılır; dönen `instanceId`'lerden **kaç pod çalıştığı** bulunur. Daha önce bilinen bir pod görünmediyse alarm vermeden önce ek isteklerle özellikle aranır.
   - İlişkilendirilmiş her bağlantı **uygulamanın kendi içinden** test edilir: uygulamanın `/connectivity-probe/discover` ucu çağrılır (işaretliyse `usesConnectivityProbe=true` ile; telnet + hedefin pod keşfi). Yanıt hangi pod'un test yaptığını da içerir; böylece sonuçlar pod başına bir matriste gösterilir. Erişilemeyen (paketi düşüren) hedefler her pod için "Timeout" olarak görünür.

**İşaretli (CP) bağlantıların olası sonuçları:**

| Hücrede | Anlamı |
|---|---|
| ✓ N pod | Telnet açık, hedefin N pod'u bu pod'un içinden görüldü. |
| ⚠ 2/3 pod'a erişildi · ✗ demo-app-c · 12 dakikadır erişilemiyor | Hedefin daha önce görülen bir pod'una bu pod'dan üst üste `MissingAfterCycles` tur erişilemedi; uygulama "Sorunlu" olur. Monitor her bağlantının hedef pod'larını kendisi hatırlar (`pod-state.json`), bu yüzden hedefin Monitor'de ayrıca kayıtlı olması gerekmez. Hedef deploy edildiyse (pod sayısı korunduysa) eski pod'lar alarmsız düşer; sayı azaldıysa "Pod listesini sıfırla" ile temizlenir. |
| ✗ Timeout / reddedildi | Telnet başarısız; HTTP isteği gönderilmedi. |
| ✗ "…ConnectivityProbe var ama istekleri reddetti: HTTP 401" | Hedefin `ConnectivityProbe:AccessKey`'i ortak anahtarla aynı değil: hedefte ortak anahtarı kullanın. |
| ✗ "…hedefte ConnectivityProbe bulunamadı" | Port açık ama hedef bizim kütüphaneyi kullanmıyor: bağlantıdaki işareti kaldırın. |

**Pod durumları:**

| Durum | Görünüm | Anlamı |
|---|---|---|
| Görüldü | normal | Bu turda cevap verdi. |
| Bu turda görülmedi | kesikli çerçeve, soluk | Rastgele dağıtım yüzünden denk gelinmemiş olabilir. **Alarm değil.** |
| Eksik | kırmızı | Üst üste `MissingAfterCycles` (3) tur görünmedi **ve** pod sayısı azaldı. **Alarm.** |

Bir pod'un yerine yenisi geldiyse (deploy, restart: pod sayısı korunuyor) eski pod alarm vermeden listeden düşer.
**"Eksik" olarak işaretlenen bir pod ise kendiliğinden asla silinmez** (sonradan yerine yeni pod gelse bile): yalnızca kartta
çıkan **"Pod listesini sıfırla"** butonuyla temizlenir. Eksik pod tekrar cevap verirse kendiliğinden normale döner. Pod listesi
`data/pod-state.json` dosyasına yazıldığı için Monitor yeniden başlasa bile kaybolmaz. Bilerek pod sayısını azalttıysanız
(scale-down) eski pod'lar "eksik" görünür; sıfırla butonuyla temizleyin.

Başarısız her hücrede kesintinin ne zamandır sürdüğü yazar ("5 dakikadır erişilemiyor"). Üzerine gelince başlangıç zamanı ve son başarılı test de görünür.

Uygulama durumları: **Sağlıklı** (her şey yolunda), **Sorunlu** (eksik pod, başarısız veya test edilemeyen bağlantı), **Erişilemiyor** (hiçbir pod cevap vermiyor; nedeni mesajda yazar, ör. `HTTP 401` = erişim anahtarı yanlış/eksik).

## Çalıştırma

```bash
dotnet run --project src/ConnectivityProbe.Monitor
```

Varsayılan adres: http://localhost:5087/ . Ayarlar `src/ConnectivityProbe.Monitor/appsettings.json` içindeki `Monitor` bölümündedir:

| Ayar | Varsayılan | Anlamı |
|---|---|---|
| `AccessKey` | boş | Ortak erişim anahtarı (uygulamalardaki `ConnectivityProbe:AccessKey` ile aynı). Ortam değişkeniyle verin: `Monitor__AccessKey` |
| `AdminUser` / `AdminPassword` | `admin` / boş | Arayüz girişi. Şifre verilmezse arayüze **yalnızca Monitor'ün çalıştığı makineden** erişilebilir. Herkese açık kurulumda mutlaka verin: `Monitor__AdminPassword` |
| `MaxConcurrentConnections` | 4 | Discover: bir uygulamanın bağlantılarından aynı anda kaç tanesi test edilir |
| `IntervalSeconds` | 30 | Test turları arasındaki süre |
| `ProbeTimeoutMs` | 5000 | Tek bir istek/bağlantı denemesinin zaman aşımı |
| `DataFile` | `data/definitions.json` | Uygulama ve bağlantı tanımlarının saklandığı dosya |
| `MaxConcurrency` | 4 | Aynı anda kontrol edilen uygulama sayısı |
| `MaxInstanceAttempts` | 60 | Pod sayımı için bir uygulamaya atılacak en fazla istek |
| `InstanceConfidence` | 0.95 | Pod sayımı için hedef güven |
| `MaxProbeCallsPerConnection` | 40 | Bir bağlantıyı tüm pod'larda denemek için en fazla istek |
| `MissingAfterCycles` | 3 | Bir pod'un "eksik" (alarm) sayılması için üst üste kaç tur görünmemesi gerektiği |

## Dikkat

- **Arayüz girişi:** `Monitor:AdminPassword` verildiğinde arayüz ve tüm yönetim API'leri giriş ister (çerez, 8 saat). Şifre
  verilmezse yalnızca localhost'tan erişilir. Monitor bir ters proxy arkasındaysa ve proxy aynı makinedeyse istekler localhost'tan
  gelmiş görünür; bu durumda şifreyi mutlaka verin. Pod'ların kullandığı `/api/agent/*` uçları girişten bağımsızdır, uygulama
  anahtarıyla korunur. Monitor'ü HTTPS arkasında yayınlayın.
- Ortak erişim anahtarını `appsettings.json`'a yazmak yerine ortam değişkeni / secret ile verin (`Monitor__AccessKey`). Anahtar
  tüm Discover uygulamalarında aynı olduğu için sızarsa hepsi etkilenir. Strict anahtarı ise yalnızca kendi uygulamasını etkiler
  (o uygulamanın tanımlarını okuma ve sonuç gönderme) ve tek tıkla yenilenebilir.
- İzlenen uygulamalardaki `/connectivity-probe` uçlarını `AccessKey` ile koruyun ve `AllowedTargets` ile hangi hedeflere bağlanabileceklerini kısıtlayın. Monitor'de tanımlı bir bağlantı bu listede yoksa uygulama `403` döner ve satırda hata görünür.
- Uygulamada kütüphanenin eski bir sürümü varsa (testi yapan pod bilgisi dönmüyorsa) satırda "sürüm eski" uyarısı görünür.
- Discover modunda pod sayımı ve "her pod bağlantıyı test etti" bilgisi olasılıksaldır (load balancer rastgele dağıtır). Bu turda bir pod'a denk gelinmezse hücrede son bilinen sonuç soluk olarak gösterilir. Kesin sonuç için Strict modu kullanın.
- Self-signed HTTPS sertifikalı uygulamalar şimdilik desteklenmiyor (sertifika doğrulaması kapatılamıyor).
