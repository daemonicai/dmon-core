using Dmon.Protocol;

string outputPath = args.Length > 0
    ? args[0]
    : Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "docs", "protocol", "schema.json");

outputPath = Path.GetFullPath(outputPath);

string? dir = Path.GetDirectoryName(outputPath);
if (dir is not null)
    Directory.CreateDirectory(dir);

string schema = ProtocolSchemaExporter.ExportAsJson();
await File.WriteAllTextAsync(outputPath, schema).ConfigureAwait(false);

Console.WriteLine($"Schema written to: {outputPath}");
