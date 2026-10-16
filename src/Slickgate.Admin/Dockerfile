# syntax=docker/dockerfile:1

# Standalone Slickgate admin UI.
#
# Builds the Blazor WebAssembly app and serves the static output with nginx.
# The UI talks to the gateway only over its admin HTTP API, so it is deployed
# as an entirely separate container/service.
#
#   docker build -f src/Slickgate.Admin/Dockerfile -t slickgate-admin .

ARG DOTNET_VERSION=11.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Slickgate.Admin/Slickgate.Admin.csproj src/Slickgate.Admin/
RUN dotnet restore src/Slickgate.Admin/Slickgate.Admin.csproj
COPY src/Slickgate.Admin/ src/Slickgate.Admin/
RUN dotnet publish src/Slickgate.Admin/Slickgate.Admin.csproj \
        -c Release --no-restore -o /app/publish

FROM nginx:alpine AS final
COPY src/Slickgate.Admin/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html

# `dotnet publish` emits both .gz and .br sidecars (CompressionEnabled). The
# official nginx image has no brotli module, so the .br files could never be
# served - drop them instead of shipping dead weight. .gz is served via the
# `gzip_static on` directive in nginx.conf.
RUN find /usr/share/nginx/html -name '*.br' -delete

EXPOSE 80
