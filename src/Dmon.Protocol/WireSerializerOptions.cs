using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Dmon.Protocol;

/// <summary>
/// The single canonical <see cref="JsonSerializerOptions"/> for the dmon wire protocol.
/// All serialization that produces or consumes ADR-003 frames MUST use this instance so that
/// the schema exported by <see cref="ProtocolSchemaExporter"/> describes the bytes actually
/// emitted on the wire.
///
/// Settings:
///   - camelCase property names (matching the ADR-003 wire shape)
///   - <c>AllowOutOfOrderMetadataProperties</c> — commands carry <c>"id"</c> before <c>"type"</c>,
///     which is the JSON polymorphism discriminator; the deserializer must tolerate out-of-order
///     discriminators to parse them correctly
///   - <c>WhenWritingNull</c> omission — events omit optional null fields on the wire
///   - <c>DefaultJsonTypeInfoResolver</c> — required by both <c>JsonSchemaExporter</c> (export path) and reflection-based polymorphic dispatch (runtime path)
/// </summary>
public static class WireSerializerOptions
{
    /// <summary>
    /// Canonical options for the dmon wire protocol. Shared by the event emitter, command
    /// dispatcher, control-frame serializer, and the schema exporter.
    /// </summary>
    public static readonly JsonSerializerOptions Default = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowOutOfOrderMetadataProperties = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.MakeReadOnly();
        return options;
    }
}
