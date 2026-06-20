# ---- SDK Build Stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore packages
COPY src/Dmail/Dmail.csproj src/Dmail/
RUN dotnet restore src/Dmail/Dmail.csproj

# Copy source and publish (preserve the src/ prefix so source sits beside the
# restored csproj at /src/src/Dmail/, matching the publish path below)
COPY src/ src/
RUN dotnet publish src/Dmail/Dmail.csproj -c Release -o /app/publish --no-restore

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
COPY models/bge-micro-v2.onnx /app/models/bge-micro-v2.onnx
COPY models/vocab.txt /app/models/vocab.txt

# Create data directory
RUN mkdir -p /data

ENV DMAIL_DATA_DIR=/data
ENV DMAIL_PORT=8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Dmail.dll"]
