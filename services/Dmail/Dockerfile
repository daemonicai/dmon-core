# Build from the repository root:
#   docker build -f services/Dmail/Dockerfile -t dmail .
# (the build context is the repo root so the central-package-management props are available)

# ---- SDK Build Stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore packages. Central Package Management means the repo-root props +
# nuget.config drive every version, so they must be present before restore.
COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY services/Dmail/Dmail.csproj services/Dmail/
RUN dotnet restore services/Dmail/Dmail.csproj

# Copy source and publish. MinVer is skipped: this is an unversioned app image.
COPY services/Dmail/ services/Dmail/
RUN dotnet publish services/Dmail/Dmail.csproj -c Release -o /app/publish --no-restore -p:MinVerSkip=true

# ---- Runtime Stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is required by the HEALTHCHECK below and is not present in the aspnet runtime image
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Copy published output
COPY --from=build /app/publish .

# Copy ONNX model and vocabulary file (both required by AddBertOnnxEmbeddingGenerator)
COPY services/Dmail/models/bge-micro-v2.onnx /app/models/bge-micro-v2.onnx
COPY services/Dmail/models/vocab.txt /app/models/vocab.txt

# Create data directory
RUN mkdir -p /data

ENV DMAIL_DATA_DIR=/data
ENV DMAIL_PORT=8080

# The container's network namespace is the security boundary: binding all
# interfaces inside the container is safe because only the mapped host port
# (published to host loopback, see docker-compose.yml) is reachable from outside.
ENV DMAIL_ALLOW_NONLOOPBACK=true
ENV DMAIL_BIND_ADDRESS=http://+:8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Dmail.dll"]
