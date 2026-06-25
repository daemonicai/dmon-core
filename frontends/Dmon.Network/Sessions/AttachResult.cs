namespace Dmon.Network.Sessions;

/// <summary>
/// Returned by <see cref="SessionHandler.Attach"/> — carries the new generation and the
/// current head sequence number so the caller can populate the <c>attached</c> reply atomically.
/// </summary>
public readonly record struct AttachResult(long Generation, long HeadSeq);
