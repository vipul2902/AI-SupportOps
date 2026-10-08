# Multi-stage build: SDK image compiles, small runtime image runs as non-root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the layer is cached until project files change.
COPY global.json Directory.Build.props ./
COPY src/Domain/AISupportOps.Domain.csproj src/Domain/
COPY src/Application/AISupportOps.Application.csproj src/Application/
COPY src/Infrastructure/AISupportOps.Infrastructure.csproj src/Infrastructure/
COPY src/Api/AISupportOps.Api.csproj src/Api/
RUN dotnet restore src/Api/AISupportOps.Api.csproj

COPY src/ src/
RUN dotnet publish src/Api/AISupportOps.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AISupportOps.Api.dll"]
