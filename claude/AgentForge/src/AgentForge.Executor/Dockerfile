# Multi-stage build for AgentForge.Executor (mirrors AgentForge.Web).
# Build context is the AgentForge/ solution root; the compose file
# specifies that via `context: .` so this Dockerfile can reach the
# sibling AgentForge.Harness project it references.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files first to cache the restore layer.
COPY src/AgentForge.Executor/AgentForge.Executor.csproj src/AgentForge.Executor/
COPY src/AgentForge.Harness/AgentForge.Harness.csproj   src/AgentForge.Harness/

RUN dotnet restore src/AgentForge.Executor/AgentForge.Executor.csproj

COPY src/AgentForge.Executor/ src/AgentForge.Executor/
COPY src/AgentForge.Harness/  src/AgentForge.Harness/

RUN dotnet publish src/AgentForge.Executor/AgentForge.Executor.csproj \
        -c Release \
        -o /app/publish \
        --no-restore

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AgentForge.Executor.dll"]
