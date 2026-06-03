using System.Text.Json.Serialization;

namespace Dmon.Protocol.Conversation;

/// <summary>
/// Discriminated union of all line types that may appear in a session's messages.jsonl.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MessageRecord), typeDiscriminator: "message")]
[JsonDerivedType(typeof(CompactionMessage), typeDiscriminator: "compaction")]
public abstract record SessionLogLine
{
}
