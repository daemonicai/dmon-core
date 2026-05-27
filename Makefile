.PHONY: all build build-terminal build-core build-extensions test clean

CONFIG            ?= Release
CORE_OUT          := build/dmoncore
TERMINAL_OUT      := build
EXTENSIONS_OUT    := build/extensions

all: build test

build: build-core build-terminal build-extensions

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

test:
	dotnet test -c $(CONFIG)

clean:
	rm -rf build/
