FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY HemisAudit.csproj .
RUN dotnet restore HemisAudit.csproj -r linux-x64

COPY . .
# ReadyToRun pre-JITs the app to native code at build time instead of leaving the runtime to
# JIT-compile everything the first time each code path is hit. Startup-time-only change, no
# behavior difference - directly targets cold-start latency, which matters most on a resource-
# constrained instance where every deploy (and this app deploys often) starts fully cold.
RUN dotnet publish HemisAudit.csproj -c Release -o /app --no-restore -r linux-x64 --self-contained false -p:PublishReadyToRun=true

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "HemisAudit.dll"]
