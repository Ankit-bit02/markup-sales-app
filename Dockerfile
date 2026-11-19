# Builds and runs MarkUp.Api. Build context = repository root (needs MarkUp.Shared too).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY MarkUp.Shared/MarkUp.Shared.csproj MarkUp.Shared/
COPY MarkUp.Api/MarkUp.Api.csproj MarkUp.Api/
RUN dotnet restore MarkUp.Api/MarkUp.Api.csproj
COPY MarkUp.Shared/ MarkUp.Shared/
COPY MarkUp.Api/ MarkUp.Api/
RUN dotnet publish MarkUp.Api/MarkUp.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
# Time zone data (Asia/Kathmandu) and basic fonts for PDF export.
RUN apt-get update \
 && apt-get install -y --no-install-recommends tzdata fontconfig fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000
# Run as the non-root user that ships with the .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "MarkUp.Api.dll"]
