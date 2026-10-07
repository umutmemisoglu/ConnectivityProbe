# Teams notifications: setup

**English** | [Türkçe](teams-kurulum.md)

Connectivity Monitor sends a personal Teams message to subscribers when a selected situation occurs in an application
(connection down, certificate expiring, memory close to the limit...).

The setup is done **only once**: **Monitor → Settings → Teams notifications**. After that, anyone who wants notifications
clicks **🔔 Notify me** on an application, signs in with their Microsoft (Teams) account and is added to the notification
list automatically; nobody is asked for anything. If "Notify me" is clicked before the setup is complete, the person is told
so and Settings opens.

The Settings screen shows the status of each step (Configured / Saved / Missing), a step-by-step guide, the Redirect URI to
enter in the Entra ID registration (with a copy button) and **Send test message** to verify the setup.

---|---|---|
| Central workflow **and** Microsoft sign-in | Sign in with Microsoft, subscribed automatically on return | Nothing (usually not even a password) |
| Central workflow only | Name and company e-mail, the first time | Types their e-mail once |
| Neither | Name and the address of their own workflow, the first time | Creates their own Teams workflow |

The first row is recommended. The three steps below are done **once**.

---

## 1. Entra ID app registration (Microsoft sign-in)

Used to learn the person's name and e-mail, verified. Only basic sign-in is requested; **no client secret** is needed.
Users who are not allowed to register applications should leave this step to IT.

1. https://entra.microsoft.com → **Applications → App registrations → + New registration**.
2. Fill in:
   - **Name:** `Connectivity Monitor`
   - **Supported account types:** *Accounts in this organizational directory only (Single tenant)*
   - **Redirect URI:** platform **Web**, `https://<monitor-address>/signin-oidc`
3. **Register**. From **Overview**, copy **Application (client) ID** and **Directory (tenant) ID**.
4. **Authentication**:
   - Under *Web*, **Add URI** for every environment (`https://<test>/signin-oidc`, `https://<prod>/signin-oidc`;
     `http://localhost:5087/signin-oidc` for local tests).
   - Check **Implicit grant and hybrid flows → ID tokens (used for implicit and hybrid flows)** → **Save**.
5. **Token configuration → + Add optional claim → ID → email → Add** (accept the Microsoft Graph email permission).
6. **API permissions**: only *Microsoft Graph → User.Read (Delegated)* and *email*. If user consent is disabled,
   **Grant admin consent for &lt;organization&gt;**.
7. (Optional) To limit who can sign in: **Enterprise applications → Connectivity Monitor → Properties → Assignment
   required = Yes**, then add groups under **Users and groups**.

## 2. HTTPS address for the Monitor

Microsoft only accepts `https` redirect addresses (except localhost). Publish the test and prod Monitors at a fixed https
address (in Kubernetes: Ingress + company certificate). It must be **exactly** the redirect URI of step 1. Behind an
ingress / proxy, `X-Forwarded-Proto` and `X-Forwarded-Host` are honored (ingresses send them by default).

## 3. Central Teams workflow

The Monitor posts every notification to this workflow with the recipient's e-mail; the workflow delivers it as a personal
chat message from Flow bot.

> The workflow belongs to the account that created it; if that person leaves, notifications stop. Prefer a shared
> service account.

1. https://make.powerautomate.com → **+ Create → Instant cloud flow → Skip**.
2. Trigger: **When a Teams webhook request is received** → *Who can trigger the flow?* **Anyone**.
3. **+ New step → Microsoft Teams – Post card in a chat or channel**:
   - **Post as:** `Flow bot`
   - **Post in:** `Chat with Flow bot`
   - **Recipient:** *Expression* → `triggerBody()?['recipient']`
   - **Adaptive Card:** *Expression* → `string(triggerBody()?['card'])`
4. Name it (e.g. `Connectivity Monitor notifications`) → **Save**. Copy the **HTTP POST URL** from the trigger. This
   address is **secret**.
5. Try it (with your own e-mail):

   ```bash
   curl -X POST "<HTTP POST URL>" -H "Content-Type: application/json" -d "{\"recipient\":\"jane.smith@company.com\",\"card\":{\"type\":\"AdaptiveCard\",\"version\":\"1.4\",\"body\":[{\"type\":\"TextBlock\",\"text\":\"Connectivity Monitor test message\"}]}}"
   ```

   If no personal message from *Workflows* arrives, check the flow's **Run history** in Power Automate. The most common
   cause is a Power Platform data policy (DLP) restricting the Teams connector.

## 4. Enter them in the Monitor

Enter the Directory (tenant) ID, the Application (client) ID and the workflow address under **Monitor → Settings → Teams
notifications** and click **Save**; no restart is needed. Then **send a test message** to your own e-mail.

- The settings are stored in the Monitor's data folder (`data/settings.json`); in Kubernetes it must be on a persistent volume.
- The workflow address is secret: once saved it is never shown in the UI or returned by any API. Paste a new address to
  change it; leaving the field empty keeps the saved one.

The values can also be given as environment variables; a value entered on the Settings screen takes precedence:

| Setting | Environment variable | Secret |
|---|---|---|
| `Monitor:Auth:TenantId` | `Monitor__Auth__TenantId` | No |
| `Monitor:Auth:ClientId` | `Monitor__Auth__ClientId` | No |
| `Monitor:Notifications:WorkflowUrl` | `Monitor__Notifications__WorkflowUrl` | **Yes** (use a secret) |

The Monitor needs outbound HTTPS to Microsoft (`login.microsoftonline.com`) and to the workflow address; behind a proxy,
`HTTPS_PROXY` is used. If the tenant ID is wrong or Microsoft cannot be reached, "Notify me" shows a page explaining it.

## What it looks like

- **Definitions → application card → 🔔 Notifications:** which situations notify for this application (grouped, described
  options; independent of people) and who is notified.
- **🔔 Notify me:** on the card and on the application's details page. The first click signs in; after that it is a single click.
- Your name appears at the top right; clicking it signs out in this browser (notifications continue).
