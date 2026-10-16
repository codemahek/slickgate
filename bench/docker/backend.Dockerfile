# syntax=docker/dockerfile:1

# Benchmark origin server: returns the same fixed 1 KiB payload for every request
# so the only variable between runs is the proxy in front of it.

ARG DOTNET_VERSION=11.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY bench/backend/Backend.csproj bench/backend/
RUN dotnet restore bench/backend/Backend.csproj
COPY bench/backend/ bench/backend/
RUN dotnet publish bench/backend/Backend.csproj \
        -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS final
WORKDIR /app
COPY --from=build /app/publish/ ./
ENV DOTNET_EnableDiagnostics=0
EXPOSE 8088
ENTRYPOINT ["dotnet", "Backend.dll"]
