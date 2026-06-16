.PHONY: all build build-terminal build-core build-core-pack build-memory test pack smoke schema clean

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
	dotnet build middleware/Dmon.Memory/Dmon.Memory.csproj -c $(CONFIG)

test: build-core
	dotnet test Everything.slnx -c $(CONFIG)

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
