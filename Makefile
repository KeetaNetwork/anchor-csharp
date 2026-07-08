.PHONY: help developer restore build rebuild do-lint do-lint-ci test pack clean wasm node-api node-harness release

# Build configuration (Debug or Release)
CONFIG ?= Release

# Solution under build
SLN := KeetaNet.Anchor.slnx

# TypeScript interop harness (reference anchor + in-memory test node)
HARNESS_DIR := tests/node-harness
HARNESS_SOURCES := $(wildcard $(HARNESS_DIR)/src/*.ts)

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

# Lint code (applies formatting fixes; C# + the TypeScript harness, one command)
do-lint: node-harness
	dotnet format $(SLN)
	npx --yes cspell --config cspell.yaml --no-progress "src/**/*.cs" "tests/**/*.cs" "tests/node-harness/src/**" "scripts/**" "Makefile" "*.md"
	cd $(HARNESS_DIR) && npm run lint

# Lint code for CI (check only, no fixes)
do-lint-ci: node-harness
	dotnet format $(SLN) --verify-no-changes
	npx --yes cspell --config cspell.yaml --no-progress "src/**/*.cs" "tests/**/*.cs" "tests/node-harness/src/**" "scripts/**" "Makefile" "*.md"
	cd $(HARNESS_DIR) && npm run lint

# Run all tests (unit + e2e against the live TypeScript reference anchor)
# with code coverage (cobertura, for SonarCloud conversion)
test: build node-harness
	dotnet test $(SLN) -c $(CONFIG) --no-build \
		-- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml

# Build the TypeScript harnesses (installs deps + compiles every entry)
$(HARNESS_DIR)/node_modules/.package-lock.json: $(HARNESS_DIR)/package-lock.json
	cd $(HARNESS_DIR) && npm ci --ignore-scripts && npm rebuild sqlite3

$(HARNESS_DIR)/dist/.built: $(HARNESS_DIR)/node_modules/.package-lock.json $(HARNESS_SOURCES)
	cd $(HARNESS_DIR) && npm run build
	touch $@

node-harness: $(HARNESS_DIR)/dist/.built

# Produce the NuGet packages (.nupkg + .snupkg)
pack: build
	dotnet pack $(SLN) -c Release --no-build -o $(ARTIFACTS)

# Build the P1 wasm core from the pinned crates.io release
wasm:
	./scripts/build-wasm.sh

# Regenerate the node REST transport from the pinned OpenAPI spec (committed)
node-api:
	./scripts/generate-node-api.sh

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
	@echo "  make developer    - Verify SDK, restore, build, run tests"
	@echo "  make restore      - Restore NuGet dependencies"
	@echo "  make build        - Build the solution (CONFIG=$(CONFIG))"
	@echo "  make rebuild      - Clean then build"
	@echo "  make test         - Run all tests with coverage (builds node-harness; unit + e2e)"
	@echo "  make node-harness - Install + build the TypeScript interop harnesses"
	@echo "  make do-lint      - Lint code with formatting fixes (C# + harness + spelling)"
	@echo "  make pack         - Produce the NuGet packages into $(ARTIFACTS)/"
	@echo "  make wasm         - Build the P1 wasm core from the pinned crates.io release"
	@echo "  make node-api     - Regenerate the node REST transport from the pinned OpenAPI spec"
	@echo "  make clean        - Remove build outputs"
	@echo ""
	@echo "CI Commands:"
	@echo "  make do-lint-ci   - Lint code for CI (check only, no fixes)"
	@echo ""
	@echo "Release commands:"
	@echo "  make release vX.Y.Z - Create a signed releases/vX.Y.Z tag"
