# Connectivity Monitor container imajı.
#
# Monitor kütüphaneyi kaynak koddan kullandığı için derleme deponun kökünden yapılır:
#   docker build -f src/ConnectivityProbe.Monitor/Dockerfile -t connectivity-monitor .
#
# Çalıştırma (ayrıntılar: README → Docker):
#   docker run -d -p 8080:8080 -v cpmonitor-data:/app/data -e Monitor__AdminPassword=<şifre> connectivity-monitor
#
# - Port 8080 (http). TLS'i önündeki ingress / proxy sonlandırır; X-Forwarded-Proto / X-Forwarded-Host dikkate alınır.
# - Kalıcı veri /app/data altındadır (tanımlar, pod durumu, ayarlar, oturum anahtarları): mutlaka bir volume bağlayın.
# - Container root olmayan "app" kullanıcısıyla çalışır.

# --- derleme ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Önce yalnızca proje dosyaları: kod değişse de paket indirme katmanı önbellekten gelir.
COPY src/ConnectivityProbe/ConnectivityProbe.csproj src/ConnectivityProbe/
COPY src/ConnectivityProbe.Monitor/ConnectivityProbe.Monitor.csproj src/ConnectivityProbe.Monitor/
RUN dotnet restore src/ConnectivityProbe.Monitor/ConnectivityProbe.Monitor.csproj

COPY src/ConnectivityProbe/ src/ConnectivityProbe/
COPY src/ConnectivityProbe.Monitor/ src/ConnectivityProbe.Monitor/
RUN dotnet publish src/ConnectivityProbe.Monitor/ConnectivityProbe.Monitor.csproj \
        -c Release -o /app/publish --no-restore -p:UseAppHost=false

# --- çalışma ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build /app/publish .

# Veri klasörü "app" kullanıcısına ait olmalı (volume ilk bağlandığında bu sahiplik kopyalanır).
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
VOLUME /app/data

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ConnectivityProbe.Monitor.dll"]
