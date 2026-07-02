.PHONY: help developer restore build rebuild format lint test coverage pack clean wasm release

# Build configuration (Debug or Release)
CONFIG ?= Release

# Solution under build
SLN := KeetaNet.Anchor.slnx

# Output directory for NuGet packages
ARTIFACTS := artifacts

# Embedded P1 wasm core, built from the pinned crates.io release
WASM_DEST := src/KeetaNet.Anchor/wasm/keetanetwork_anchor_client_wasi.wasm

default: help

# Restore NuGet dependencies
restore:
	dotnet restore $(SLN)

# Build the solution (builds the wasm core first if missing)
build: restore $(WASM_DEST)
	dotnet build $(SLN) -c $(CONFIG) --no-restore

# Clean then build
rebuild: clean build

# Apply formatting fixes
format:
	dotnet format $(SLN)

# Verify formatting and spelling without writing changes
lint:
	dotnet format $(SLN) --verify-no-changes
	npx --yes cspell --config cspell.yaml --no-progress "src/**/*.cs" "tests/**/*.cs" "scripts/**" "Makefile" "*.md"

# Run tests (xUnit v3 on Microsoft.Testing.Platform)
test: build
	dotnet test $(SLN) -c $(CONFIG) --no-build

# Run tests with code coverage (cobertura, for SonarCloud conversion)
coverage: build
	dotnet test $(SLN) -c $(CONFIG) --no-build \
		-- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml

# Produce the NuGet package (.nupkg + .snupkg)
pack: build
	dotnet pack src/KeetaNet.Anchor/KeetaNet.Anchor.csproj -c Release --no-build -o $(ARTIFACTS)

# Build the P1 wasm core from the pinned crates.io release
wasm:
	./scripts/build-wasm.sh

$(WASM_DEST):
	./scripts/build-wasm.sh

# Remove build outputs
clean:
	dotnet clean $(SLN) -c $(CONFIG) || true
	rm -rf $(ARTIFACTS)
	find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

# Developer bootstrap: verify SDK, restore, build, test
developer:
	@dotnet --version >/dev/null 2>&1 || { echo "install the .NET SDK (see global.json)"; exit 1; }
	@echo "Using .NET SDK $$(dotnet --version)"
	$(MAKE) build
	$(MAKE) test
	$(MAKE) help

# Publish a signed release tag (delegates to scripts/release.sh)
release:
	./scripts/release.sh $(filter-out $@,$(MAKECMDGOALS))

# Allow flags to be passed as fake targets
--%:
	@:

help:
	@echo "anchor-csharp"
	@echo "=================================="
	@echo "Developer commands:"
	@echo "  make developer   - Verify SDK, restore, build, run tests"
	@echo "  make restore     - Restore NuGet dependencies"
	@echo "  make build       - Build the solution (CONFIG=$(CONFIG))"
	@echo "  make rebuild     - Clean then build"
	@echo "  make test        - Run tests"
	@echo "  make coverage    - Run tests with code coverage"
	@echo "  make format      - Apply formatting fixes"
	@echo "  make lint        - Verify formatting + spelling"
	@echo "  make pack        - Produce the NuGet package into $(ARTIFACTS)/"
	@echo "  make wasm        - Build the P1 wasm core from the pinned crates.io release"
	@echo "  make clean       - Remove build outputs"
	@echo ""
	@echo "Release commands:"
	@echo "  make release vX.Y.Z - Create a signed releases/vX.Y.Z tag"
