# The API image. Build from the repository root:
#   docker build -f build/docker/Dockerfile -t api:ci .
#
# Configuration is environment variables only; no secret is baked into the image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore layer cache. Dockerfile COPY cannot glob and keep the folder layout, so each project's .csproj and
# packages.lock.json is copied to its own folder, one COPY per project. Only the projects the Api references are
# listed (the test projects are not needed, and .dockerignore drops tests/), so restoring the Api project restores
# all of them. The layers up to the restore are rebuilt only when a project file, a lock file, a root Directory.* file,
# global.json or Version.props changes; Version.props is imported by Directory.Build.props, so it must be here.
# src/Directory.Build.props is not in this layer (it arrives with COPY src/ below and holds compile-time items only), so it must
# stay free of properties that change what restore resolves.
# The solution file is left out on purpose: it changes whenever a test project is added and would bust the cache.
# Add a COPY line when a project is added to the Api's references; the restore below fails loudly if one is missing.
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props Version.props ./
COPY src/BuildingBlocks/TemplateName.SharedKernel/TemplateName.SharedKernel.csproj src/BuildingBlocks/TemplateName.SharedKernel/packages.lock.json src/BuildingBlocks/TemplateName.SharedKernel/
COPY src/BuildingBlocks/TemplateName.Application.Common/TemplateName.Application.Common.csproj src/BuildingBlocks/TemplateName.Application.Common/packages.lock.json src/BuildingBlocks/TemplateName.Application.Common/
COPY src/BuildingBlocks/TemplateName.Infrastructure.Common/TemplateName.Infrastructure.Common.csproj src/BuildingBlocks/TemplateName.Infrastructure.Common/packages.lock.json src/BuildingBlocks/TemplateName.Infrastructure.Common/
COPY src/BuildingBlocks/TemplateName.Web.Common/TemplateName.Web.Common.csproj src/BuildingBlocks/TemplateName.Web.Common/packages.lock.json src/BuildingBlocks/TemplateName.Web.Common/
#if (caching == "redis")
COPY src/BuildingBlocks/TemplateName.Platform.Redis/TemplateName.Platform.Redis.csproj src/BuildingBlocks/TemplateName.Platform.Redis/packages.lock.json src/BuildingBlocks/TemplateName.Platform.Redis/
#endif
COPY src/Modules/Auth/TemplateName.Modules.Auth.Contracts/TemplateName.Modules.Auth.Contracts.csproj src/Modules/Auth/TemplateName.Modules.Auth.Contracts/packages.lock.json src/Modules/Auth/TemplateName.Modules.Auth.Contracts/
COPY src/Modules/Auth/TemplateName.Modules.Auth/TemplateName.Modules.Auth.csproj src/Modules/Auth/TemplateName.Modules.Auth/packages.lock.json src/Modules/Auth/TemplateName.Modules.Auth/
COPY src/Modules/Notifications/TemplateName.Modules.Notifications/TemplateName.Modules.Notifications.csproj src/Modules/Notifications/TemplateName.Modules.Notifications/packages.lock.json src/Modules/Notifications/TemplateName.Modules.Notifications/
#if (includeSample)
COPY src/Modules/Sample/TemplateName.Modules.Sample/TemplateName.Modules.Sample.csproj src/Modules/Sample/TemplateName.Modules.Sample/packages.lock.json src/Modules/Sample/TemplateName.Modules.Sample/
#endif
COPY src/Modules/Audit/TemplateName.Modules.Audit/TemplateName.Modules.Audit.csproj src/Modules/Audit/TemplateName.Modules.Audit/packages.lock.json src/Modules/Audit/TemplateName.Modules.Audit/
COPY src/Host/TemplateName.Api/TemplateName.Api.csproj src/Host/TemplateName.Api/packages.lock.json src/Host/TemplateName.Api/
RUN dotnet restore src/Host/TemplateName.Api/TemplateName.Api.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/Host/TemplateName.Api/TemplateName.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

# chiseled-extra: distroless (no shell, no package manager) and carries ICU, which the globalised
# (en, ms, zh-Hans) error messages need.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .

# HTTP on 8080 only. TLS ends at the proxy. Do NOT set HTTPS_PORT, ASPNETCORE_HTTPS_PORT(S) or an https URL here:
# the host enables HTTPS redirection outside Development once an https port is configured, and the HEALTHCHECK
# below (which does not follow redirects) would then get a 307 and report the container unhealthy (307).
# AllowedHosts must list 127.0.0.1 beside the public names: the HEALTHCHECK sends Host 127.0.0.1:<port>.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The image's built-in non-root user (APP_UID is set by the base image).
USER $APP_UID

ENTRYPOINT ["dotnet", "TemplateName.Api.dll"]
# The binary's own `healthcheck` mode GETs /health/live on the first port of ASPNETCORE_HTTP_PORTS; the image has
# no shell, curl or wget.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "TemplateName.Api.dll", "healthcheck"]
