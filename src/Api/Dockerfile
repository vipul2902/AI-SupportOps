# syntax=docker/dockerfile:1
# Multi-stage build: the SDK image compiles; a small Alpine runtime image runs it as non-root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so this layer is cached until dependencies change.
COPY global.json Directory.Build.props ./
COPY src/Domain/AISupportOps.Domain.csproj src/Domain/
COPY src/Application/AISupportOps.Application.csproj src/Application/
COPY src/Infrastructure/AISupportOps.Infrastructure.csproj src/Infrastructure/
COPY src/Api/AISupportOps.Api.csproj src/Api/
RUN dotnet restore src/Api/AISupportOps.Api.csproj

COPY src/ src/
RUN dotnet publish src/Api/AISupportOps.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app .
# Writable upload directory owned by the non-root app user (a named volume inherits this ownership).
RUN mkdir -p /app/data/uploads && chown -R $APP_UID /app/data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/health/live >/dev/null || exit 1
ENTRYPOINT ["dotnet", "AISupportOps.Api.dll"]
