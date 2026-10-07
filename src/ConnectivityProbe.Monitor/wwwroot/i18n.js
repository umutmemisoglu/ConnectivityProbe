'use strict';

// ---------------------------------------------------------------------------------------------
// Dil desteği (Türkçe / İngilizce). Seçim tarayıcıda saklanır; sunucuya her istekte "X-Lang" başlığıyla (giriş sayfası için
// "cp-lang" çereziyle) bildirilir, böylece sunucunun hata mesajları da aynı dilde gelir.
// HTML'de sabit metinler data-i18n (metin), data-i18n-placeholder ve data-i18n-title öznitelikleriyle işaretlenir.
// ---------------------------------------------------------------------------------------------
const I18N = {
  tr: {
    'lang.name': 'Türkçe',
    'state.healthy': 'Sağlıklı', 'state.degraded': 'Sorunlu', 'state.down': 'Erişilemiyor', 'state.unknown': 'Bekliyor',

    // üst menü
    'tab.monitor': 'Monitör', 'tab.defs': 'Tanımlar',
    'search.placeholder': 'Uygulama, anahtar, ekip, sürüm, ağ',
    'onlyProblems': 'Sadece sorunlular', 'runNow': 'Şimdi test et', 'runNow.title': "Pod'lar zaten her test aralığında kendiliğinden test eder. Bir düzeltmeden sonra sonucu beklemeden görmek için: pod'lar en geç 10 saniye içinde yeniden test eder.", 'logout': 'Çıkış', 'logout.title': 'Oturumu kapat',
    'lang.title': 'Dil / Language',
    'meta.updated': 'güncellendi {t}', 'meta.interval': "pod'lar her {n} sn test eder",
    'dlg.cancel': 'Vazgeç', 'dlg.save': 'Kaydet', 'err.login': 'Giriş gerekli',

    // zaman
    'ago.s': '{n} sn önce', 'ago.m': '{n} dk önce', 'ago.h': '{n} sa önce',
    'for.s': '{n} saniyedir', 'for.m': '{n} dakikadır', 'for.hm': '{h} saat {m} dakikadır', 'for.h': '{h} saattir', 'for.d': '{n} gündür',

    // durum notları (sunucudan kod olarak gelir)
    'note.waiting': 'Pod bildirimi bekleniyor',
    'note.noReports': 'Hiçbir pod bildirim göndermiyor (son bildirim: {t})',
    'note.missingPods': '{n} pod eksik (üst üste {c}+ test aralığı bildirim göndermedi)',
    'note.failedTests': '{n} bağlantı testi başarısız',
    'note.latePods': "{n} pod'un bildirimi gecikti",
    'note.versions': '{n} farklı sürüm/build çalışıyor',
    'note.firstResults': 'ilk test sonuçları bekleniyor',

    // tanımlar: ağaç
    'defs.unitsTeams': 'Birimler ve ekipler', 'defs.addUnit': '+ Birim', 'defs.addTeam': '+ Ekip', 'defs.addTeam.title': 'Bu birime ekip ekle',
    'common.edit': 'Düzenle', 'common.delete': 'Sil', 'defs.appsCount': '{n} uyg.',
    'defs.noTeams': 'Ekip yok. "+ Ekip" ile ekleyin.',
    'defs.noUnits': 'Önce bir birim (ör. Efatura), sonra altına ekipler ekleyin.',
    'defs.unassignedApps': 'Atanmamış uygulamalar',

    // tanımlar: havuz
    'pool.title': 'Bağlantı havuzu',
    'pool.help': 'Tüm bağlantılar tek havuzdadır; birim ve ekiplerden bağımsızdır. Bir bağlantıyı tutup sağdaki uygulamanın üzerine bırakın.', 'pool.empty': 'Henüz bağlantı yok. "+ Ekle" ile ekleyin (ör. merkezi SQL, Redis, harici API).',
    'pool.add': '+ Ekle', 
    
    

    // tanımlar: arama, kullanım, durum, sürükleme
    'pool.search': 'Bağlantı ara: ad, host, port, uygulama…', 'pool.results': '{n} sonuç (en alakalı üstte)', 'pool.noResults': 'Eşleşen bağlantı yok.',
    'pool.usedBy': '{n} uygulama', 'pool.usedByTip': 'Kullanan uygulamalar: {names}', 'pool.unused': 'kullanılmıyor', 'pool.unusedTip': 'Hiçbir uygulamaya atanmamış',
    'pool.status': '{ok}/{all} pod erişiyor', 'pool.noStatus': 'Henüz test sonucu yok', 
    'apps.open': 'Detay', 'apps.dragToTeam': 'Başka ekibe taşımak için kartı soldaki ekibin üzerine sürükleyin', 'apps.noReport': 'pod bildirimi yok',
    'apps.moved': '"{app}" → {team}',

    // tanımlar: uygulamalar
    'apps.teamTitle': '{name} uygulamaları', 'apps.title': 'Uygulamalar',
    'apps.howTo': 'Yeni uygulama nasıl eklenir?',
    'apps.howToText': 'Uygulamalar burada eklenmez. ConnectivityProbe\'u uygulamaya ekleyip başlangıçta aşağıdaki satırı çağırın; uygulama ilk açılışta anahtarıyla kendini kaydeder ve "Atanmamış uygulamalar" altında görünür. Sonra ✎ ile ekibine taşıyıp bağlantılarını atayın.',
    'apps.unassignedHint': 'Bu uygulamalar kendini kaydetti ama henüz bir ekibe atanmadı. ✎ ile ekibine taşıyın.',
    'apps.key': 'anahtar', 'apps.registered': 'kayıt {t}', 'apps.copyKey': 'Anahtarı kopyala', 'apps.editTitle': 'Adını değiştir / ekibe taşı',
    'apps.detach': 'Bu uygulamadan çıkar',
    'apps.dropHere': 'Bağlantıları buraya sürükleyip bırakın', 'apps.pickConn': '+ bağlantı seç…', 
    'apps.emptyTeam': 'Bu ekipte uygulama yok. Kendini kaydeden uygulamalar "Atanmamış uygulamalar" altında görünür; oradan bu ekibe taşıyın.',
    'apps.selectTeam': 'Soldan bir ekip seçin.',
    'snippet.key': 'uygulama-anahtari', 'snippet.name': 'Uygulama Adı',
    'snippet.where': "ASP.NET Core / Worker / konsol: Program.cs'te başlangıçta.  IIS / klasik ASP.NET: Global.asax Application_Start içinde.",

    // formlar
    'form.unitAdd': 'Birim ekle', 'form.unitEdit': 'Birimi düzenle', 'form.unitName': 'Birim adı', 'form.unitPh': 'ör. Efatura',
    'form.teamAdd': 'Ekip ekle', 'form.teamEdit': 'Ekibi düzenle', 'form.teamName': 'Ekip adı', 'form.teamPh': 'ör. Fatura Ekibi', 'form.unit': 'Birim',
    'form.connAdd': 'Bağlantı ekle', 'form.connEdit': 'Bağlantıyı düzenle', 'form.appEdit': 'Uygulamayı düzenle',
    'form.name': 'Ad', 'form.connNamePh': 'ör. Ana veritabanı',
    'form.host': 'Host veya URL', 'form.hostHint': 'Sunucu adı, IP, sunucu:port ya da tam URL.',
    'form.port': 'Port', 'form.portPh': 'ör. 1433', 'form.portHint': 'Host içinde port varsa veya URL girdiyseniz boş bırakabilirsiniz (https 443, http 80).',
    'form.target': 'Hedef uygulama (isteğe bağlı)', 'form.targetNone': '(yok: veritabanı, kuyruk, dış servis...)',
    'form.targetHint': "Hedef de ConnectivityProbe kullanan, Monitor'e kayıtlı bir uygulamaysa seçin: bağlantı satırında onun pod sayısı ve durumu da görünür.",
    
    
    'form.appNamePh': 'ör. Orders API', 'form.appNameHint': 'İlk kayıtta uygulamanın bildirdiği ad; burada değiştirebilirsiniz. Anahtar değişmez.',
    'form.team': 'Ekip', 'form.teamNone': '(atanmamış)',
    'form.teamHint': 'Uygulamayı ekipler arasında taşıyabilirsiniz; atanmış bağlantıları değişmez.',
    'form.clusterRename': 'Ağa ad ver', 'form.clusterName': 'Ad', 'form.clusterPh': 'ör. Prod İstanbul',
    'form.clusterHint': 'Boş bırakırsanız pod ağı gösterilir ({net}).',
    'target.title': 'Hedef uygulama: {name}',

    // onaylar
    'confirm.delUnit': '"{name}" birimi silinsin mi?', 'confirm.delTeam': '"{name}" ekibi silinsin mi?',
    'confirm.delConn': '"{name}" bağlantısı havuzdan ve tüm uygulamalardan silinsin mi?',
    'confirm.delApp': '"{name}" uygulaması silinsin mi?\n\nPod\'ları hâlâ çalışıyorsa bir sonraki bildirimde kendini yeniden kaydeder (ekipsiz ve bağlantısız). Kalıcı olarak kaldırmak için uygulamadan ConnectivityProbe\'u da çıkarın.',
    'confirm.reset': '"{name}" için hatırlanan pod listesi (eksik pod\'lar dahil) silinecek; mevcut pod\'lar yeniden keşfedilecek. Devam edilsin mi?',
    'copy': 'Kopyala',

    // monitör
    'reset': 'Pod listesini sıfırla',
    'reset.title': 'Eksik pod\'lar kendiliğinden silinmez; sorunu giderdiyseniz (veya pod sayısını bilerek azalttıysanız) buradan temizleyin.',
    'reset.help': 'Eksik pod\'lar kendiliğinden silinmez; sorunu giderdiyseniz veya pod sayısını bilerek azalttıysanız "Pod listesini sıfırla" ile temizleyin.',
    'flag.down': 'ERİŞİLEMİYOR', 'flag.missing': '{n} POD EKSİK', 'flag.conn': '{n} BAĞLANTI SORUNLU', 'flag.versions': '{n} SÜRÜM', 'flag.waiting': 'BEKLİYOR',
    'pods.none': 'pod yok', 'pods.n': '{n} pod',
    'conns.none': 'bağlantı yok', 'conns.bad': '⚠ {bad}/{total} bağlantı', 'conns.ok': '✓ {n} bağlantı',
    'ver.title': 'Uygulama sürümü · build', 'ver.n': '{n} sürüm',
    'scroll.left': 'Sola kaydır', 'scroll.right': 'Sağa kaydır',
    'bb.apps': 'uygulama', 'bb.healthy': 'sağlıklı', 'bb.degraded': 'sorunlu', 'bb.down': 'erişilemiyor',
    'bb.emptyTitle': 'Henüz izlenen uygulama yok',
    'bb.emptyDesc': 'Uygulamalar ConnectivityProbe ile başladığında anahtarlarıyla kendini kaydeder ve burada görünür.',
    'bb.gotoDefs': 'Tanımlara git', 'bb.live': 'Canlı izleme', 'bb.allUp': 'Tüm sistemler ayakta',
    'bb.allUpDesc': '{all} uygulamanın {ok} tanesi sağlıklı{wait}. Her {n} saniyede bir pod\'lar ve bağlantılar yeniden test ediliyor.',
    'bb.waitPart': ', {n} tanesi ilk testi bekliyor',
    'bb.attention': 'Dikkat gerektiriyor', 'bb.details': 'Detayları gör',
    'search.head': '{what} için {n} uygulama', 'search.problems': 'sorunlu', 'search.empty': 'Uyan uygulama yok.',
    'row.problems': 'Dikkat gerektirenler', 'row.live': 'Canlı', 'unit.summary': '{teams} ekip · {apps} uygulama',

    // detay penceresi
    'm.close': 'Kapat', 'm.notFound': 'Uygulama bulunamadı (silinmiş olabilir).', 'm.back': 'Monitöre dön', 'm.unassigned': 'Atanmamış', 'm.versionsN': '{n} farklı sürüm', 'm.updated': 'güncellendi {t}',
    'm.historyLabel': 'Son {n} test aralığı · çubuk yüksekliği pod sayısı',
    'm.key': 'Anahtar', 'm.unit': 'Birim', 'm.team': 'Ekip', 'm.networks': 'Ağlar', 'm.conns': 'Bağlantılar', 'm.connsBad': '({n} sorunlu)',
    'm.interval': 'Test aralığı', 'm.intervalVal': '{n} sn',
    'm.versions': 'Sürümler', 'm.version': 'Sürüm · build', 'm.total': 'Toplam',
    'm.versionsHelp': 'Her ağda (cluster) hangi sürümün kaç pod\'da çalıştığı. Azınlıktaki sürüm/build sarıyla işaretlenir.',
    'm.pods': "Pod'lar", 'm.podsSummary': '{n} pod · {c} ağ', 'm.podsUp': '{up}/{all} pod',
    'm.rename': 'Ağa ad ver', 'm.unknownNet': 'Bilinmeyen ağ',
    'pod.up': 'ÇALIŞIYOR', 'pod.unconfirmed': 'BİLDİRİM GECİKTİ', 'pod.missing': 'EKSİK', 'pod.odd': 'FARKLI SÜRÜM',
    'pod.lastReport': 'son bildirim {t}', 'pod.missingDesc': '{d} bildirim göndermiyor · son görülme {t}',
    'pod.lateDesc': 'Bildirimi gecikti · son bildirim {t}', 'pod.started': 'başladı {t}',
    'kv.version': 'sürüm', 'kv.build': 'build', 'kv.ip': 'IP', 'kv.namespace': 'namespace',

    // bağlantı matrisi
    'mx.title': 'Bağlantılar (her pod kendi içinden test eder)', 'mx.conn': 'Bağlantı',
    'mx.noConns': 'Bu uygulamaya bağlantı atanmamış. "Tanımlar" sekmesinden sürükleyip bırakın.',
    'mx.waiting': 'Bağlantı sonuçları bekleniyor…', 'mx.missing': 'eksik',
    'mx.target': 'hedef: {name} · {n} pod · {state}', 'mx.ip': 'IP: {ips}',
    'mx.lastKnown': 'Son bilinen sonuç ({t}): {r}', 'mx.okMs': 'başarılı, {n} ms', 'mx.failed': 'başarısız',
    'mx.noPrev': 'Bu pod için daha önce sonuç alınmadı',
    'mx.podSilent': 'Pod {d} bildirim göndermiyor (son: {t}).', 'mx.podSilentShort': 'Pod bildirim göndermiyor',
    'mx.noResult': 'Bu pod henüz test sonucu göndermedi', 'mx.stale': 'Son bilinen sonuç ({t})', 'mx.reached': 'Ulaşılan IP: {ip}',
    'mx.error': 'Hata: {e}', 'mx.failingSince': 'Başarısız: {t} tarihinden beri', 'mx.lastOk': 'Son başarılı test: {t}',
    'mx.neverOk': "Monitor açıldığından beri hiç başarılı olmadı", 'mx.unreachableFor': '{d} erişilemiyor',

    // 2.1: kaynaklar, TLS, DNS, gecikme
    'note.memHigh': "{n} pod'da bellek limite yakın", 'note.restart': '{n} pod yeniden başladı', 'note.oom': "{n} pod'da bellek yetmediği için süreç öldürüldü (OOM)",
    'note.portsHigh': "{n} pod'da TCP soketleri port aralığını dolduruyor", 'note.certExpiring': '{n} bağlantının sertifikası yakında bitiyor ({t})',
    'note.throttled': "{n} pod CPU limiti yüzünden yavaşlatılıyor", 'note.slow': '{n} bağlantı olağandan yavaş', 'note.ipChanged': '{n} bağlantının IP adresi değişti',
    'flag.memHigh': 'BELLEK %{n}', 'flag.restart': 'YENİDEN BAŞLADI', 'flag.oom': 'OOM', 'flag.ports': 'PORT %{n}', 'flag.cert': 'SERTİFİKA {n} GÜN', 'flag.certExpired': 'SERTİFİKA BİTTİ',
    'alert.memHigh': 'BELLEK %{n}', 'alert.throttled': 'THROTTLE %{n}', 'alert.restart': 'YENİDEN BAŞLADI {t}', 'alert.oom': 'OOM {t}', 'alert.portsHigh': 'PORT %{n}',
    'res.title': 'Kaynaklar', 'res.help': "Her pod kendi CPU, bellek, thread ve TCP soket kullanımını bildirir. Limitler Kubernetes / container (cgroup) değerleridir; container dışında yalnızca süreç ölçümleri görünür. Grafikler son ~10 dakikayı gösterir.",
    'res.pod': 'Pod', 'res.cpu': 'CPU', 'res.memory': 'Bellek', 'res.threads': 'Thread', 'res.tcp': 'TCP', 'res.net': 'Ağ', 'res.restarts': 'Restart',
    'res.cores': '{n} çekirdek', 'res.limit': 'limit {n}', 'res.noLimit': 'limit yok', 'res.throttle': 'throttle %{n}', 'res.heap': 'GC heap {n}',
    'res.gc': 'GC {a}/{b}/{c}', 'res.pool': 'havuz {busy} meşgul', 'res.est': '{n} kurulu', 'res.tw': '{n} TIME_WAIT', 'res.ports': 'port %{n}',
    // Kaynaklar tablosu açıklamaları (başlık / değer ipuçları ve tablo altındaki açıklama bölümü)
    'res.legend': 'Bu değerler ne anlama geliyor?',
    'res.cpuTip': "Sürecin son ölçümde kullandığı işlemci, çekirdek cinsinden (0,5 = yarım çekirdek). limit: Kubernetes'te pod'a verilen CPU limiti. throttle: limit yüzünden yavaşlatılan sürenin oranı; %25'in üstü yanıt sürelerini uzatır, CPU limiti artırılmalı.",
    'res.memTip': "Container'ın kullandığı bellek / Kubernetes'te pod'a verilen bellek limiti. Limite ulaşan pod öldürülür (OOMKilled) ve yeniden başlar. %60 altı rahat, %60–90 takip edilmeli, %90 ve üstü uyarı. Grafik son ~10 dakikayı gösterir: düz çizgi normaldir, sürekli yükselen ve hiç düşmeyen çizgi bellek sızıntısı belirtisidir.",
    'res.heapTip': "GC heap: .NET'in uygulamanın nesneleri için tuttuğu bellek; toplam belleğin bir parçasıdır (gerisi .NET çalışma ortamı, DLL'ler, thread'ler, önbellekler). Sürekli büyüyorsa uygulama nesneleri bırakmıyor olabilir.",
    'res.gcTip': "Çöp toplayıcının (GC) süreç başından beri çalışma sayıları: Gen0 / Gen1 / Gen2. Gen0 kısa ömürlü nesnelerin ucuz temizliğidir, yüksek olması normaldir. Gen2 tam temizliktir ve pahalıdır; hızla artıyorsa uygulama bellek baskısı altındadır ve yanıtlarda takılmalar olabilir.",
    'res.threadsTip': "Süreçteki thread sayısı. havuz meşgul: .NET ThreadPool'da o an iş yapan thread'ler; sürekli yüksekse istekler sıraya giriyor olabilir (thread pool starvation). handle: açık dosya, soket ve nesne tanıtıcıları; sürekli artıyorsa bir şeyler kapatılmıyor olabilir.",
    'res.tcpTip': "Pod'daki TCP soketleri. kurulu: açık bağlantılar. TIME_WAIT: kapatılmış ama işletim sisteminin bir süre beklettiği bağlantılar; çok birikirse yeni bağlantı açmak için port kalmaz. port %: kullanılan soketlerin yerel port aralığına oranı; %70 ve üstü uyarı.",
    'res.netTip': "Pod'un saniyede aldığı (↓) ve gönderdiği (↑) veri; son iki ölçümün farkından hesaplanır.",
    'res.restartsTip': "Monitor'ün gördüğü yeniden başlama sayısı (aynı pod, yeni süreç) ve son yeniden başlama zamanı. Son 60 dakikadaki yeniden başlama uyarı üretir; nedeni çoğu zaman bellek limitine ulaşılması (OOM) veya uygulamanın çökmesidir.",
    'res.cpusTip': "Sürecin gördüğü işlemci sayısı (container'da CPU limitine göre ayarlanır).",
    'res.limitsNote': "Limit ve yüzdeler yalnızca Kubernetes / container'da görünür; IIS veya sanal makinede limit olmadığı için sadece sürecin kullanımı gösterilir.",
    'res.memOk': '%{n}: rahat', 'res.memWatch': '%{n}: takip edilmeli; sürekli artıyorsa bellek sızıntısı olabilir', 'res.memHighTip': '%{n}: limite çok yakın, pod her an öldürülebilir (OOM); limit artırılmalı veya uygulama incelenmeli',
    'res.none': 'Kaynak bilgisi yok (pod 2.1 öncesi bir ConnectivityProbe sürümü kullanıyor).', 'res.restartsVal': '{n} · son {t}',
    'mx.dns': 'DNS {n} ms', 'mx.tls': 'TLS', 'mx.certValid': 'Sertifika {n} gün geçerli', 'mx.certExpiring': 'Sertifika {n} gün sonra bitiyor', 'mx.certExpired': 'Sertifikanın süresi doldu', 'mx.certInvalid': 'Sertifika geçersiz', 'mx.certOk': 'Sertifika geçerli', 'mx.tlsFailed': 'Şifreli bağlantı (TLS) kurulamadı', 'mx.tlsOld': '⚠ Eski ve güvensiz protokol: {p}', 'mx.slow': 'YAVAŞ',
    'mx.slowTip': 'Son ölçümler olağan süreden ({n} ms) en az 3 kat yavaş', 'mx.ipChanged': 'IP değişti {t}: {from} → {to}',
    'mx.cert': 'Sertifika: {subject}', 'mx.certIssuer': 'Veren: {issuer}', 'mx.certEnd': 'Bitiş: {t}', 'mx.certErrors': 'Sertifika sorunu: {e}', 'mx.tlsProto': 'Protokol: {p}',
    'form.duplicate': '⚠ Bu adres havuzda zaten var: "{name}" ({key}). Aynı adres ikinci kez eklenemez; mevcut bağlantıyı kullanın.',
    'form.tls': 'TLS / sertifika kontrolü', 'form.tlsAuto': 'Otomatik (https:// ve 443, 8443, 636, 993, 995, 465, 5671 portları)', 'form.tlsOn': 'Açık', 'form.tlsOff': 'Kapalı',
    'form.tlsHint': 'Açıkken pod TCP bağlantısından sonra TLS el sıkışması yapar; sertifikanın bitiş tarihi, ad uyumu ve güven zinciri kontrol edilir.',
  },

  en: {
    'lang.name': 'English',
    'state.healthy': 'Healthy', 'state.degraded': 'Degraded', 'state.down': 'Unreachable', 'state.unknown': 'Waiting',

    'tab.monitor': 'Monitor', 'tab.defs': 'Definitions',
    'search.placeholder': 'Application, key, team, version, network',
    'onlyProblems': 'Problems only', 'runNow': 'Test now', 'runNow.title': 'Pods already test on every interval by themselves. Use this right after a fix to see the result without waiting: pods test again within 10 seconds.', 'logout': 'Log out', 'logout.title': 'Sign out',
    'lang.title': 'Dil / Language',
    'meta.updated': 'updated {t}', 'meta.interval': 'pods test every {n} s',
    'dlg.cancel': 'Cancel', 'dlg.save': 'Save', 'err.login': 'Login required',

    'ago.s': '{n}s ago', 'ago.m': '{n} min ago', 'ago.h': '{n} h ago',
    'for.s': 'for {n} s', 'for.m': 'for {n} min', 'for.hm': 'for {h} h {m} min', 'for.h': 'for {h} h', 'for.d': 'for {n} days',

    'note.waiting': 'Waiting for pod reports',
    'note.noReports': 'No pod is reporting (last report: {t})',
    'note.missingPods': '{n} pod(s) missing (no report for {c}+ test intervals)',
    'note.failedTests': '{n} connection test(s) failed',
    'note.latePods': '{n} pod report(s) late',
    'note.versions': '{n} different versions/builds running',
    'note.firstResults': 'waiting for the first test results',

    'defs.unitsTeams': 'Units and teams', 'defs.addUnit': '+ Unit', 'defs.addTeam': '+ Team', 'defs.addTeam.title': 'Add a team to this unit',
    'common.edit': 'Edit', 'common.delete': 'Delete', 'defs.appsCount': '{n} app(s)',
    'defs.noTeams': 'No teams. Add one with "+ Team".',
    'defs.noUnits': 'Add a unit first (e.g. E-Invoice), then teams under it.',
    'defs.unassignedApps': 'Unassigned applications',

    'pool.title': 'Connection pool',
    'pool.help': 'All connections are in one pool, independent of units and teams. Drag a connection onto an application on the right.', 'pool.empty': 'No connections yet. Add one with "+ Add" (e.g. central SQL, Redis, an external API).',
    'pool.add': '+ Add', 
    
    

    'pool.search': 'Search connections: name, host, port, application…', 'pool.results': '{n} result(s), most relevant first', 'pool.noResults': 'No matching connections.',
    'pool.usedBy': '{n} app(s)', 'pool.usedByTip': 'Used by: {names}', 'pool.unused': 'unused', 'pool.unusedTip': 'Not attached to any application',
    'pool.status': '{ok}/{all} pods reach it', 'pool.noStatus': 'No test results yet', 
    'apps.open': 'Details', 'apps.dragToTeam': 'Drag the card onto a team on the left to move it', 'apps.noReport': 'no pod reports',
    'apps.moved': '"{app}" → {team}',

    'apps.teamTitle': '{name} applications', 'apps.title': 'Applications',
    'apps.howTo': 'How do I add an application?',
    'apps.howToText': 'Applications are not added here. Add ConnectivityProbe to the application and call the line below at startup; on first start the application registers itself with its key and appears under "Unassigned applications". Then move it to its team with ✎ and attach its connections.',
    'apps.unassignedHint': 'These applications registered themselves but have no team yet. Move them to their team with ✎.',
    'apps.key': 'key', 'apps.registered': 'registered {t}', 'apps.copyKey': 'Copy key', 'apps.editTitle': 'Rename / move to a team',
    'apps.detach': 'Detach from this application',
    'apps.dropHere': 'Drag and drop connections here', 'apps.pickConn': '+ choose a connection…', 
    'apps.emptyTeam': 'No applications in this team. Applications that register themselves appear under "Unassigned applications"; move them here from there.',
    'apps.selectTeam': 'Select a team on the left.',
    'snippet.key': 'app-key', 'snippet.name': 'Application Name',
    'snippet.where': 'ASP.NET Core / Worker / console: at startup in Program.cs.  IIS / classic ASP.NET: in Global.asax Application_Start.',

    'form.unitAdd': 'Add unit', 'form.unitEdit': 'Edit unit', 'form.unitName': 'Unit name', 'form.unitPh': 'e.g. E-Invoice',
    'form.teamAdd': 'Add team', 'form.teamEdit': 'Edit team', 'form.teamName': 'Team name', 'form.teamPh': 'e.g. Billing Team', 'form.unit': 'Unit',
    'form.connAdd': 'Add connection', 'form.connEdit': 'Edit connection', 'form.appEdit': 'Edit application',
    'form.name': 'Name', 'form.connNamePh': 'e.g. Main database',
    'form.host': 'Host or URL', 'form.hostHint': 'Server name, IP, server:port or a full URL.',
    'form.port': 'Port', 'form.portPh': 'e.g. 1433', 'form.portHint': 'Leave empty if the host contains a port or is a URL (https 443, http 80).',
    'form.target': 'Target application (optional)', 'form.targetNone': '(none: database, queue, external service...)',
    'form.targetHint': 'Choose it if the target is also an application registered in the Monitor: its pod count and state are then shown on the connection row.',
    
    
    'form.appNamePh': 'e.g. Orders API', 'form.appNameHint': 'The name the application reported when it registered; you can change it here. The key cannot change.',
    'form.team': 'Team', 'form.teamNone': '(unassigned)',
    'form.teamHint': 'You can move the application between teams; its attached connections stay the same.',
    'form.clusterRename': 'Name this network', 'form.clusterName': 'Name', 'form.clusterPh': 'e.g. Prod Istanbul',
    'form.clusterHint': 'Leave empty to show the pod network ({net}).',
    'target.title': 'Target application: {name}',

    'confirm.delUnit': 'Delete the unit "{name}"?', 'confirm.delTeam': 'Delete the team "{name}"?',
    'confirm.delConn': 'Delete the connection "{name}" from the pool and from all applications?',
    'confirm.delApp': 'Delete the application "{name}"?\n\nIf its pods are still running it registers again with their next report (without team and connections). To remove it for good, also remove ConnectivityProbe from the application.',
    'confirm.reset': 'The remembered pod list of "{name}" (missing pods included) will be cleared; running pods are discovered again. Continue?',
    'copy': 'Copy',

    'reset': 'Reset pod list',
    'reset.title': 'Missing pods are never removed automatically; clear them here once the problem is fixed (or the pod count was reduced on purpose).',
    'reset.help': 'Missing pods are never removed automatically; once the problem is fixed, or if you reduced the pod count on purpose, clear them with "Reset pod list".',
    'flag.down': 'UNREACHABLE', 'flag.missing': '{n} POD MISSING', 'flag.conn': '{n} CONN. FAILING', 'flag.versions': '{n} VERSIONS', 'flag.waiting': 'WAITING',
    'pods.none': 'no pods', 'pods.n': '{n} pod(s)',
    'conns.none': 'no connections', 'conns.bad': '⚠ {bad}/{total} connections', 'conns.ok': '✓ {n} connection(s)',
    'ver.title': 'Application version · build', 'ver.n': '{n} versions',
    'scroll.left': 'Scroll left', 'scroll.right': 'Scroll right',
    'bb.apps': 'applications', 'bb.healthy': 'healthy', 'bb.degraded': 'degraded', 'bb.down': 'unreachable',
    'bb.emptyTitle': 'No applications yet',
    'bb.emptyDesc': 'Applications register themselves with their key when they start with ConnectivityProbe, and appear here.',
    'bb.gotoDefs': 'Go to definitions', 'bb.live': 'Live monitoring', 'bb.allUp': 'All systems up',
    'bb.allUpDesc': '{ok} of {all} applications are healthy{wait}. Pods and connections are tested again every {n} seconds.',
    'bb.waitPart': ', {n} waiting for the first test',
    'bb.attention': 'Needs attention', 'bb.details': 'See details',
    'search.head': '{n} application(s) for {what}', 'search.problems': 'problems', 'search.empty': 'No matching applications.',
    'row.problems': 'Needs attention', 'row.live': 'Live', 'unit.summary': '{teams} team(s) · {apps} application(s)',

    'm.close': 'Close', 'm.notFound': 'Application not found (it may have been deleted).', 'm.back': 'Back to the monitor', 'm.unassigned': 'Unassigned', 'm.versionsN': '{n} different versions', 'm.updated': 'updated {t}',
    'm.historyLabel': 'Last {n} test intervals · bar height = pod count',
    'm.key': 'Key', 'm.unit': 'Unit', 'm.team': 'Team', 'm.networks': 'Networks', 'm.conns': 'Connections', 'm.connsBad': '({n} with problems)',
    'm.interval': 'Test interval', 'm.intervalVal': '{n} s',
    'm.versions': 'Versions', 'm.version': 'Version · build', 'm.total': 'Total',
    'm.versionsHelp': 'Which version runs on how many pods in each network (cluster). A minority version/build is highlighted in yellow.',
    'm.pods': 'Pods', 'm.podsSummary': '{n} pod(s) · {c} network(s)', 'm.podsUp': '{up}/{all} pods',
    'm.rename': 'Name this network', 'm.unknownNet': 'Unknown network',
    'pod.up': 'RUNNING', 'pod.unconfirmed': 'REPORT LATE', 'pod.missing': 'MISSING', 'pod.odd': 'DIFFERENT VERSION',
    'pod.lastReport': 'last report {t}', 'pod.missingDesc': 'not reporting {d} · last seen {t}',
    'pod.lateDesc': 'Report late · last report {t}', 'pod.started': 'started {t}',
    'kv.version': 'version', 'kv.build': 'build', 'kv.ip': 'IP', 'kv.namespace': 'namespace',

    'mx.title': 'Connections (every pod tests from inside)', 'mx.conn': 'Connection',
    'mx.noConns': 'No connections attached to this application. Drag and drop them in the "Definitions" tab.',
    'mx.waiting': 'Waiting for connection results…', 'mx.missing': 'missing',
    'mx.target': 'target: {name} · {n} pod(s) · {state}', 'mx.ip': 'IP: {ips}',
    'mx.lastKnown': 'Last known result ({t}): {r}', 'mx.okMs': 'ok, {n} ms', 'mx.failed': 'failed',
    'mx.noPrev': 'No earlier result for this pod',
    'mx.podSilent': 'The pod has not reported {d} (last: {t}).', 'mx.podSilentShort': 'Pod not reporting',
    'mx.noResult': 'This pod has not sent a test result yet', 'mx.stale': 'Last known result ({t})', 'mx.reached': 'Reached IP: {ip}',
    'mx.error': 'Error: {e}', 'mx.failingSince': 'Failing since {t}', 'mx.lastOk': 'Last successful test: {t}',
    'mx.neverOk': 'Never succeeded since the Monitor started', 'mx.unreachableFor': 'unreachable {d}',

    'note.memHigh': '{n} pod(s) close to the memory limit', 'note.restart': '{n} pod(s) restarted', 'note.oom': '{n} pod(s) had a process killed for running out of memory (OOM)',
    'note.portsHigh': '{n} pod(s) running out of local ports', 'note.certExpiring': '{n} connection certificate(s) expiring soon ({t})',
    'note.throttled': '{n} pod(s) throttled by the CPU limit', 'note.slow': '{n} connection(s) slower than usual', 'note.ipChanged': '{n} connection IP address(es) changed',
    'flag.memHigh': 'MEMORY {n}%', 'flag.restart': 'RESTARTED', 'flag.oom': 'OOM', 'flag.ports': 'PORTS {n}%', 'flag.cert': 'CERT {n} DAYS', 'flag.certExpired': 'CERT EXPIRED',
    'alert.memHigh': 'MEMORY {n}%', 'alert.throttled': 'THROTTLED {n}%', 'alert.restart': 'RESTARTED {t}', 'alert.oom': 'OOM {t}', 'alert.portsHigh': 'PORTS {n}%',
    'res.title': 'Resources', 'res.help': 'Every pod reports its own CPU, memory, thread and TCP socket usage. Limits are the Kubernetes / container (cgroup) values; outside a container only process figures are shown. Charts cover the last ~10 minutes.',
    'res.pod': 'Pod', 'res.cpu': 'CPU', 'res.memory': 'Memory', 'res.threads': 'Threads', 'res.tcp': 'TCP', 'res.net': 'Network', 'res.restarts': 'Restarts',
    'res.cores': '{n} cores', 'res.limit': 'limit {n}', 'res.noLimit': 'no limit', 'res.throttle': 'throttled {n}%', 'res.heap': 'GC heap {n}',
    'res.gc': 'GC {a}/{b}/{c}', 'res.pool': 'pool {busy} busy', 'res.est': '{n} established', 'res.tw': '{n} TIME_WAIT', 'res.ports': 'ports {n}%',
    'res.legend': 'What do these values mean?',
    'res.cpuTip': 'CPU used by the process in the last measurement, in cores (0.5 = half a core). limit: the CPU limit given to the pod in Kubernetes. throttled: share of time slowed down by the limit; above 25% response times grow and the CPU limit should be raised.',
    'res.memTip': 'Memory used by the container / memory limit given to the pod in Kubernetes. A pod that reaches the limit is killed (OOMKilled) and restarts. Below 60% is comfortable, 60–90% worth watching, 90% and above raises an alert. The chart shows the last ~10 minutes: a flat line is normal, a line that keeps rising and never drops suggests a memory leak.',
    'res.heapTip': 'GC heap: memory .NET keeps for the application objects; part of the total (the rest is the .NET runtime, DLLs, threads, caches). If it keeps growing, the application may not be releasing objects.',
    'res.gcTip': 'How many times the garbage collector ran since the process started: Gen0 / Gen1 / Gen2. Gen0 is the cheap cleanup of short-lived objects, a high number is normal. Gen2 is a full, expensive collection; if it grows quickly the application is under memory pressure and may stall.',
    'res.threadsTip': 'Threads in the process. pool busy: .NET ThreadPool threads doing work right now; if constantly high, requests may be queuing (thread pool starvation). handle: open file, socket and object handles; if it keeps growing, something is not being closed.',
    'res.tcpTip': 'TCP sockets in the pod. established: open connections. TIME_WAIT: closed connections the OS keeps for a while; if they pile up there are no ports left for new connections. ports %: sockets in use relative to the local port range; 70% and above raises an alert.',
    'res.netTip': 'Data received (↓) and sent (↑) by the pod per second, from the difference of the last two measurements.',
    'res.restartsTip': 'Restarts seen by the Monitor (same pod, new process) and the last restart time. A restart in the last 60 minutes raises an alert; the cause is usually hitting the memory limit (OOM) or a crash.',
    'res.cpusTip': 'Number of CPUs the process sees (adjusted to the CPU limit in a container).',
    'res.limitsNote': 'Limits and percentages only appear in Kubernetes / containers; on IIS or a VM there is no limit, so only the process usage is shown.',
    'res.memOk': '{n}%: comfortable', 'res.memWatch': '{n}%: worth watching; if it keeps rising there may be a memory leak', 'res.memHighTip': '{n}%: very close to the limit, the pod can be killed at any moment (OOM); raise the limit or investigate',
    'res.none': 'No resource data (the pod uses a ConnectivityProbe version older than 2.1).', 'res.restartsVal': '{n} · last {t}',
    'mx.dns': 'DNS {n} ms', 'mx.tls': 'TLS', 'mx.certValid': 'Certificate valid for {n} days', 'mx.certExpiring': 'Certificate expires in {n} days', 'mx.certExpired': 'Certificate expired', 'mx.certInvalid': 'Certificate invalid', 'mx.certOk': 'Certificate valid', 'mx.tlsFailed': 'Encrypted connection (TLS) failed', 'mx.tlsOld': '⚠ Outdated, insecure protocol: {p}', 'mx.slow': 'SLOW',
    'mx.slowTip': 'Recent measurements are at least 3× slower than usual ({n} ms)', 'mx.ipChanged': 'IP changed {t}: {from} → {to}',
    'mx.cert': 'Certificate: {subject}', 'mx.certIssuer': 'Issuer: {issuer}', 'mx.certEnd': 'Expires: {t}', 'mx.certErrors': 'Certificate problem: {e}', 'mx.tlsProto': 'Protocol: {p}',
    'form.duplicate': '⚠ This address is already in the pool: "{name}" ({key}). The same address cannot be added twice; use the existing connection.',
    'form.tls': 'TLS / certificate check', 'form.tlsAuto': 'Automatic (https:// and ports 443, 8443, 636, 993, 995, 465, 5671)', 'form.tlsOn': 'On', 'form.tlsOff': 'Off',
    'form.tlsHint': 'When on, the pod performs a TLS handshake after the TCP connection; the certificate expiry, name match and trust chain are checked.',
  },
};

// Seçili dil: kayıtlı tercih, yoksa Türkçe.
let LANG = (() => {
  try { const v = localStorage.getItem('lang'); if (v === 'tr' || v === 'en') return v; } catch { /* depolama kapalı */ }
  return 'tr';
})();

const LOCALE = () => (LANG === 'en' ? 'en-GB' : 'tr-TR');

// Metni seçili dilde döner; {ad} yer tutucuları vars ile doldurulur.
function t(key, vars) {
  const text = I18N[LANG][key] ?? I18N.tr[key] ?? key;
  return vars ? text.replace(/\{(\w+)\}/g, (m, k) => (vars[k] ?? m)) : text;
}

// Sayfadaki sabit metinleri (data-i18n...) seçili dile çevirir.
function applyI18n(root = document) {
  document.documentElement.lang = LANG;
  root.querySelectorAll('[data-i18n]').forEach((el) => { el.textContent = t(el.dataset.i18n); });
  root.querySelectorAll('[data-i18n-placeholder]').forEach((el) => { el.placeholder = t(el.dataset.i18nPlaceholder); });
  root.querySelectorAll('[data-i18n-title]').forEach((el) => { el.title = t(el.dataset.i18nTitle); });
  root.querySelectorAll('[data-lang]').forEach((el) => el.classList.toggle('active', el.dataset.lang === LANG));
}

// Dili değiştirir ve saklar; sunucu hata mesajlarını da bu dilde döner.
function setLang(lang) {
  LANG = lang === 'en' ? 'en' : 'tr';
  try { localStorage.setItem('lang', LANG); } catch { /* depolama kapalı */ }
  document.cookie = 'cp-lang=' + LANG + '; path=/; max-age=31536000; samesite=strict';
  applyI18n();
}

document.cookie = 'cp-lang=' + LANG + '; path=/; max-age=31536000; samesite=strict';
