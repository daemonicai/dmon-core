.PHONY: all build build-terminal build-core test clean

CONFIG       ?= Release
CORE_OUT     := build/dmoncore
TERMINAL_OUT := build

all: build test

build: build-terminal build-core

build-terminal:
	dotnet publish src/Dmon.Terminal/Dmon.Terminal.csproj \
		-c $(CONFIG) \
		-o $(TERMINAL_OUT) \
		--no-self-contained

build-core:
	dotnet publish src/Dmon.Core/Dmon.Core.csproj \
		-c $(CONFIG) \
		-o $(CORE_OUT) \
		--no-self-contained

test:
	dotnet test -c $(CONFIG)

clean:
	rm -rf build/
