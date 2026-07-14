// Stub core for CoreProcessManager stop/kill tests.
//
// It holds an exclusive session-directory lock exactly like Dmon.Core.Session.SessionLock
// (replicated flags, not referenced, to keep the fixture dependency-free), emits the
// agentReady readiness handshake, then ignores stdin and blocks forever so the graceful
// 500 ms WaitForExitAsync in CoreProcessManager.StopAsync times out and the forced-kill
// path runs.

using System;
using System.IO;
using System.Threading;

FileStream lockFile = new(
    Path.Combine(Environment.CurrentDirectory, ".lock"),
    FileMode.OpenOrCreate,
    FileAccess.ReadWrite,
    FileShare.None);

// Emit the readiness handshake only after the lock is held so the test never issues
// StopAsync before the OS single-writer lock is in place.
Console.Out.WriteLine("""{"type":"agentReady","coreVersion":"0.0.0","protocolVersion":"0.1"}""");
Console.Out.Flush();

// Ignore stdin entirely (no EOF-driven exit) and block forever.
Thread.Sleep(Timeout.Infinite);

GC.KeepAlive(lockFile);
