# Multi-stage build for AgentForge.Web.
# Build context is the AgentForge/ solution root; the compose file
# specifies that via `context: .` so the Web Dockerfile can reach the
# sibling RedTeam project it references.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files first to cache the restore layer.
COPY AgentForge.Web/AgentForge.Web.csproj         AgentForge.Web/
COPY AgentForge.RedTeam/AgentForge.RedTeam.csproj AgentForge.RedTeam/

RUN dotnet restore AgentForge.Web/AgentForge.Web.csproj

# Now copy the actual source plus the schema file (linked into the
# Web project so it ends up in the published output as db/001_schema.sql).
COPY AgentForge.Web/         AgentForge.Web/
COPY AgentForge.RedTeam/     AgentForge.RedTeam/
COPY db/                     db/

RUN dotnet publish AgentForge.Web/AgentForge.Web.csproj \
        -c Release \
        -o /app/publish \
        --no-restore

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AgentForge.Web.dll"]
