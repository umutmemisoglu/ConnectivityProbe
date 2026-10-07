# Teams bildirimleri: kurulum

[English](teams-setup.md) | **Türkçe**

Connectivity Monitor, uygulamalarda seçili durumlar oluştuğunda (bağlantı koptu, sertifika bitiyor, bellek limite yakın...)
abone olan kişilere Teams'ten özel mesaj gönderir. "🔔 Bana haber ver" düğmesinin nasıl çalışacağı, Monitor'e girilen
ayarlara göre kendiliğinden seçilir:

| Girilen ayarlar | "Bana haber ver" | Kişi ne yapar |
|---|---|---|
| Merkezi iş akışı **ve** Microsoft girişi | Microsoft hesabıyla giriş, dönüşte otomatik abonelik | Hiçbir şey (çoğu zaman şifre de sorulmaz) |
| Yalnızca merkezi iş akışı | İlk seferde ad ve şirket e-postası | Bir kez e-postasını yazar |
| Hiçbiri | İlk seferde ad ve kendi iş akışının adresi | Teams'te kendi iş akışını kurar |

Önerilen kurulum ilk satırdır. Aşağıdaki üç adım **bir kez** yapılır.

---

## 1. Entra ID uygulama kaydı (Microsoft ile giriş)

Kişinin adını ve e-postasını doğrulanmış olarak öğrenmek içindir. Yalnızca temel oturum açma izni ister; **client secret
gerekmez**. Uygulama kaydı oluşturma izni olmayan kullanıcılar bu adımı BT'ye bırakmalıdır.

1. https://entra.microsoft.com → **Applications → App registrations → + New registration**.
2. Doldurun:
   - **Name:** `Connectivity Monitor`
   - **Supported account types:** *Accounts in this organizational directory only (Single tenant)*
   - **Redirect URI:** platform **Web**, `https://<monitor-adresi>/signin-oidc`
3. **Register**. **Overview** sayfasından **Application (client) ID** ve **Directory (tenant) ID** değerlerini alın.
4. **Authentication**:
   - *Web* altında **Add URI** ile her ortamın adresini ekleyin (`https://<test>/signin-oidc`, `https://<prod>/signin-oidc`;
     yerel deneme için `http://localhost:5087/signin-oidc`).
   - **Implicit grant and hybrid flows → ID tokens (used for implicit and hybrid flows)** kutusunu işaretleyin → **Save**.
5. **Token configuration → + Add optional claim → ID → email → Add** (Microsoft Graph email izni sorulursa onaylayın).
6. **API permissions**: yalnızca *Microsoft Graph → User.Read (Delegated)* ve *email*. Kullanıcı onayı kapalıysa
   **Grant admin consent for &lt;şirket&gt;**.
7. (İsteğe bağlı) Kimlerin girebileceğini sınırlamak için **Enterprise applications → Connectivity Monitor → Properties →
   Assignment required = Yes** ve **Users and groups**'tan grup ekleyin.

## 2. Monitor'ün HTTPS adresi

Microsoft, localhost dışındaki dönüş adreslerinde yalnızca `https` kabul eder. Test ve prod Monitor'leri sabit bir https
adreste yayınlayın (Kubernetes'te Ingress + şirket sertifikası). Adres, 1. adımdaki redirect URI ile **birebir aynı**
olmalıdır. Monitor bir ingress / proxy arkasındaysa `X-Forwarded-Proto` ve `X-Forwarded-Host` başlıkları dikkate alınır
(ingress'ler bunu varsayılan olarak gönderir).

## 3. Merkezi Teams iş akışı

Monitor her bildirimi bu iş akışına alıcının e-postasıyla gönderir; iş akışı mesajı kişiye Flow bot üzerinden özel sohbet
olarak iletir.

> İş akışı onu oluşturan hesaba bağlıdır; o kişi ayrılırsa bildirimler durur. Mümkünse ortak bir servis hesabı kullanın.

1. https://make.powerautomate.com → **+ Create → Instant cloud flow → Skip**.
2. Tetikleyici: **When a Teams webhook request is received** → *Who can trigger the flow?* **Anyone**.
3. **+ New step → Microsoft Teams – Post card in a chat or channel**:
   - **Post as:** `Flow bot`
   - **Post in:** `Chat with Flow bot`
   - **Recipient:** *Expression* → `triggerBody()?['recipient']`
   - **Adaptive Card:** *Expression* → `string(triggerBody()?['card'])`
4. Ad verin (ör. `Connectivity Monitor bildirimleri`) → **Save**. Tetikleyicideki **HTTP POST URL** adresini kopyalayın.
   Bu adres **gizlidir**.
5. Deneyin (kendi e-postanızla):

   ```bash
   curl -X POST "<HTTP POST URL>" -H "Content-Type: application/json" -d "{\"recipient\":\"ad.soyad@sirket.com\",\"card\":{\"type\":\"AdaptiveCard\",\"version\":\"1.4\",\"body\":[{\"type\":\"TextBlock\",\"text\":\"Connectivity Monitor deneme mesajı\"}]}}"
   ```

   Teams'e *Workflows* adına özel mesaj gelmezse Power Automate'te akışın **Run history**'sine bakın. En sık neden şirketin
   Power Platform veri politikasının (DLP) Teams bağlayıcısını kısıtlamasıdır.

## 4. Monitor ayarları

| Ayar | Ortam değişkeni | Gizli mi |
|---|---|---|
| `Monitor:Auth:TenantId` | `Monitor__Auth__TenantId` | Hayır |
| `Monitor:Auth:ClientId` | `Monitor__Auth__ClientId` | Hayır |
| `Monitor:Notifications:WorkflowUrl` | `Monitor__Notifications__WorkflowUrl` | **Evet** (secret olarak verin) |

Kubernetes örneği:

```yaml
env:
  - name: Monitor__Auth__TenantId
    value: "<directory-tenant-id>"
  - name: Monitor__Auth__ClientId
    value: "<application-client-id>"
  - name: Monitor__Notifications__WorkflowUrl
    valueFrom: { secretKeyRef: { name: connectivity-monitor, key: teams-workflow-url } }
```

Monitor'ün çalıştığı yerden Microsoft'a (`login.microsoftonline.com`) ve iş akışı adresine dışarı doğru HTTPS erişimi
olmalıdır; proxy varsa `HTTPS_PROXY` kullanılır.

## Nasıl görünür

- **Tanımlar → uygulama kartı → 🔔 Bildirimler:** uygulamanın hangi durumlarda bildirim göndereceği (kategorili, açıklamalı
  seçenekler; kişiden bağımsızdır) ve bildirim alanlar.
- **🔔 Bana haber ver:** kartta ve uygulamanın detay sayfasında. Microsoft modunda ilk tıklamada giriş, sonrasında tek tık.
- Sağ üstte adınız görünür; Microsoft modunda tıklayınca bu tarayıcıdaki oturum kapatılabilir (bildirimler sürer).
