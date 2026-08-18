FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY HemisAudit/HemisAudit.csproj HemisAudit/
RUN dotnet restore HemisAudit/HemisAudit.csproj

COPY HemisAudit/ HemisAudit/
RUN dotnet publish HemisAudit/HemisAudit.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "HemisAudit.dll"]
