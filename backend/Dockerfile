# ---- build stage: full SDK (big) ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
# Copy ONLY the project files first and restore: Docker caches this layer until a .csproj changes,
# so day-to-day code edits do not re-download NuGet packages.
COPY global.json Directory.Build.props ShopFlow.sln ./
COPY src/ShopFlow.Domain/*.csproj src/ShopFlow.Domain/
COPY src/ShopFlow.Application/*.csproj src/ShopFlow.Application/
COPY src/ShopFlow.Infrastructure/*.csproj src/ShopFlow.Infrastructure/
COPY src/ShopFlow.Api/*.csproj src/ShopFlow.Api/
RUN dotnet restore src/ShopFlow.Api/ShopFlow.Api.csproj
COPY src/ src/
RUN dotnet publish src/ShopFlow.Api/ShopFlow.Api.csproj -c Release -o /app --no-restore

# ---- runtime stage: tiny ASP.NET image, no SDK, non-root ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShopFlow.Api.dll"]