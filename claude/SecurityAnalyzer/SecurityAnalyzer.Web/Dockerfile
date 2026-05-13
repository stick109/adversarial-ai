# Multi-stage build for SecurityAnalyzer.Web.
# Build context is the SecurityAnalyzer/ solution root; the compose file
# specifies that via `context: .` so the Web Dockerfile can reach the
# sibling RedTeam project it references.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files first to cache the restore layer.
COPY SecurityAnalyzer.Web/SecurityAnalyzer.Web.csproj         SecurityAnalyzer.Web/
COPY SecurityAnalyzer.RedTeam/SecurityAnalyzer.RedTeam.csproj SecurityAnalyzer.RedTeam/

RUN dotnet restore SecurityAnalyzer.Web/SecurityAnalyzer.Web.csproj

# Now copy the actual source plus the schema file (linked into the
# Web project so it ends up in the published output as db/001_schema.sql).
COPY SecurityAnalyzer.Web/         SecurityAnalyzer.Web/
COPY SecurityAnalyzer.RedTeam/     SecurityAnalyzer.RedTeam/
COPY db/                     db/

RUN dotnet publish SecurityAnalyzer.Web/SecurityAnalyzer.Web.csproj \
        -c Release \
        -o /app/publish \
        --no-restore

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SecurityAnalyzer.Web.dll"]
