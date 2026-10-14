FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MapGame/MapGame.csproj MapGame/
RUN dotnet restore MapGame/MapGame.csproj
COPY MapGame/ MapGame/
RUN dotnet publish MapGame/MapGame.csproj -c Release --no-restore -p:UseSharedCompilation=false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production \
    Hosting__Mode=Internet \
    Hosting__FileLogs=false \
    PORT=10000 \
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 10000
ENTRYPOINT ["dotnet", "MapGame.dll"]
