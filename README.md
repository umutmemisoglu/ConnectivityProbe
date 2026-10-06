# ConnectivityProbe

**English** | [Türkçe](README.tr.md)

[![NuGet](https://img.shields.io/nuget/v/ConnectivityProbe.svg)](https://www.nuget.org/packages/ConnectivityProbe)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

ConnectivityProbe answers two questions that are hard to answer from the outside:

1. **Can my application reach X _from inside each of its own pods / servers_?**
   For example: can every pod of `orders-api` open a TCP connection to `sql01:1433`, to Redis and to the payment API?
2. **How many instances (pods) are really running behind a service, and which one is broken?**

It is a small .NET library you add to your application. **ConnectivityProbe Monitor**, the central web application in this
repository, uses it to watch hundreds of applications on one screen.

| Project | What it is |
|---|---|
| [`src/ConnectivityProbe`](src/ConnectivityProbe) | The library, published on NuGet as [`ConnectivityProbe`](https://www.nuget.org/packages/ConnectivityProbe). Targets `net462`, `netstandard2.0` and `net8.0`. |
| [`src/ConnectivityProbe.Monitor`](src/ConnectivityProbe.Monitor) | Central monitor (ASP.NET Core web app). Registers applications and connections, and shows pods and results. **Not** a NuGet package. |
| [`samples/ConnectivityProbe.SampleApi`](samples/ConnectivityProbe.SampleApi) | Sample ASP.NET Core app that enables the library without a single line of code. |
| [`tests/ConnectivityProbe.Tests`](tests/ConnectivityProbe.Tests) | Unit and end-to-end tests (real Kestrel servers): `dotnet test tests/ConnectivityProbe.Tests` |

---

## Contents

- [Two modes: Discover and Strict](#two-modes-discover-and-strict)
- [Supported platforms](#supported-platforms)
- [Installation by platform](#installation-by-platform)
- [Strict mode in detail](#strict-mode-in-detail)
- [Discover mode in detail](#discover-mode-in-detail)
- [Configuration reference](#configuration-reference)
- [Endpoints](#endpoints)
- [Security](#security)
- [ConnectivityProbe Monitor](#connectivityprobe-monitor)
- [Versions](#versions)

---

## Two modes: Discover and Strict

Each application is registered in the Monitor in one of two modes.

| | **Discover** | **Strict** (1.1.0+) |
|---|---|---|
| Who starts the check | The Monitor calls the application's URL. | Every pod contacts the Monitor by itself. |
| How a check runs | The request goes through the load balancer to a pod. That pod tests the target from inside and answers. The Monitor repeats this until it has seen every pod. | Each pod pulls its connection list from the Monitor (identified by an **app key**), tests every connection from inside and reports back. |
| Pod count | **Estimated**: requests are spread at random, so the result has a confidence (e.g. 95 %). | **Exact**: every pod reports itself. |
| Per-pod result | Probabilistic: a pod the load balancer never chose is shown with its last known result. | Every pod, every cycle. |
| Network direction | Monitor → application | Application (pod) → Monitor |
| Application needs | The package + endpoints enabled (`AccessKey` or `AllowAnonymous`) | The package (1.1.0+) + `MonitorUrl` + `AppKey` |
| Application URL | Required | Optional. If given, the Monitor also checks it from outside. |
| Works for apps without HTTP (queue consumers, workers) | No | **Yes** |
| Deploy / scale-down | A replaced pod drops silently after a few cycles. | A pod shutting down cleanly says "goodbye" and leaves immediately, without an alarm. |
| "Test now" button | Immediate | Within `Strict:CommandPollSeconds` (default 10 s) |

**Which one should I use?**
- **Strict** if you want exact pod counts and exact per-pod results, if the app is a worker without HTTP, or if the
  Monitor cannot reach the application but the application can reach the Monitor.
- **Discover** if you cannot change the application's configuration, or if the application cannot reach the Monitor.
- Both can be used together: in Strict mode the `discover` and `identity` endpoints keep working, so other applications
  can still discover this application's pods.

---

## Supported platforms

| Application type | Target in the package | Discover endpoints | Strict mode (1.1.0+) |
|---|---|---|---|
| ASP.NET Core **.NET 8 / 9 / 10** | `net8.0` | `app.UseConnectivityProbe()` or zero-code (environment variable) | Starts and stops with the application, automatically |
| ASP.NET Core **2.1 – 7** (.NET Core 2.1, 3.1, .NET 5, 6, 7) | `netstandard2.0` | Same as above | Same as above |
| **Classic ASP.NET on IIS** (MVC 5, Web API 2, WebForms, WCF) – .NET Framework **4.6.2+** | `net462` | **Zero-code**: the DLL in `bin` registers itself | Starts with the application, automatically |
| **OWIN self-host** (Katana, Web API 2 self-host) | `net462` / `netstandard2.0` | `app.Use(typeof(ConnectivityProbeOwinMiddleware))` | Starts with the middleware; call `Stop()` on shutdown |
| **No web server**: Worker Service, console app, Windows Service | all | `ConnectivityProbeListener.Start()` (own small HTTP listener) | `ConnectivityProbeAgent.Start()` / `Stop()` |

NuGet picks the right target by itself. The .NET Framework build has **no NuGet dependencies**, so no binding redirects
are needed. .NET Framework 4.6.1 and earlier are not supported.

---

## Installation by platform

```bash
dotnet add package ConnectivityProbe
```

All settings are read from configuration with the prefix `ConnectivityProbe`:

| Source | Example |
|---|---|
| `appsettings.json` (ASP.NET Core) | `"ConnectivityProbe": { "MonitorUrl": "https://monitor.example.com" }` |
| Environment variable (all platforms) | `ConnectivityProbe__MonitorUrl=https://monitor.example.com` |
| `web.config` / `app.config` `<appSettings>` (.NET Framework) | `<add key="ConnectivityProbe:MonitorUrl" value="https://monitor.example.com" />` |
| Code | `options.MonitorUrl = "https://monitor.example.com";` |

The examples below enable **both** the endpoints (for Discover mode) and Strict mode. Leave out `MonitorUrl` / `AppKey`
if you only need Discover mode.

### ASP.NET Core 6, 7, 8, 9, 10 (minimal hosting, `Program.cs`)

```csharp
using ConnectivityProbe;

var builder = WebApplication.CreateBuilder(args);
// ... your services
var app = builder.Build();

// Add it early: before UseHttpsRedirection and before your own authentication.
// The endpoints are protected by their own access key (or AllowAnonymous).
app.UseConnectivityProbe(options =>
{
    options.Info["app"] = "orders-api";            // optional: shown in the identity response and in the Monitor
});

app.UseHttpsRedirection();
// ... the rest of your pipeline
app.Run();
```

`appsettings.json` (or the matching environment variables):

```json
"ConnectivityProbe": {
  "AccessKey": "",
  "MonitorUrl": "https://monitor.example.com",
  "AppKey": ""
}
```

Put `AccessKey` and `AppKey` in secrets / environment variables, not in `appsettings.json`:
`ConnectivityProbe__AccessKey`, `ConnectivityProbe__AppKey`.

### ASP.NET Core 2.1 – 5 (`Startup.cs`)

```csharp
using ConnectivityProbe;

public void Configure(IApplicationBuilder app, IHostingEnvironment env)   // IWebHostEnvironment on 3.0+
{
    app.UseConnectivityProbe();      // first line of Configure; settings come from configuration
    // ... app.UseMvc(), app.UseRouting(), ...
}
```

### ASP.NET Core without code changes (any version 2.1 – 10)

ASP.NET Core's official "hosting startup" mechanism activates a referenced library from an environment variable. You only
add the package and change deployment settings:

```yaml
env:
  - name: ASPNETCORE_HOSTINGSTARTUPASSEMBLIES
    value: ConnectivityProbe                 # if you already have hosting startups: "Other.Assembly;ConnectivityProbe"
  - name: ConnectivityProbe__MonitorUrl
    value: https://monitor.example.com
  - name: ConnectivityProbe__AppKey
    valueFrom: { secretKeyRef: { name: connectivity-probe, key: app-key } }
```

On IIS use `web.config` → `<aspNetCore><environmentVariables>`; with Docker use `ENV`; locally use `launchSettings.json`.
If you also call `app.UseConnectivityProbe()`, it is not added twice.

### Classic ASP.NET on IIS (.NET Framework 4.6.2+)

Add the package (or copy `ConnectivityProbe.dll` to `bin`). Nothing else is needed: the module registers itself when
the application starts. Settings go to `web.config`:

```xml
<appSettings>
  <add key="ConnectivityProbe:AccessKey" value="..." />
  <add key="ConnectivityProbe:MonitorUrl" value="https://monitor.example.com" />
  <add key="ConnectivityProbe:AppKey" value="cpk_..." />
  <add key="ConnectivityProbe:Info:app" value="orders-web" />
</appSettings>
```

- **Strict mode on IIS:** IIS stops an idle application pool after 20 minutes by default, and the background job stops
  with it. Set the application pool to `Start Mode = AlwaysRunning` and `Idle Time-out = 0`, and the site to
  `Preload Enabled = true`. Otherwise the pod shows as "missing" while the pool sleeps.
- **Classic pipeline mode:** automatic registration needs the Integrated pipeline. In Classic mode add
  `<system.web><httpModules><add name="ConnectivityProbe" type="ConnectivityProbe.ConnectivityProbeModule, ConnectivityProbe" /></httpModules></system.web>`
  and set `ConnectivityProbe:AutoRegister=false`.
- **.NET Framework 4.6.2 – 4.7 and HTTPS:** TLS 1.2 must be enabled (`httpRuntime targetFramework="4.7"` or later, or
  `ServicePointManager.SecurityProtocol`).

### OWIN self-host

```csharp
using ConnectivityProbe;

public void Configuration(IAppBuilder app)
{
    app.Use(typeof(ConnectivityProbeOwinMiddleware));   // settings from app.config / environment variables
    // ...
}

// On shutdown (OWIN has no standard shutdown event), so the Monitor gets a "goodbye":
ConnectivityProbeAgent.Current?.Stop();
```

### Worker Service, console app, Windows Service (no web server)

**Strict mode** (recommended for workers): the application only needs to reach the Monitor.

```csharp
using ConnectivityProbe;

// .NET Generic Host (Worker Service): start and stop with the host
builder.Services.AddHostedService<ConnectivityProbeAgentService>();

sealed class ConnectivityProbeAgentService : IHostedService
{
    private readonly IConfiguration _config;
    private ConnectivityProbeAgent? _agent;

    public ConnectivityProbeAgentService(IConfiguration config) => _config = config;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _agent = ConnectivityProbeAgent.Start(new ConnectivityProbeOptions
        {
            MonitorUrl = _config["ConnectivityProbe:MonitorUrl"],
            AppKey = _config["ConnectivityProbe:AppKey"],
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _agent?.Stop();          // sends "goodbye" to the Monitor
        return Task.CompletedTask;
    }
}
```

Without a host, read the settings from environment variables (and from `app.config` on .NET Framework):

```csharp
var agent = ConnectivityProbeAgent.Start();   // null if MonitorUrl / AppKey are not set
// ... application runs
agent?.Stop();
```

**Discover endpoints** without a web server: `ConnectivityProbeListener` opens a small HTTP listener (default
`http://+:8099/`). It also starts the Strict agent if it is configured.

```csharp
var probe = ConnectivityProbeListener.Start();
// ...
probe?.Dispose();
```

On Windows, listening on `http://+:port/` needs administrator rights. Run
`netsh http add urlacl url=http://+:8099/ user="NT AUTHORITY\NETWORK SERVICE"` once, or use
`ConnectivityProbe:ListenerPrefixes=http://localhost:8099/`.

### Kubernetes: complete example

```yaml
env:
  # Strict mode
  - name: ConnectivityProbe__MonitorUrl
    value: https://monitor.example.com
  - name: ConnectivityProbe__AppKey
    valueFrom: { secretKeyRef: { name: connectivity-probe, key: app-key } }
  # Discover endpoints (shared key; or ConnectivityProbe__AllowAnonymous=true on internal services)
  - name: ConnectivityProbe__AccessKey
    valueFrom: { secretKeyRef: { name: connectivity-probe, key: access-key } }
  # Pod information (Downward API): pod names in the Monitor, and unique ids for hostNetwork pods
  - name: POD_NAME
    valueFrom: { fieldRef: { fieldPath: metadata.name } }
  - name: POD_NAMESPACE
    valueFrom: { fieldRef: { fieldPath: metadata.namespace } }
  - name: NODE_NAME
    valueFrom: { fieldRef: { fieldPath: spec.nodeName } }
```

---

## Strict mode in detail

### Setting it up

1. In the Monitor, add the application with **Mode = Strict** (Definitions tab). An **app key** (`cpk_...`) is generated.
   It is shown on the application card, next to *Copy*, *Regenerate* and a ready-to-copy *Setup* block.
2. Attach the connections the application should test (drag and drop from the connection pool).
3. Give the application two settings, `ConnectivityProbe:MonitorUrl` and `ConnectivityProbe:AppKey` (see the platform
   examples above), and deploy.
4. Within about 10 seconds every pod appears in the Monitor, with an exact pod count and the results of each pod.

### What happens in each pod

```
every 10 s  ──►  POST {MonitorUrl}/api/agent/v1/report   (header X-ConnectivityProbe-AppKey)
                  "I am pod X, I am alive"  ◄── connection list + test interval
every 30 s  ──►  test every connection from inside this pod (TCP, plus pod discovery for ConnectivityProbe targets)
                  └─► the results go with the next report, immediately
on shutdown ──►  POST {MonitorUrl}/api/agent/v1/goodbye   → the pod leaves the list without an alarm
```

- The tests use exactly the same logic as the `discover` endpoint, so results mean the same thing in both modes.
- **"Test now"** in the Monitor reaches every pod with its next report (default within 10 s), and the pods test
  immediately.
- If a pod cannot reach the Monitor (wrong URL, firewall, wrong key), the application logs a warning (category
  `ConnectivityProbe`) and tries again with the next report. The application itself is never affected.
- At most 4 connections are tested in parallel per pod, so targets do not get a sudden burst of connections.

### Pod states in Strict mode

| State | Meaning |
|---|---|
| Running | The pod reported within the last 2 × report interval (about 25 s). |
| Report late | The report is late, but the threshold is not reached yet. Not an alarm. |
| Missing | No report for `MissingAfterCycles` (3) test intervals, and the pod count went down. **Alarm.** Removed only with *Reset pod list*. |
| (gone) | The pod said goodbye (deploy, scale-down), or it was replaced by a new pod while the count stayed the same. No alarm. |

### Settings

| Setting | Default | Meaning |
|---|---|---|
| `MonitorUrl` | – | The Monitor's address. The pods must be able to reach it. |
| `AppKey` | – | The app key from the Monitor. It decides which application's definitions the pod gets. |
| `Strict:IntervalSeconds` | Monitor's interval (30) | Test interval in seconds. |
| `Strict:CommandPollSeconds` | 10 | How often the pod reports to the Monitor. "Test now" reaches the pod within this time. |

### The app key

- It identifies the application. With the key, a pod can read **that application's** connection list and send results
  for it, and nothing else.
- Regenerate it in the Monitor at any time; the old key stops working immediately.
- Use a separate application (and key) for each environment, for example "Orders Test" and "Orders Prod".

---

## Discover mode in detail

1. Register the application in the Monitor with its URL (the address that load-balances to the pods).
2. Enable the endpoints in the application: either a shared `AccessKey` (the same value as `Monitor:AccessKey`), or
   `AllowAnonymous=true` for internal services.
3. Every cycle the Monitor:
   - first opens a TCP connection to the application's port. If the port is closed, it reports why (timeout = firewall,
     refused = service down, DNS);
   - calls `/connectivity-probe/identity` over new connections and counts the distinct `instanceId`s (the pods);
   - asks the application to test each attached connection via `/connectivity-probe/discover`. The answer tells which pod
     did the test, so results are shown per pod.

Pod counting assumes that the load balancer spreads **new connections** across pods (Kubernetes Services and ingresses
do this by default). With session affinity, every request lands on the same pod. In that case the response contains a
`notes` warning; use Strict mode instead.

---

## Configuration reference

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` turns the endpoints off (Strict mode is independent). |
| `AccessKey` | empty | Shared access key for the endpoints. Requests must send it in `X-ConnectivityProbe-Key`. |
| `AllowAnonymous` | `false` | Allows access without a key. Only for internal services. |
| `AllowedTargets` | empty | Allowed targets: `sql01:1433`, `redis:*`, `*.svc.cluster.local:443`, `*.lan:*`. Empty = any target. |
| `MaxConcurrentDiscover` | 20 | Discover requests processed at the same time (0 = unlimited); more get `429`. |
| `Path` | `/connectivity-probe` | Base path of the endpoints. |
| `DefaultTimeoutMs` / `MaxTimeoutMs` | 5000 / 30000 | Default and maximum `timeoutMs`. |
| `MaxAttempts` | 100 | Maximum identity requests for pod discovery. |
| `MaxRequestDurationSeconds` | 60 | Time limit of one request; when reached, the response is `truncated: true`. |
| `MaxAddresses` | 64 | Maximum IPs tested for one name. |
| `EnableIdentity` | `true` | Turns the `identity` endpoint on or off. |
| `InstanceIdSeed` | empty | Added to the `instanceId` (for copies that share a machine name). |
| `IdentityEnvironmentVariables` | `POD_NAME, POD_NAMESPACE, POD_IP, NODE_NAME, CLUSTER_NAME, HOSTNAME, APP_POOL_ID, ASPNETCORE_ENVIRONMENT` | Environment variables copied into the identity response. No other variable is ever read. |
| `Info:<name>` | – | Fixed values added to the identity response. |
| `ListenerPrefixes` | `http://+:8099/` | `ConnectivityProbeListener` only. |
| `AutoRegister` | `true` | IIS only: `false` disables automatic module registration. |
| `MonitorUrl`, `AppKey`, `Strict:IntervalSeconds`, `Strict:CommandPollSeconds` | – | Strict mode (see above). |

In code, `options.Log = (level, message) => ...` sends the library's log messages anywhere. In ASP.NET Core they go to the
application's `ILogger` (category `ConnectivityProbe`) automatically. On IIS, Strict mode messages go to
`System.Diagnostics.Trace` unless you set `Log` yourself.

---

## Endpoints

All are `GET`, return JSON and are never cached. Every response, error responses included, carries the header
`X-ConnectivityProbe: 1`.

### `GET /connectivity-probe/discover`

```
/connectivity-probe/discover?host=sql01:1433                                          → TCP test only
/connectivity-probe/discover?host=https://orders.prod.svc&usesConnectivityProbe=true  → TCP + pod discovery of the target
```

| Parameter | Default | Meaning |
|---|---|---|
| `host` | (required) | Host name, IP, `host:port` or URL. IPv6: `[::1]:80`. |
| `port` | port in `host` | 1–65535. For a URL without a port: 443 for https, 80 for http. |
| `usesConnectivityProbe` | `false` | `true` if the target also uses ConnectivityProbe: its pods are discovered after the TCP test. |
| `timeoutMs` | 5000 | Timeout of one connection or request. |
| `attempts` | `MaxAttempts` | Maximum identity requests for pod discovery. |
| `confidence` | 0.99 | Pod discovery: required probability that no pod was missed (0.5–0.999). |
| `scheme` | from the URL, otherwise `http` | Pod discovery: `http` or `https`. |

`targetKind`: `tcp`, `unreachable`, `connectivityProbe` (target pods in `instances[]`), `connectivityProbeError` (the
target uses ConnectivityProbe but rejected the request), `other` (the target does not use ConnectivityProbe).

### `GET /connectivity-probe/identity`

Returns this instance's identity: `instanceId` (12 characters, stable per pod), `machineName`, `processId`,
`startedAtUtc`, `uptimeSeconds`, `localAddresses`, `os`, `framework`, `probeVersion`, `environment`, `info`, `request`.

### Error codes

| Code | When |
|---|---|
| `400` | Invalid `host`, port, `confidence` or `scheme`. |
| `401` | `AccessKey` is set and `X-ConnectivityProbe-Key` is missing or wrong. |
| `403` | Not configured (neither `AccessKey` nor `AllowAnonymous`), or the target is not in `AllowedTargets`. |
| `429` | Too many discover requests at the same time (`MaxConcurrentDiscover`). |

---

## Security

- **Secure by default:** without `AccessKey` or `AllowAnonymous=true`, the endpoints answer every request with `403`.
- `discover` makes the pod open TCP connections to the given address. Never leave it anonymous on a service that is
  reachable from outside. Block `/connectivity-probe` at the ingress, and limit targets with `AllowedTargets`.
- `identity` returns pod IPs, the machine name, OS / .NET version and only the environment variables listed in
  `IdentityEnvironmentVariables`.
- Keep `AccessKey` and `AppKey` in secrets or environment variables.
- Strict mode only needs outbound HTTPS from the pod to the Monitor; no inbound port is needed.

---

## ConnectivityProbe Monitor

```bash
dotnet run --project src/ConnectivityProbe.Monitor
```

Default address: http://localhost:5087/ . The Monitor is not a NuGet package; it is a standalone application.

**What it offers**

- **Units → Teams → Applications.** Each team has its own connection pool; a common pool is shared by everyone. Team
  connections can only be attached to that team's applications.
- **Monitor screen:** the most critical application in a large banner at the top, then a "Needs attention" row and one
  horizontally scrolling row per team. Each card shows its state, pod count, connection summary and a **STRICT /
  DISCOVER** badge. Search by name, URL, team, unit or mode (type "strict").
- **Details window:** pods (name, IPs, last report, library version), a connection × pod matrix (which target pod was
  unreachable, and since when), history and *Reset pod list*.
- **Missing pods are never removed automatically.** Only *Reset pod list* removes them. A pod that comes back returns to
  normal by itself.
- **Login:** set `Monitor:AdminPassword` and the UI and management API require a login. Without a password, the UI is
  only reachable from the machine the Monitor runs on (localhost). The Strict endpoints (`/api/agent/*`) use the app key,
  not the login.

**Settings** (`appsettings.json` → `Monitor`, or environment variables `Monitor__<Name>`)

| Setting | Default | Meaning |
|---|---|---|
| `AdminUser` / `AdminPassword` | `admin` / empty | UI login. Always set the password when the Monitor is reachable by others. |
| `AccessKey` | empty | Shared key sent to Discover applications (same as their `ConnectivityProbe:AccessKey`). |
| `IntervalSeconds` | 30 | Test interval (also the Strict pods' interval). |
| `ProbeTimeoutMs` | 5000 | Timeout of one connection or request. |
| `MissingAfterCycles` | 3 | Cycles without an answer before a pod is "missing". |
| `MaxConcurrency` | 4 | Applications checked at the same time (Discover). |
| `MaxConcurrentConnections` | 4 | Connections of one application tested at the same time (Discover). |
| `MaxInstanceAttempts` / `InstanceConfidence` | 60 / 0.95 | Pod counting (Discover). |
| `MaxProbeCallsPerConnection` | 40 | Calls used to test one connection on all pods (Discover). |
| `DataFile` | `data/definitions.json` | Definitions. Pod state (`pod-state.json`) and login keys (`keys/`) are kept next to it. |

Publish the Monitor behind HTTPS. If a reverse proxy runs on the same machine, every request looks like localhost, so
set `AdminPassword`.

---

## Versions

| Version | Highlights |
|---|---|
| **1.1.0** | **Strict mode**: pods pull their definitions from the Monitor with an app key, test from inside and report back. Exact pod counts. Clean shutdown sends a "goodbye". |
| 1.0.0 | First release: `discover` and `identity` endpoints, zero-code activation on ASP.NET Core and IIS, `MaxConcurrentDiscover`, `AllowedTargets` wildcards, `probeVersion`, logging. |

Full list: [CHANGELOG](src/ConnectivityProbe/CHANGELOG.md) · Releases: [GitHub Releases](https://github.com/umutmemisoglu/ConnectivityProbe/releases) ·
Publishing a new version: [PUBLISHING.md](PUBLISHING.md)

## License

[MIT](LICENSE) © 2026 Fatih Umut Memişoğlu
