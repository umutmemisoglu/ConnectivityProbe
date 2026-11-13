# ConnectivityProbe

**English** | [Türkçe](README.tr.md)

[![NuGet](https://img.shields.io/nuget/v/ConnectivityProbe.svg)](https://www.nuget.org/packages/ConnectivityProbe)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

ConnectivityProbe answers questions that are hard to answer from the outside:

1. **Can my application reach X _from inside each of its own pods / servers_?**
   For example: can every pod of `orders-api` open a TCP connection to `sql01:1433`, to Redis and to the payment API?
2. **How many pods are really running, in which cluster, and which version / build is each one running?**

It is a small .NET library you add to your application with **one line of code**. Every pod reports to a
**ConnectivityProbe Monitor**, a central web application that shows hundreds of applications on one screen. The
Monitor is run separately; it is not part of this repository or the NuGet package.

| Project | What it is |
|---|---|
| [`src/ConnectivityProbe`](src/ConnectivityProbe) | The library, published on NuGet as [`ConnectivityProbe`](https://www.nuget.org/packages/ConnectivityProbe). Targets `netstandard2.0` and `net462`, **no dependencies**. |
| [`samples/ConnectivityProbe.SampleApi`](samples/ConnectivityProbe.SampleApi) | Sample ASP.NET Core app. |
| [`tests/ConnectivityProbe.Tests`](tests/ConnectivityProbe.Tests) | Library unit and end-to-end tests: `dotnet test tests/ConnectivityProbe.Tests` |

---

## Contents

- [How it works](#how-it-works)
- [Supported platforms](#supported-platforms)
- [Installation by platform](#installation-by-platform)
- [The app key](#the-app-key)
- [Versions and builds](#versions-and-builds)
- [Clusters (networks)](#clusters-networks)
- [Resources, TLS and latency](#resources-tls-and-latency-21)
- [Dependencies from settings](#dependencies-from-settings-22)
- [Health endpoint and connection groups](#health-endpoint-and-connection-groups-23)
- [Console messages](#console-messages)
- [Options](#options)
- [Security](#security)
- [The Monitor](#the-monitor)
- [Upgrading from 1.x](#upgrading-from-1x)
- [Version history](#version-history)

---

## How it works

```
application starts ──►  ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)

every 10 s   ──►  POST {MonitorUrl}/api/agent/v2/report     (header X-ConnectivityProbe-AppKey)
                  "I am pod X of app <key>, version 1.4.0, IP 10.42.1.15"
                  ◄── connection list + test interval
every 30 s   ──►  test every connection from inside this pod (TCP)
                  └─► results go with the next report, immediately
on shutdown  ──►  POST {MonitorUrl}/api/agent/v2/goodbye    → the pod leaves the list without an alarm
```

- **No registration step.** The first pod that reports with a new key registers the application in the Monitor
  automatically. Pods with the same key are the same application; it is never registered twice.
- **Nothing added to your HTTP pipeline.** The library adds no route to your application; apart from its own small
  health endpoint on a separate port (2.3+, can be turned off) it opens no port. The pod only needs outbound HTTP(S) to
  the Monitor. Applications without HTTP (workers, queue consumers, Windows services) work the
  same way.
- **Exact pod count.** Every pod reports itself, so the Monitor knows exactly how many pods run and what each one sees.
- **Your application is never affected.** If the Monitor is unreachable or the settings are wrong, the library writes a
  short English message to the console and keeps trying in the background. It never throws.
- **Connections are defined in the Monitor**, not in the application: attach `sql01:1433` to `orders-api` in the Monitor,
  and every pod of `orders-api` starts testing it with its next report.
- "**Test now**" in the Monitor reaches every pod with its next report (within 10 s) and the pods test immediately.
- At most 4 connections are tested in parallel per pod, so targets never get a burst of connections.

---

## Supported platforms

| Application type | Target used from the package |
|---|---|
| ASP.NET Core / .NET **Core 2.0 – .NET 10** (web API, MVC, Razor, gRPC, Worker Service, console) | `netstandard2.0` |
| **.NET Framework 4.6.2+** (classic ASP.NET on IIS: MVC 5, Web API 2, WebForms, WCF; Windows services; console) | `net462` |
| Mono, Xamarin, Unity and anything else that implements .NET Standard 2.0 | `netstandard2.0` |

The library has **no NuGet dependencies** (not even `System.Text.Json`), so it causes no version conflicts and needs no
binding redirects. .NET Framework 4.6.1 and earlier are not supported.

---

## Installation by platform

```bash
dotnet add package ConnectivityProbe
```

Everywhere it is the same single call, once, when the application starts:

```csharp
ConnectivityProbe.ConnectivityProbeAgent.Start(
    monitorUrl: "https://monitor.example.com",   // the Monitor of this environment (test / prod)
    appKey:     "orders-api",                    // you choose it; see "The app key"
    appName:    "Orders API");                   // optional: the name shown in the Monitor
```

Where the values come from is up to you (constants, `appsettings.json`, environment variables, `web.config`). The examples
below read them from configuration so that each environment can point to its own Monitor.

### ASP.NET Core 6 – 10 (`Program.cs`)

```csharp
using ConnectivityProbe;

var builder = WebApplication.CreateBuilder(args);

ConnectivityProbeAgent.Start(
    builder.Configuration["ConnectivityProbe:MonitorUrl"] ?? "",
    builder.Configuration["ConnectivityProbe:AppKey"] ?? "",
    "Orders API");

// ... your services
var app = builder.Build();
// ... your pipeline (nothing to add here)
app.Run();
```

```json
"ConnectivityProbe": {
  "MonitorUrl": "https://monitor.example.com",
  "AppKey": "orders-api"
}
```

Or with environment variables: `ConnectivityProbe__MonitorUrl`, `ConnectivityProbe__AppKey`.

### ASP.NET Core 2.x – 5 (`Startup.cs` / `Program.cs`)

```csharp
public Startup(IConfiguration configuration)
{
    Configuration = configuration;
    ConnectivityProbeAgent.Start(configuration["ConnectivityProbe:MonitorUrl"] ?? "",
                                 configuration["ConnectivityProbe:AppKey"] ?? "",
                                 "Orders API");
}
```

### Worker Service (.NET Generic Host)

```csharp
var builder = Host.CreateApplicationBuilder(args);
ConnectivityProbeAgent.Start(builder.Configuration["ConnectivityProbe:MonitorUrl"] ?? "",
                             builder.Configuration["ConnectivityProbe:AppKey"] ?? "",
                             "Orders Worker");
builder.Services.AddHostedService<Worker>();
builder.Build().Run();
```

### Console application

```csharp
ConnectivityProbeAgent.Start(Environment.GetEnvironmentVariable("ConnectivityProbe__MonitorUrl") ?? "",
                             Environment.GetEnvironmentVariable("ConnectivityProbe__AppKey") ?? "",
                             "Orders Importer");
```

### Classic ASP.NET on IIS (.NET Framework 4.6.2+, `Global.asax.cs`)

```csharp
using System.Configuration;
using ConnectivityProbe;

protected void Application_Start()
{
    ConnectivityProbeAgent.Start(ConfigurationManager.AppSettings["ConnectivityProbe:MonitorUrl"] ?? "",
                                 ConfigurationManager.AppSettings["ConnectivityProbe:AppKey"] ?? "",
                                 "Orders Web");
    // ... AreaRegistration, RouteConfig, ...
}
```

```xml
<appSettings>
  <add key="ConnectivityProbe:MonitorUrl" value="https://monitor.example.com" />
  <add key="ConnectivityProbe:AppKey" value="orders-web" />
</appSettings>
```

- IIS stops an idle application pool after 20 minutes by default, and the reports stop with it; the Monitor then shows the
  pod as missing. Set the pool to `Start Mode = AlwaysRunning`, `Idle Time-out = 0`, and the site to
  `Preload Enabled = true`.
- On .NET Framework the library enables TLS 1.2 for HTTPS Monitor addresses by itself.

### Windows Service (.NET Framework)

Call `Start` in `OnStart`. In `OnStop` you can call `ConnectivityProbeAgent.Current?.Stop();`; it is optional, because
the library also says goodbye when the process exits.

### Stopping

The agent stops by itself when the process exits (`ProcessExit`) or the IIS application domain unloads (`DomainUnload`),
and tells the Monitor that the pod is leaving. Call `ConnectivityProbeAgent.Current?.Stop()` only if you want to stop it
earlier.

### Kubernetes: recommended pod information

The library works without anything else. With the Downward API the Monitor also shows pod and node names:

```yaml
env:
  - name: ConnectivityProbe__MonitorUrl
    value: https://monitor.example.com
  - name: ConnectivityProbe__AppKey
    value: orders-api
  - name: POD_NAME
    valueFrom: { fieldRef: { fieldPath: metadata.name } }
  - name: POD_NAMESPACE
    valueFrom: { fieldRef: { fieldPath: metadata.namespace } }
  - name: NODE_NAME
    valueFrom: { fieldRef: { fieldPath: spec.nodeName } }
```

---

## The app key

- **You choose it** when you add the library, for example `orders-api`. Allowed: 1–200 visible ASCII characters.
- The first pod that reports with a new key registers the application. All pods with the same key are the same
  application.
- **The same key can be used in every environment.** Test and prod have their own Monitor (`MonitorUrl`), so the
  test pods appear in the test Monitor with the test connections, and the prod pods in the prod Monitor.
- In the Monitor you can rename the application and move it to a team; the key cannot be changed. If you delete an
  application while its pods are still running, it registers again with their next report.

---

## Versions and builds

Every pod reports the version of **your application**, not of ConnectivityProbe. Nothing has to be configured:

- **Version:** the greater of `AssemblyInformationalVersion` and `AssemblyVersion` of the assembly that calls `Start`
  (a `+commit` suffix is removed). Set it the usual way, e.g. `<Version>1.4.0</Version>` in the `.csproj` or
  `dotnet publish -p:Version=1.4.0` in CI.
- **Build:** the first 8 characters of the assembly's MVID, a unique id the compiler creates for every build. Two pods
  with the same version but a different build were built separately.
- **Build date:** the date of the assembly file.

The Monitor shows the version on every card and, in the application's details window, a versions × networks table. Pods
that run a different version or build than the rest are highlighted (for example during a rollout, or when a deploy only
reached one cluster).

---

## Clusters (networks)

The Monitor groups pods by network **automatically** and names each group after it:

```
orders-a1 → 10.42.1.15 ┐
orders-a2 → 10.42.1.16 ├─ 10.42.0.0/16   (3 pods)
orders-a3 → 10.42.2.17 ┘
orders-b1 → 10.43.1.21 ┐
orders-b2 → 10.43.1.22 ┘─ 10.43.0.0/16   (2 pods)
```

- **Pod address (2.1+):** `POD_IP` (Kubernetes Downward API) if set; otherwise the local address the pod uses to reach the
  Monitor; otherwise the first IPv4 address of the machine name.
- **Network:** IPv4 `/16` (Kubernetes gives each cluster its own pod network and each node a `/24` of it, so all nodes of a
  cluster fall into the same `/16`), IPv6 `/64`.
- **Kubernetes:** pods are also told apart by a fingerprint of the cluster's CA certificate
  (`/var/run/secrets/kubernetes.io/serviceaccount/ca.crt`; SHA-256, never the certificate itself). So two clusters that use
  the same pod network (e.g. both the default `10.42.0.0/16`) are still two groups, shown as `10.42.0.0/16 · e88c3c`.
  The namespace is read from the same folder.

A network can be given a name in the details window (✎ next to it, e.g. "Prod Istanbul"); leave it empty to go back to the
network address.

---

## Resources, TLS and latency (2.1+)

With every report (every 10 s) the pod also sends its **resource usage**:

| | Everywhere | In a Linux container (Kubernetes, Docker) |
|---|---|---|
| CPU | cores used by the process | **CPU limit** and **throttling** (% of periods slowed down by the limit) |
| Memory | working set, private bytes, GC heap, GC counts | **memory usage and limit**, **OOM kills** |
| Threads | threads, busy thread-pool workers, handles | |
| Network | | bytes in / out (`/proc/net/dev`) |
| TCP sockets | | established, TIME_WAIT, local port range (port exhaustion) |

The Monitor also detects **restarts** (same pod, new process). Nothing is configured; values that cannot be read on a
platform stay empty, and reading them never throws.

For every connection, in addition to the TCP test:

- **DNS time** is measured separately, and a **new IP address** for the name (DNS change, failover) is marked. Round-robin
  DNS does not cause false alarms: addresses seen before are remembered.
- **Slow** connections are marked: the last 3 measurements are 3× slower (and ≥ 50 ms slower) than usual.
- **TLS / certificate check:** automatic for `https://` and ports 443, 8443, 636, 993, 995, 465, 5671 (or set to on / off per
  connection). After the TCP connection the pod performs a TLS handshake and reports protocol, subject, issuer, **expiry
  date** and certificate errors (untrusted, name mismatch, expired). An invalid certificate counts as a failed test.

**Alerts** (thresholds in `Monitor:Alerts`):

| Alert | Default | Effect |
|---|---|---|
| Memory ≥ % of the container limit | 90 | degraded |
| Restart or OOM kill within the last N minutes | 60 | degraded |
| TCP sockets ≥ % of the local port range | 70 | degraded |
| Certificate expires within N days | 14 | degraded |
| CPU throttling ≥ % | 25 | note |
| Slow connection, changed IP | – | note |

---

## Dependencies from settings (2.2+)

Give the pod the application's settings and the databases, queues and service addresses in them are defined in the Monitor
automatically; changes are tracked.

### Where to give the settings

The settings are given in the **fourth parameter of `Start`** (the options block). `Start` itself does not change; if
none of these is set, discovery is simply off and everything works as before. Use **one** of the three:

| Option | Give it when | What the pod does |
|---|---|---|
| `o.SettingsUrl` | The settings come from a URL that returns JSON (e.g. a central settings service) | Reads the URL with a plain `GET` (no extra headers), every `SettingsRefreshMinutes` |
| `o.SettingsJson` | You already have the settings as JSON text | Scans the text |
| `o.Settings` | The application already loaded its settings into an object (e.g. `RemoteSettings`, `IOptions<T>.Value`) | Converts the object to JSON in memory and scans it |

If more than one is set, `SettingsJson` wins, then `Settings`, then `SettingsUrl`.

**1. A settings URL** (ASP.NET Core, the URL comes from configuration, so the same code works in every environment):

```csharp
ConnectivityProbeAgent.Start(
    monitorUrl: builder.Configuration["ConnectivityProbe:MonitorUrl"]!,
    appKey:     "orders-api",
    appName:    "Orders API",
    configure:  o => o.SettingsUrl = builder.Configuration["SettingsFullUrl"]);   // e.g. https://settings.example.com/api/settings/obj/<id>?version=1.0.0
```

**2. JSON text** (e.g. a file mounted into the pod, or a string you already downloaded):

```csharp
ConnectivityProbeAgent.Start(monitorUrl, "orders-api", "Orders API",
    o => o.SettingsJson = File.ReadAllText("/config/settings.json"));
```

**3. The settings object the application already loaded** (recommended when the application downloads its settings at
startup anyway: the settings service is not called a second time and its URL does not have to appear in the code again):

```csharp
var remoteSettings = LoadRemoteSettings();                 // the application's own code
builder.Services.AddSingleton<IRemoteSettings>(remoteSettings);

ConnectivityProbeAgent.Start(monitorUrl, "orders-api", "Orders API",
    o => o.Settings = remoteSettings);
```

With `Settings` / `SettingsJson` the value is the one given at startup; to follow changes without a restart, use
`SettingsUrl`. Other related options: `SettingsRefreshMinutes` (default 10).

What you see: the pod writes one console line when the result changes, e.g.
`[ConnectivityProbe] Settings: 4 dependencies found (1 postgresql, 1 rabbitmq, 1 http, 1 mongodb).` In the Monitor, open
**Definitions → the application's card → ⚙ Found in settings**.

**Security:** the settings are read **only in the pod**. Only the setting's name, type, server, port and TLS go to the
Monitor; passwords, user names, keys and the settings address itself never do. Fields named password / secret / token /
key are not even read.

**How addresses are recognized (no AI, rules only):**

1. **Candidates:** connection strings (`Host=..;` PostgreSQL, `Server=..;Initial Catalog=..` SQL Server, `Uid=` MySQL,
   `(HOST=..)(PORT=..)` Oracle, `host:6379,ssl=true` Redis), URLs (`https://`, `mongodb://`, `amqp(s)://`, `redis(s)://`,
   `postgres://`, `mysql://`, `kafka://`, `ldap(s)://`, `smtp(s)://`...), `host:port` and `h1:9092,h2:9092` lists, and
   host + port fields in the same object (`{"Host": "mq01", "Port": 0}`, `SmtpServer` + `SmtpPort`).
2. **Type:** from the scheme, the connection string, the port (5672 RabbitMQ, 9092 Kafka, 27017 MongoDB, 5601 Kibana, 9200
   Elasticsearch, 6379 Redis...), the setting's name (`RabbitMQSettings`, `KibanaUrl`) and the server name. A missing or
   `0` port becomes the type's default (RabbitMQ 5672). Unambiguous formats are **certain**: added automatically. Values
   that only look like an address (e.g. `"Endpoint": "srv01:8080"`) are **suggestions**: added after you approve them.
   Names that do not resolve in DNS are dropped. `localhost`, example domains and schema URLs are ignored.
3. **Verification (optional, per application in the Monitor):** the pod connects to each address and sends that protocol's
   harmless "hello" (AMQP header, Kafka ApiVersions, MongoDB `hello`, Redis `PING`, PostgreSQL SSLRequest, SQL Server
   PRELOGIN, HTTP `GET /` → Kibana / Elasticsearch / RabbitMQ management...). The type is taken from the server's answer;
   a verified suggestion is added automatically, an uncertain address that cannot be reached at all is dropped. No
   credentials are sent and no data is read. **Certain dependencies are never dropped:** an unreachable database is
   exactly what should be monitored.

**In the Monitor** (Definitions → application card → ⚙ Found in settings):

- A new address reuses the pool connection to the same host:port, otherwise a connection is created (marked ⚙) and attached.
- Changed address (same setting, new server): the old connection is detached and the new one attached. Removed setting:
  detached. Connections created from settings that nobody uses any more are removed from the pool; manually added ones
  are never touched.
- Every addition / change / removal is logged on the card.
- What you remove yourself is not added again (listed under "Ignored", with "Add again").
- Settings are re-read every `SettingsRefreshMinutes` (10). A failed read keeps the previous result (shown with a warning).
- During a deploy only the most recently started pod's settings apply, so old pods cannot roll the definitions back.

## Health endpoint and connection groups (2.3+)

**Health endpoint.** Every application with ConnectivityProbe opens its own small health endpoint on a **separate port**:

```
http://<pod-ip>:29470/connectivityprobe/health
```

- Works the same in ASP.NET Core, classic ASP.NET / IIS, Worker Service, console and Windows services, on every supported
  .NET: the library listens on the port itself (raw TCP), no web server or extra line of code is needed.
- It never collides with the application's own `/health`: different port, different path.
- **Port 29470** is outside the ranges operating systems hand out for outgoing connections (Linux 32768–60999, Windows
  49152–65535) and is not used by common services. The port is opened exclusively; if it is busy, the next 9 ports are
  tried, then a free port. The port in use is reported to the Monitor.
- The answer carries a ConnectivityProbe signature (`X-ConnectivityProbe` header and `connectivityProbe` field), the status
  (`Healthy`, or `Degraded` when the last test could not reach a connection), application, version and pod (instanceId, machine name, so a caller behind a load balancer knows which pod answered). The application
  key is not included.
- Options: `HealthEnabled` (default `true`), `HealthPort` (default `29470`).

**Who uses it.** Connection tests are still made **from inside the pods**. The **Monitor** queries every pod's health
endpoint (per-pod health on the details page) and checks whether the services an application uses run ConnectivityProbe:
a signed answer means *yes* (and the connection's target application is set automatically), "connection refused" or
another service on the port means *no*, a timeout means *unknown* (e.g. a Kubernetes Service only exposes the application
port). The Monitor needs network access to the pods / services on this port.

**Services behind an ingress (2.4+).** An ingress or load balancer exposes only the application's web port, so 29470
cannot be reached at a service's public address (e.g. `https://orders.example.com`). ConnectivityProbe therefore also
learns, on its own, **at which addresses the application is called**: in ASP.NET Core it listens to the event the framework
publishes at the start of every request and reads the request's host name (the request itself is not touched; IP addresses
and localhost are skipped, at most 20 names). The pods report these names to the Monitor, which matches them with the
connection addresses of other applications: when `orders.example.com` is called only for *Orders API*, every connection to
that address gets *Orders API* as its target application for sure, and its health is shown from Orders API's pods. Nothing
has to be added or configured in the application. If the same name is called for more than one application (e.g. an ingress
that routes by path), no target is set. Turn it off with `o.ReportServedHosts = false`. Worker, console and classic ASP.NET
applications do not report addresses.

**Connection groups.** Give groups with keywords; the Monitor shows the connections whose name or address contains one of
the keywords together under the group on the application's details page:

```csharp
ConnectivityProbeAgent.Start(url, key, "Orders API", o =>
{
    o.AddGroup("Payment services", "pay", "billing");
    o.AddGroup("Search", "elastic", "kibana");
});
```

The other connections are shown by type: Services, Databases, Queues, Cache, Other.

## Console messages

The library never throws and never writes a stack trace. It writes a few English lines to the console (and to
`System.Diagnostics.Trace`), all starting with `[ConnectivityProbe]`:

| When | Message |
|---|---|
| First successful report | `Registered to monitor https://monitor.example.com as "orders-api" (3 connections).` |
| Monitor unreachable / rejected | `Could not connect to monitor https://monitor.example.com: <reason>` (first time, then at most every 5 minutes) |
| Monitor reachable again | `Reconnected to monitor https://monitor.example.com.` |
| Missing settings | `MonitorUrl and AppKey are required. Connectivity probe is disabled.` |
| Invalid URL | `Invalid MonitorUrl '...' (expected http:// or https://). Connectivity probe is disabled.` |

---

## Options

All optional; pass them with the fourth parameter:

```csharp
ConnectivityProbeAgent.Start(url, key, "Orders API", o =>
{
    o.PollSeconds = 10;
    o.IntervalSeconds = 60;
});
```

| Option | Default | Meaning |
|---|---|---|
| `PollSeconds` | 10 | How often the pod reports to the Monitor. A pod is "running" while its reports arrive; "Test now" reaches it within this time. |
| `IntervalSeconds` | Monitor's (30) | Test interval in seconds. |
| `TimeoutMs` | Monitor's (5000) | Timeout of one connection attempt (and DNS lookup). |
| `MaxAddresses` | 64 | Maximum IPs tested for one host name. |
| `MaxParallelTests` | 4 | Connections tested at the same time in one pod. |
| `SettingsUrl` / `SettingsJson` / `Settings` | — | The application's settings: their dependencies are defined in the Monitor automatically (see [Dependencies from settings](#dependencies-from-settings-22)). |
| `SettingsRefreshMinutes` | 10 | How often the settings are read again. |
| `HealthEnabled` / `HealthPort` | true / 29470 | The ConnectivityProbe health endpoint on its own port (see [Health endpoint](#health-endpoint-and-connection-groups-23)). |
| `ReportServedHosts` | true | Report the host names the application is called at (ASP.NET Core) so the Monitor can identify the target of other applications' connections (see [Services behind an ingress](#health-endpoint-and-connection-groups-23)). |
| `AddGroup(name, keywords…)` | — | Groups connections in the Monitor by keyword. |

`ConnectivityProbeAgent.Current` returns the running agent (one per process); it exposes `LastContactUtc`, `LastRunUtc`,
`LastError` and `AppId`.

---

## Security

- The library adds **nothing to your application's HTTP pipeline**. It makes outbound HTTP(S) requests to `MonitorUrl`
  and, from 2.3, opens one read-only health endpoint on its own port (`o.HealthEnabled = false` turns it off).
- It sends: pod name, machine name, process id, IP addresses, OS / .NET version, application name / version / build,
  cluster fingerprint, namespace, resource figures (CPU, memory, threads, TCP socket counts, network byte counters),
  test results (for TLS connections the server certificate's subject, issuer and dates) and only these environment variables: `POD_NAME`, `POD_NAMESPACE`, `POD_IP`,
  `NODE_NAME`, `HOSTNAME`, `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, `APP_POOL_ID`. No other variable is ever read.
- The pod tests only the connections that are attached to its application in the Monitor.
- The Monitor is meant for internal networks: registration is open, and the app key identifies the application but is
  not a password. Publish the Monitor only on the internal network,
  behind HTTPS.

---

## The Monitor

The library reports to a ConnectivityProbe Monitor (`MonitorUrl`): a separate web application that is not part of this
repository or the NuGet package. Run one Monitor per environment (test, prod); the pods only need outbound HTTP(S) to it.

---

## Upgrading from 1.x

2.0.0 is a breaking release: everything now works the way 1.1's Strict mode did, with one line of code.

| 1.x | 2.0 |
|---|---|
| `app.UseConnectivityProbe(...)`, hosting startup, IIS module, OWIN middleware, `ConnectivityProbeListener` | `ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)` |
| `/connectivity-probe/discover` and `/identity` endpoints, `AccessKey`, `AllowAnonymous`, `AllowedTargets` | Removed. The application opens no endpoint. |
| Discover mode (Monitor calls the app, estimated pod count) | Removed. Pods report themselves; the count is exact. |
| Application added in the Monitor, generated `cpk_...` key | The developer chooses the key; the first pod registers the application. |
| Package targets: net8.0, netstandard2.0, net462 | netstandard2.0, net462 (no dependencies) |

Steps:

1. Update the package to 2.0.0 and remove `app.UseConnectivityProbe(...)` (and `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`,
   `AccessKey`, `AllowAnonymous` settings).
2. Add `ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)` at startup. An application that used Strict mode can
   keep its existing `cpk_...` key: the Monitor keeps those applications and their connections.
3. Update the Monitor to 2.0. On first start it removes applications that were registered in Discover mode (they had no
   key). Applications still on 1.x keep running, but do not appear in a 2.0 Monitor until they are upgraded.

---

## Version history

| Version | Highlights |
|---|---|
| **2.4.0** | Services behind an ingress are identified for sure: the pods report the host names the application is called at (ASP.NET Core, read-only, nothing to configure) and the Monitor matches them with other applications' connection addresses (`ReportServedHosts`). |
| 2.3.0 | Health endpoint on its own port (`http://<pod-ip>:29470/connectivityprobe/health`) in every kind of application, with a ConnectivityProbe signature; the Monitor checks pod health and whether services use ConnectivityProbe. Connection groups (`o.AddGroup`). |
| 2.2.0 | Dependencies from settings: give `SettingsUrl` / `SettingsJson` / `Settings` and the databases, queues and services in them are defined in the Monitor automatically (only type, server and port leave the pod), with change tracking and optional protocol-level verification (RabbitMQ, Kafka, MongoDB, Redis, PostgreSQL, SQL Server, Kibana, Elasticsearch...). Monitor: optional MongoDB storage. |
| 2.1.0 | Pod resources (CPU, memory and limits, throttling, OOM, threads, TCP sockets), TLS / certificate checks, DNS time, slow-connection and restart detection. Pods report their own IP address (`POD_IP` or the route to the Monitor); the Monitor names clusters after the pod network (e.g. `10.42.0.0/16`). Monitor: versions and network grouping in the details window, Turkish / English UI. |
| 2.0.0 | One line: `ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)`. Automatic registration, application version / build per pod, automatic cluster grouping, no endpoints, no dependencies. |
| 1.1.0 | Strict mode: pods pull their definitions with an app key, test from inside and report back. |
| 1.0.0 | First release: `discover` and `identity` endpoints. |

Full list: [CHANGELOG](src/ConnectivityProbe/CHANGELOG.md) · Releases: [GitHub Releases](https://github.com/umutmemisoglu/ConnectivityProbe/releases) ·
Publishing a new version: [PUBLISHING.md](PUBLISHING.md)

## License

[MIT](LICENSE) © 2026 Fatih Umut Memişoğlu
