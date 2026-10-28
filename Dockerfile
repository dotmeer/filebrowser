# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore layer: only project files, for better caching.
COPY dotmeer.filebrowser.slnx ./
COPY src/dotmeer.filebrowser/dotmeer.filebrowser.csproj src/dotmeer.filebrowser/
COPY tests/dotmeer.filebrowser.Tests/dotmeer.filebrowser.Tests.csproj tests/dotmeer.filebrowser.Tests/
RUN dotnet restore dotmeer.filebrowser.slnx

COPY . .
RUN dotnet publish src/dotmeer.filebrowser/dotmeer.filebrowser.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Non-privileged user provided by the aspnet image (UID 1654).
# The user can be overridden at run time with --user / user: in compose.
USER $APP_UID

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "dotmeer.filebrowser.dll"]