FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY server/CineVault.csproj server/
RUN dotnet restore server/CineVault.csproj
COPY server/ server/
COPY dist/ dist/
RUN dotnet publish server/CineVault.csproj -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /publish/ ./
RUN mkdir -p /data /downloads && chown -R app:app /data /downloads
ENV ASPNETCORE_URLS=http://+:8080 \
    DataDirectory=/data \
    DownloadDirectory=/downloads \
    LocalOnly=false
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["dotnet", "CineVault.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "CineVault.dll"]
