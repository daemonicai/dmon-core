# Daemon.App (`dmonium`)

The `dmonium` macOS menu-bar app for the DAEMON personal-assistant surface.

This is a **Swift Package Manager** package, **not** an Xcode project — there is
no `.xcodeproj`/`.xcworkspace` checked in. It is built outside `Everything.slnx`
(which is .NET-only) per [ADR-028](../../docs/adrs/ADR-028-personal-assistant-monorepo-topology.md).

## Open in Xcode

Xcode opens SwiftPM packages natively (it resolves `Package.swift` into an
in-memory project with a runnable `DaemonApp` scheme):

```sh
xed daemon/Daemon.App
# or: open daemon/Daemon.App/Package.swift
# or: Xcode → File → Open… → select the Daemon.App folder
```

## Build / run from the command line

From the repo root, the wrapper target is:

```sh
make daemon-app
```

which runs:

```sh
swift build -c release --package-path daemon/Daemon.App
```

The executable lands at `daemon/Daemon.App/.build/release/DaemonApp`.

## Layout

- `Package.swift` — manifest; `DaemonApp` executable target, macOS 14+.
- `Sources/DaemonApp/` — app sources (menu-bar UI, gateway manager, keychain,
  Tailscale/Dcal health monitors, login-item management).
