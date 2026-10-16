# syntax=docker/dockerfile:1

# Slickgate container image.
#
# Two flavors are available:
#   * Default (managed/JIT)  -> docker build -t slickgate .
#   * Native AOT (small/fast) -> docker build --target aot -t slickgate:aot .
#
# The managed image is the default (last stage) so a plain `docker build` works.

ARG DOTNET_VERSION=11.0

# ---------------------------------------------------------------------------
# Managed (JIT) build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Slickgate.Core/Slickgate.Core.csproj src/Slickgate.Core/
COPY src/Slickgate.Gateway/Slickgate.Gateway.csproj src/Slickgate.Gateway/
RUN dotnet restore src/Slickgate.Gateway/Slickgate.Gateway.csproj
COPY src/ src/
RUN dotnet publish src/Slickgate.Gateway/Slickgate.Gateway.csproj \
        -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---------------------------------------------------------------------------
# Native AOT build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-aot AS publish-aot
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Slickgate.Core/Slickgate.Core.csproj src/Slickgate.Core/
COPY src/Slickgate.Gateway/Slickgate.Gateway.csproj src/Slickgate.Gateway/
RUN dotnet restore src/Slickgate.Gateway/Slickgate.Gateway.csproj -r linux-x64 -p:SlickgateAot=true
COPY src/ src/
RUN dotnet publish src/Slickgate.Gateway/Slickgate.Gateway.csproj \
        -c Release -r linux-x64 -p:SlickgateAot=true --self-contained true --no-restore -o /app/publish \
        --verbosity normal \
    && echo "=== Files in publish directory ===" \
    && ls -la /app/publish/ \
    && echo "=== Checking for executable ===" \
    && find /app/publish -type f -name "*" -exec file {} \; | head -20

# ---------------------------------------------------------------------------
# Native AOT runtime image (docker build --target aot)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION} AS aot
WORKDIR /app
RUN groupadd --system --gid 10001 slickgate \
 && useradd --system --uid 10001 --gid slickgate --home-dir /app --shell /usr/sbin/nologin slickgate \
 && mkdir -p /etc/slickgate /data \
 && chown -R slickgate:slickgate /app /data
COPY --from=publish-aot --chown=slickgate:slickgate /app/publish/ ./
# Debug: list all files and their permissions
RUN echo "=== Debug: Files in /app ===" && ls -la /app/
# Try to find any executable file
RUN find /app -type f -executable -print
# Fallback: if no Slickgate.Gateway, check for any file that could be the executable
RUN if [ ! -f "/app/Slickgate.Gateway" ]; then \
      echo "Slickgate.Gateway not found, looking for alternatives..." && \
      # Check for files without extension (common for Linux executables) \
      for f in /app/*; do \
        if [ -f "$f" ] && [[ ! "$f" =~ \. ]]; then \
          echo "Found potential executable (no extension): $f" && \
          ln -sf "$f" /app/Slickgate.Gateway && \
          break; \
        fi; \
      done; \
    fi
# Ensure the file is executable
RUN chmod +x /app/Slickgate.Gateway 2>/dev/null || true
COPY --chown=slickgate:slickgate samples/docker/gateway.yaml /etc/slickgate/gateway.yaml
ENV HOME=/app \
    SLICKGATE_CONFIG=/etc/slickgate/gateway.yaml \
    DOTNET_EnableDiagnostics=0
USER slickgate
EXPOSE 80 443
VOLUME ["/data"]
ENTRYPOINT ["/app/Slickgate.Gateway"]
CMD ["--config", "/etc/slickgate/gateway.yaml"]

# ---------------------------------------------------------------------------
# Managed (JIT) runtime image (default)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS final
WORKDIR /app
RUN mkdir -p /etc/slickgate /data && chown -R 1654:1654 /app /data
COPY --from=build --chown=1654:1654 /app/publish/ ./
COPY --chown=1654:1654 samples/docker/gateway.yaml /etc/slickgate/gateway.yaml
ENV HOME=/app \
    SLICKGATE_CONFIG=/etc/slickgate/gateway.yaml \
    DOTNET_EnableDiagnostics=0
USER 1654:1654
EXPOSE 80 443
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -qO- "http://127.0.0.1:${SLICKGATE_HTTP_PORT:-80}/-/health" >/dev/null 2>&1 || exit 1
ENTRYPOINT ["dotnet", "Slickgate.Gateway.dll"]
