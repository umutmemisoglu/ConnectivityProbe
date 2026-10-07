# ConnectivityProbe

Test, **from inside every pod / server of your application**, whether it can reach its databases, queues and APIs, and
see **how many pods really run, in which cluster, with which version**. One line of code, no endpoints, no dependencies.

📖 Full documentation: **[English](https://github.com/umutmemisoglu/ConnectivityProbe/blob/main/README.md)** ·
**[Türkçe](https://github.com/umutmemisoglu/ConnectivityProbe/blob/main/README.tr.md)**

## Quick start

```bash
dotnet add package ConnectivityProbe
```

Once, when the application starts (ASP.NET Core, Worker Service, console, IIS `Application_Start`, Windows service):

```csharp
ConnectivityProbe.ConnectivityProbeAgent.Start(
    monitorUrl: "https://monitor.example.com",   // ConnectivityProbe Monitor of this environment
    appKey:     "orders-api",                    // chosen by you; the first pod registers the app
    appName:    "Orders API");                   // optional display name
```

That's all. Every pod then:

- reports to the Monitor every 10 s (registering the application automatically on first contact),
- pulls the connections attached to its application in the Monitor and tests them over TCP from inside the pod,
- reports your application's **version and build** (from its assembly) and its **cluster** (Kubernetes service-account
  CA fingerprint and pod network, e.g. `10.42.0.0/16`),
- reports its **resource usage**: CPU, memory, threads; in a container also CPU / memory **limits, throttling, OOM kills**
  and TCP sockets (2.1+),
- checks **TLS certificates** (expiry, name, trust) of `https://` / TLS targets and measures DNS time (2.1+),
- says goodbye on shutdown, so deploys and scale-downs raise no alarm.

If the Monitor cannot be reached, your application is not affected: the library writes a short English line to the
console (`[ConnectivityProbe] Could not connect to monitor ...`) and keeps trying.

Targets: `netstandard2.0` (.NET Core 2.0 – .NET 10) and `net462` (.NET Framework 4.6.2+). No NuGet dependencies.

> **2.0 is a breaking release.** The `discover` / `identity` endpoints, `UseConnectivityProbe()`, the IIS module and
> Discover mode were removed. See *Upgrading from 1.x* in the documentation.

---

## Türkçe özet

Uygulamanızın **her pod'unun / sunucusunun içinden** veritabanlarına, kuyruklara ve API'lere erişebildiğini test eder;
**kaç pod'un, hangi cluster'da, hangi sürümle** çalıştığını gösterir. Tek satır kod, uç yok, bağımlılık yok.

```csharp
ConnectivityProbe.ConnectivityProbeAgent.Start("https://monitor.example.com", "orders-api", "Orders API");
```

Anahtarı siz belirlersiniz; ilk pod uygulamayı Monitor'e kendiliğinden kaydeder. Bağlantılar Monitor'de tanımlanır, her pod
kendi içinden test eder. Monitor'e ulaşılamazsa uygulama etkilenmez, konsola kısa bir İngilizce mesaj yazılır. Ayrıntılı
anlatım: **[Türkçe dokümantasyon](https://github.com/umutmemisoglu/ConnectivityProbe/blob/main/README.tr.md)**.

License / Lisans: MIT © 2026 Fatih Umut Memişoğlu
