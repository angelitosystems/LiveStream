# Backend del marketplace de LiveStream. Contexto de construcción: la raíz del repositorio.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/LiveStream.PluginSdk/ src/LiveStream.PluginSdk/
COPY marketplace/server/ marketplace/server/
RUN dotnet publish marketplace/server/LiveStream.Marketplace.Server.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Datos persistentes (índice, historial y paquetes) en un volumen; el proceso no corre como root.
RUN mkdir -p /data && chown app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    PORT=8080 \
    DATA_DIR=/data \
    DOTNET_gcServer=0
EXPOSE 8080
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["dotnet", "LiveStream.Marketplace.Server.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "LiveStream.Marketplace.Server.dll"]
