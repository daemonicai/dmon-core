using Microsoft.Extensions.VectorData;

namespace Dmail.Models;

public sealed class EmailVector
{
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    /// <summary>384-dimension embedding from bge-micro-v2</summary>
    [VectorStoreVector(384)]
    public ReadOnlyMemory<float> Vector { get; set; }

    public static string BuildId(string account, uint uid) => $"{account}:{uid}";
}
