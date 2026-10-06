# ConnectivityProbe

**English** | [Türkçe](README.tr.md)

[![NuGet](https://img.shields.io/nuget/v/ConnectivityProbe.svg)](https://www.nuget.org/packages/ConnectivityProbe)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

ConnectivityProbe answers questions that are hard to answer from the outside:

1. **Can my application reach X _from inside each of its own pods / servers_?**
   For example: can every pod of `orders-api` open a TCP connection to `sql01:1433`, to Redis and to the payment API?
2. **How many pods are really running, in which cluster, and which version / build is each one running?**

It is a small .NET library you add to your application with **one line of code**. Every pod reports to
**ConnectivityProbe Monitor**, the central web application in this repository, which shows hundreds of applications on
one screen.

| Project | What it is |
|---|---|
| [`src/ConnectivityProbe`](src/ConnectivityProbe) | The library, published on NuGet as [`ConnectivityProbe`](https://www.nuget.org/packages/ConnectivityProbe). Targets `netstandard2.0` and `net462`, **no dependencies**. |
| [`src/ConnectivityProbe.Monitor`](src/ConnectivityProbe.Monitor) | Central monitor (ASP.NET Core web app). Shows pods, versions, clusters and results; holds the connection definitions. **Not** a NuGet package. |
| [`samples/ConnectivityProbe.SampleApi`](samples/ConnectivityProbe.SampleApi) | Sample ASP.NET Core app. |
| [`tests/ConnectivityProbe.Tests`](tests/ConnectivityProbe.Tests) | Unit and end-to-end tests: `dotnet test tests/ConnectivityProbe.Tests` |

---

## Contents

- [How it works](#how-it-works)
- [Supported platforms](#supported-platforms)
- [Installation by platform](#installation-by-platform)
- [The app key](#the-app-key)
- [Versions and builds](#versions-and-builds)
- [Clusters](#clusters)
- [Console messages](#console-messages)
- [Options](#options)
- [Security](#security)
- [ConnectivityProbe Monitor](#connectivityprobe-monitor)
- [Upgrading from 1.x](#upgrading-from-1x)
- [Version history](#version-history)

---

## How it works

```
application starts ──►  ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)

every 10 s   ──►  POST {MonitorUrl}/api/agent/v2/report     (header X-ConnectivityProbe-AppKey)
                  "I am pod X of app <key>, version 1.4.0, cluster C"
                  ◄── connection list + test interval
every 30 s   ──►  test every connection from inside this pod (TCP)
                  └─► results go with the next report, immediately
on shutdown  ──►  POST {MonitorUrl}/api/agent/v2/goodbye    → the pod leaves the list without an alarm
```

- **No registration step.** The first pod that reports with a new key registers the application in the Monitor
  automatically. Pods with the same key are the same application; it is never registered twice.
- **No endpoint in your application.** The library opens no port and adds nothing to your HTTP pipeline. The pod only
  needs outbound HTTP(S) to the Monitor. Applications without HTTP (workers, queue consumers, Windows services) work the
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

The Monitor shows the version on every card and in the **Versions** tab, and highlights pods that run a different
version or build than the rest (for example during a rollout, or when a deploy only reached one cluster).

---

## Clusters

The Monitor groups pods by cluster **automatically**:

- **Kubernetes:** every pod has its cluster's CA certificate at
  `/var/run/secrets/kubernetes.io/serviceaccount/ca.crt`. The library sends a fingerprint of it (SHA-256, never the
  certificate itself), so all pods of the same cluster get the same cluster id. The namespace is read from the same folder.
- **Outside Kubernetes** (IIS, VMs): pods are grouped by the network address their reports come from.

New clusters appear as "Cluster 1", "Cluster 2", … and can be renamed in the **Versions** tab (e.g. "Prod Istanbul").

---

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

`ConnectivityProbeAgent.Current` returns the running agent (one per process); it exposes `LastContactUtc`, `LastRunUtc`,
`LastError` and `AppId`.

---

## Security

- The library opens **no port and no endpoint**. It only makes outbound HTTP(S) requests to `MonitorUrl`.
- It sends: pod name, machine name, process id, IP addresses, OS / .NET version, application name / version / build,
  cluster fingerprint, namespace and only these environment variables: `POD_NAME`, `POD_NAMESPACE`, `POD_IP`,
  `NODE_NAME`, `HOSTNAME`, `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, `APP_POOL_ID`. No other variable is ever read.
- The pod tests only the connections that are attached to its application in the Monitor.
- The Monitor is meant for internal networks: registration is open, and the app key identifies the application but is
  not a password. Protect the Monitor UI with `Monitor:AdminPassword` and publish it behind HTTPS.

---

## ConnectivityProbe Monitor

```bash
dotnet run --project src/ConnectivityProbe.Monitor
```

Default address: http://localhost:5087/ . The Monitor is a standalone application, not a NuGet package. Run one Monitor per
environment (test, prod).

**Screens**

- **Monitor:** the most critical application in a large banner, then a "Needs attention" row and one horizontally
  scrolling row per team. Each card shows its state, pod count, connection summary and version. Search by application,
  key, team, version, cluster or pod name.
- **Details window:** pods grouped by cluster (name, IPs, version, build, namespace, last report), a connection × pod
  matrix (which pod cannot reach a target, and since when), history and *Reset pod list*.
- **Versions:** clusters (renamable) and an applications × clusters table with the versions running in each cluster.
- **Definitions:** Units → Teams → Applications. Each team has its own connection pool; a common pool is shared by
  everyone. Drag a connection onto an application to attach it. A connection can point to another registered
  application ("target application"), which is shown in the matrix.

**Pod states**

| State | Meaning |
|---|---|
| Running | The pod reported within the last 2 × `PollSeconds` (+5 s). |
| Report late | The report is late, but the threshold is not reached yet. Not an alarm. |
| Missing | No report for `MissingAfterCycles` (3) test intervals while the pod count went down. **Alarm.** Removed only with *Reset pod list*; a pod that comes back returns to normal by itself. |
| (gone) | The pod said goodbye (deploy, scale-down), or it was replaced by a new pod while the count stayed the same. No alarm. |

**Settings** (`appsettings.json` → `Monitor`, or environment variables `Monitor__<Name>`)

| Setting | Default | Meaning |
|---|---|---|
| `AdminUser` / `AdminPassword` | `admin` / empty | UI login. Without a password the UI is only reachable from the Monitor's own machine (localhost). The agent endpoints (`/api/agent/*`) never require a login. |
| `IntervalSeconds` | 30 | Test interval sent to the pods. |
| `ProbeTimeoutMs` | 5000 | Connection timeout sent to the pods. |
| `MissingAfterCycles` | 3 | Test intervals without a report before a pod is "missing". |
| `DataFile` | `data/definitions.json` | Definitions. Pod state (`pod-state.json`) and login keys (`keys/`) are kept next to it. |

If a reverse proxy runs on the same machine, every request looks like localhost, so always set `AdminPassword`.

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
| **2.0.0** | One line: `ConnectivityProbeAgent.Start(monitorUrl, appKey, appName)`. Automatic registration, application version / build per pod, automatic cluster grouping, no endpoints, no dependencies. |
| 1.1.0 | Strict mode: pods pull their definitions with an app key, test from inside and report back. |
| 1.0.0 | First release: `discover` and `identity` endpoints. |

Full list: [CHANGELOG](src/ConnectivityProbe/CHANGELOG.md) · Releases: [GitHub Releases](https://github.com/umutmemisoglu/ConnectivityProbe/releases) ·
Publishing a new version: [PUBLISHING.md](PUBLISHING.md)

## License

[MIT](LICENSE) © 2026 Fatih Umut Memişoğlu
