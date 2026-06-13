# ---- SDK Build Stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore packages
COPY src/AIMail/AIMail.csproj src/AIMail/
RUN dotnet restore src/AIMail/AIMail.csproj

# Copy source and publish
COPY src/ .
RUN dotnet publish src/AIMail/AIMail.csproj -c Release -o /app/publish --no-restore

# ---- Runtime Stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Copy published output
COPY --from=build /app/publish .

# Copy ONNX model and vocabulary file (both required by AddBertOnnxEmbeddingGenerator)
COPY models/bge-micro-v2.onnx /app/models/bge-micro-v2.onnx
COPY models/vocab.txt /app/models/vocab.txt

# Create data directory
RUN mkdir -p /data

ENV AIM_DATA_DIR=/data
ENV AIM_PORT=8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "AIMail.dll"]
