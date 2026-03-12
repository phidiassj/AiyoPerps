FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AiyoPerps/AiyoPerps.csproj AiyoPerps/
RUN dotnet restore AiyoPerps/AiyoPerps.csproj -p:RestoreAdditionalProjectFallbackFolders=

COPY AiyoPerps/ AiyoPerps/
RUN dotnet publish AiyoPerps/AiyoPerps.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    -p:RestoreAdditionalProjectFallbackFolders=

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ENV AIYOPERPS_HTTP_PORT=5078
EXPOSE 5078

COPY --from=build /app/publish ./

VOLUME ["/app/db"]

ENTRYPOINT ["dotnet", "AiyoPerps.dll", "headless"]
