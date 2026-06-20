using Dmail;
using Dmail.Data;
using Dmail.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

#pragma warning disable SKEXP0070

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration ----
var dataDir = builder.Configuration["DMAIL_DATA_DIR"] ?? "/data";
var port = builder.Configuration["DMAIL_PORT"] ?? "8080";
var keysDir = Path.Combine(dataDir, "keys");
Directory.CreateDirectory(keysDir);
var backfillMonths = builder.Configuration.GetValue<int>("DMAIL_BACKFILL_MONTHS", 1);

builder.WebHost.UseUrls($"http://+:{port}");

// ---- SQLite Connection ----
// Per-operation pooled connections (B3): Microsoft.Data.Sqlite pools by connection
// string, so each operation opens/uses/disposes its own connection. The SK SqliteVec
// store is registered with the SAME connection string so it shares the pool/file.
var dbPath = Path.Combine(dataDir, "dmail.db");
var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = dbPath,
    Pooling = true
}.ToString();
builder.Services.AddSingleton<ISqliteConnectionFactory>(
    new SqliteConnectionFactory(connectionString));
builder.Services.AddSingleton<DatabaseInitializer>();

// ---- SqliteVec Vector Store ----
builder.Services.AddSqliteVectorStore(_ => connectionString);

// ---- ONNX Embedding ----
var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "bge-micro-v2.onnx");
var vocabPath = Path.Combine(AppContext.BaseDirectory, "models", "vocab.txt");
builder.Services.AddBertOnnxEmbeddingGenerator(modelPath, vocabPath);

builder.Services.AddSingleton<EmbeddingService>();

// ---- Services ----
builder.Services.AddSingleton<TokenProtectionService>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<EmailRepository>();
builder.Services.AddSingleton<VectorStoreService>();
builder.Services.AddSingleton<HybridSearchService>();
builder.Services.AddSingleton<OAuth2StateStore>();
builder.Services.AddSingleton<OAuth2Service>();
builder.Services.AddHttpClient("google");

// ---- Data Protection (token encryption) ----
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("dmail");

// ---- Channels ----
builder.Services.AddSingleton(
    System.Threading.Channels.Channel.CreateBounded<Dmail.Models.Email>(
        new System.Threading.Channels.BoundedChannelOptions(100)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
        }));

// ---- Background Services ----
builder.Services.AddSingleton<ImapWatcherManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ImapWatcherManager>());
builder.Services.AddHostedService<EmailProcessingService>();

var app = builder.Build();

// Initialize database (WAL mode, schema, FTS5)
var dbInit = app.Services.GetRequiredService<DatabaseInitializer>();
await dbInit.InitializeAsync();

// Ensure the vec0 virtual table exists before any background processing runs.
// GetCollection() only returns a handle; the table is created here, once, at boot.
var vectorStoreService = app.Services.GetRequiredService<VectorStoreService>();
await vectorStoreService.EnsureCollectionAsync();

// Task 4.5: Validate ONNX model at startup
var embedding = app.Services.GetRequiredService<EmbeddingService>();
await embedding.ValidateModelAsync();

// Map endpoints
app.MapHealthHandlers();
app.MapSearchHandlers();
app.MapEmailHandlers();
app.MapAccountHandlers();
app.MapAuthHandlers();

// Static files for dashboard
app.UseDefaultFiles();
app.UseStaticFiles();

await app.RunAsync();
