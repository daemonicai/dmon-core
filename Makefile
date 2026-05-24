.PHONY: all build build-console build-core test clean

CONFIG      ?= Release
CONSOLE_OUT := build
CORE_OUT    := build/dmoncore

all: build test

build: build-console build-core

build-console:
	dotnet publish src/Dmon.Console/Dmon.Console.csproj \
		-c $(CONFIG) \
		-o $(CONSOLE_OUT) \
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
