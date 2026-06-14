.PHONY: all build build-terminal build-core build-extensions build-memory test pack smoke schema clean

CONFIG            ?= Release
CORE_OUT          := build/dmoncore
TERMINAL_OUT      := build
EXTENSIONS_OUT    := build/extensions
PACK_OUT          ?= .pack-out

all: build test

build: build-core build-terminal build-extensions build-memory

build-core:
	dotnet publish src/Dmon.Core/Dmon.Core.csproj \
		-c $(CONFIG) \
		-o $(CORE_OUT) \
		--no-self-contained

build-terminal:
	dotnet publish src/Dmon.Terminal/Dmon.Terminal.csproj \
		-c $(CONFIG) \
		-o $(TERMINAL_OUT) \
		--no-self-contained

build-extensions:
	@for csproj in extensions/*/*.csproj; do \
		name=$$(basename $$(dirname $$csproj)); \
		echo "Building extension: $$name"; \
		dotnet publish "$$csproj" \
			-c $(CONFIG) \
			-o "$(EXTENSIONS_OUT)/$$name" \
			--no-self-contained; \
	done

build-memory:
	dotnet build src/Dmon.Memory/Dmon.Memory.csproj -c $(CONFIG)

test:
	dotnet test -c $(CONFIG)

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
	dotnet run --project src/Dmon.Protocol.SchemaGen/Dmon.Protocol.SchemaGen.csproj \
		-c $(CONFIG) -- docs/protocol/schema.json

clean:
	rm -rf build/
