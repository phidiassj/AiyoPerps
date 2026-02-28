FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AiyoPerps.slnx ./
COPY AiyoPerps/AiyoPerps.csproj AiyoPerps/
RUN dotnet restore AiyoPerps.slnx -p:RestoreAdditionalProjectFallbackFolders=

COPY . .
RUN dotnet publish AiyoPerps/AiyoPerps.csproj \
    -c Release \
    -o /app/publish \
    -p:RestoreAdditionalProjectFallbackFolders=

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ENV AIYOPERPS_HTTP_PORT=5078
EXPOSE 5078

COPY --from=build /app/publish ./

VOLUME ["/app/db"]

ENTRYPOINT ["dotnet", "AiyoPerps.dll", "headless"]
