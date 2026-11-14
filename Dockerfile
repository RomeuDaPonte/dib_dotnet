# syntax=docker/dockerfile:1
# PLAN.md §12: the new app's image (UI + /api on one origin, port 3010). Production runs linux/amd64 (NAS).
#   docker build --platform linux/amd64 -t dib4t-dotnet:1.0.0 .
# The SDK stage runs on the build machine's own platform and cross-publishes for the target architecture.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first (cached until a project file or package version changes).
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Dib4t.SharedKernel/Dib4t.SharedKernel.csproj src/Dib4t.SharedKernel/
COPY src/Dib4t.Domain/Dib4t.Domain.csproj src/Dib4t.Domain/
COPY src/Dib4t.Application/Dib4t.Application.csproj src/Dib4t.Application/
COPY src/Dib4t.Infrastructure/Dib4t.Infrastructure.csproj src/Dib4t.Infrastructure/
COPY src/Dib4t.Contracts/Dib4t.Contracts.csproj src/Dib4t.Contracts/
COPY src/Dib4t.Client/Dib4t.Client.csproj src/Dib4t.Client/
COPY src/Dib4t.Server/Dib4t.Server.csproj src/Dib4t.Server/
RUN dotnet restore src/Dib4t.Server/Dib4t.Server.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/Dib4t.Server/Dib4t.Server.csproj -c Release -a $TARGETARCH --no-restore \
      -p:UseAppHost=false -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
# /app/logs: SQLite log store (dib4t-logs volume, §3.3). /app/backups: scheduled backups (§12).
RUN mkdir -p /app/logs /app/backups && chown "$APP_UID" /app/logs /app/backups
COPY --from=build /app/publish .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=3010 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 3010
VOLUME ["/app/logs"]
ENTRYPOINT ["dotnet", "Dib4t.Server.dll"]
