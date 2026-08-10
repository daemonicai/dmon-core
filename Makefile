.PHONY: all build build-terminal build-core build-core-pack build-memory test test-live pack smoke schema clean daemon-app daemon-app-test dmon-home dmon-home-test dmon-home-app dmon-home-ios-check network release-wave

CONFIG            ?= Release
CORE_OUT          := build/dmoncore
TERMINAL_OUT      := build
PACK_OUT          ?= .pack-out
# Temp feed used only by build-core to resolve #:package dmoncore when publishing
# the default-core/Dmon.cs composition root. Separate from PACK_OUT so `make build`
# does not clobber a user-managed PACK_OUT.
BUILD_CORE_FEED   := build/core-feed

all: build test

build: build-core build-terminal build-memory

# Pack dmoncore and contract packages to a private temp feed, then publish
# default-core/Dmon.cs against that feed to produce the prebuilt default-core
# closure at build/dmoncore/dmoncore.dll (runnable via `dotnet exec`).
build-core: build-core-pack
	@printf '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key="core-feed" value="$(abspath $(BUILD_CORE_FEED))" />\n    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />\n  </packageSources>\n</configuration>\n' > build/core-feed.nuget.config
	NUGET_PACKAGES="$(abspath build/core-pkgs)" \
	dotnet publish default-core/Dmon.cs \
		-c $(CONFIG) \
		-o $(CORE_OUT) \
		--no-self-contained \
		--configfile "$(abspath build/core-feed.nuget.config)"

build-core-pack:
	bash scripts/pack-core.sh "$(BUILD_CORE_FEED)"

build-terminal:
	dotnet publish frontends/Dmon.Terminal/Dmon.Terminal.csproj \
		-c $(CONFIG) \
		-o $(TERMINAL_OUT) \
		--no-self-contained

build-memory:
	dotnet build memory/Dmon.Memory/Dmon.Memory.csproj -c $(CONFIG)

test: build-core
	dotnet test Everything.slnx -c $(CONFIG) --filter "Category!=Live"

test-live: build-core
	dotnet test Everything.slnx -c $(CONFIG) --filter "Category=Live"

# Pack dmoncore + the contract trio (and the sample extension) to a local
# NuGet feed so a Dmon.cs composition root resolves `#:package dmoncore@<protocol>.*`
# offline. The integration tests self-provision their own temp feeds, so this
# is a convenience for manual/CI use, not a prerequisite of `make test`.
pack:
	bash scripts/pack-core.sh "$(PACK_OUT)"

# Pack the SDK contract packages to a local feed and verify the out-of-tree
# sample (samples/Dmon.ExtensionSmoke) compiles against package references only.
smoke:
	bash scripts/smoke-sdk.sh

schema:
	dotnet run --project core/Dmon.Protocol.SchemaGen/Dmon.Protocol.SchemaGen.csproj \
		-c $(CONFIG) -- docs/protocol/schema.json

clean:
	rm -rf build/

daemon-app:
	swift build -c release --package-path daemon/Daemon.App

daemon-app-test:
	swift test --package-path daemon/Daemon.App

dmon-home:
	swift build -c release --package-path home

dmon-home-test:
	swift test --package-path home

# Requires xcodegen (brew install xcodegen). home/project.yml is the source
# of truth; the generated home/DmonHomeApp.xcodeproj is gitignored.
# -destination silences the ambiguous "My Mac" vs "Any Mac" destination warning;
# unrelated to ARCHS, which is set in project.yml and pins the built slice.
# The lipo check enforces the spec requirement that the built binary reports
# arm64 as its only architecture (ADR-037 Decision 5, design D15) — without it
# an ARCHS regression in project.yml would go undetected until a human ran
# lipo by hand.
dmon-home-app:
	xcodegen generate --spec home/project.yml --project home
	xcodebuild -project home/DmonHomeApp.xcodeproj -scheme DmonHomeApp -configuration Release \
		-derivedDataPath home/.build-xcode -destination 'platform=macOS,arch=arm64' -quiet build
	lipo -archs home/.build-xcode/Build/Products/Release/DmonHomeApp.app/Contents/MacOS/DmonHomeApp | grep -qx arm64 \
		|| { echo "dmon-home-app: expected arm64-only binary, got: $$(lipo -archs home/.build-xcode/Build/Products/Release/DmonHomeApp.app/Contents/MacOS/DmonHomeApp)"; exit 1; }

# Portability gate (design D16, dmon-home-foundations 6.1): builds only the
# GatewayClient target for a generic iOS destination, so a macOS-only
# dependency creeping into that module fails a build rather than a review.
# Separate derived-data path from dmon-home-app's home/.build-xcode, which
# two Product Owner verification recipes depend on and must not be disturbed.
dmon-home-ios-check:
	cd home && xcodebuild -scheme GatewayClient -destination 'generic/platform=iOS' \
		-derivedDataPath .build-ios -quiet build

network:
	dotnet pack frontends/Dmon.Network/Dmon.Network.csproj -c $(CONFIG) -o "$(PACK_OUT)"
	-dotnet tool uninstall --global Dmon.Network
	rm -rf "$(HOME)/.nuget/packages/dmon.network"
	dotnet tool install --global --add-source "$(abspath $(PACK_OUT))" Dmon.Network --prerelease

# Tag every NuGet-family package at <prefix>VERSION.0 for a protocol-cycle
# boundary release. Dry run by default; pass PUSH=1 to create and push tags.
# Usage: make release-wave VERSION=0.2
release-wave:
	bash scripts/release-wave.sh $(VERSION) $(if $(filter 1,$(PUSH)),--push,)
