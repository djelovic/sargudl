# syntax=docker/dockerfile:1

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached unless the project file changes).
COPY MiniDl.csproj ./
RUN dotnet restore

# Build and publish.
COPY . ./
RUN dotnet publish MiniDl.csproj -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# The aspnet base image listens on 8080 by default (ASPNETCORE_HTTP_PORTS=8080).
EXPOSE 8080

# Default download roots live under /app/downloads; mount volumes to persist
# them, and mount over /app/appsettings.json to supply host configuration.
ENTRYPOINT ["dotnet", "MiniDl.dll"]
