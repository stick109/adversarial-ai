# Multi-stage build for SecurityAnalyzer.Executor (mirrors SecurityAnalyzer.Web).
# Build context is the SecurityAnalyzer/ solution root; the compose file
# specifies that via `context: .`.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj first to cache the restore layer.
COPY SecurityAnalyzer.Executor/SecurityAnalyzer.Executor.csproj SecurityAnalyzer.Executor/

RUN dotnet restore SecurityAnalyzer.Executor/SecurityAnalyzer.Executor.csproj

COPY SecurityAnalyzer.Executor/ SecurityAnalyzer.Executor/

RUN dotnet publish SecurityAnalyzer.Executor/SecurityAnalyzer.Executor.csproj \
        -c Release \
        -o /app/publish \
        --no-restore

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SecurityAnalyzer.Executor.dll"]
