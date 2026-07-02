#!/usr/bin/env bash
# Build the P1 wasm core from the pinned crates.io release and place it where
# the library embeds it.
set -euo pipefail

CRATE_NAME="keetanetwork-anchor-client-wasi"
CRATE_VERSION="0.1.0"
CRATE_SHA256="6595286c4cd83bf6c2a79e715b932feb498c73fd9a78ae735d464aa915c9f189"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="${ROOT}/.wasm-build"
TARBALL="${BUILD_DIR}/${CRATE_NAME}-${CRATE_VERSION}.crate"
CRATE_DIR="${BUILD_DIR}/${CRATE_NAME}-${CRATE_VERSION}"
ARTIFACT="${CRATE_DIR}/target/wasm32-wasip1/release/keetanetwork_anchor_client_wasi.wasm"
DEST="${ROOT}/src/KeetaNet.Anchor/wasm/keetanetwork_anchor_client_wasi.wasm"
DOWNLOAD_URL="https://crates.io/api/v1/crates/${CRATE_NAME}/${CRATE_VERSION}/download"

checksum() {
	if command -v sha256sum >/dev/null 2>&1; then
		sha256sum "$1" | cut -d' ' -f1
	else
		shasum -a 256 "$1" | cut -d' ' -f1
	fi
}

require_wasip1_target() {
	if ! command -v cargo >/dev/null 2>&1; then
		echo "build-wasm: cargo not found; install Rust (https://rustup.rs)" >&2
		exit 1
	fi

	if ! rustup target list --installed 2>/dev/null | grep -q '^wasm32-wasip1$'; then
		echo "build-wasm: the wasm32-wasip1 target is not installed for the active toolchain" >&2
		echo "  fix: rustup target add wasm32-wasip1" >&2
		echo "  (or set RUSTUP_TOOLCHAIN to a toolchain that has it)" >&2
		exit 1
	fi
}

fetch_crate() {
	mkdir -p "${BUILD_DIR}"
	if [ ! -f "${TARBALL}" ]; then
		echo "build-wasm: downloading ${CRATE_NAME} ${CRATE_VERSION} from crates.io"
		# crates.io rejects requests without a User-Agent.
		curl --fail --silent --show-error --location \
			--user-agent "anchor-csharp build-wasm (https://github.com/KeetaNetwork/anchor-csharp)" \
			--output "${TARBALL}" "${DOWNLOAD_URL}"
	fi

	actual="$(checksum "${TARBALL}")"
	if [ "${actual}" != "${CRATE_SHA256}" ]; then
		rm -f "${TARBALL}"
		echo "build-wasm: checksum mismatch for ${TARBALL}" >&2
		echo "  expected: ${CRATE_SHA256}" >&2
		echo "  actual:   ${actual}" >&2
		exit 1
	fi

	if [ ! -d "${CRATE_DIR}" ]; then
		tar --extract --gzip --file "${TARBALL}" --directory "${BUILD_DIR}"
	fi
}

build_artifact() {
	echo "build-wasm: cargo build (wasm32-wasip1, features p1, release)"
	cargo build \
		--manifest-path "${CRATE_DIR}/Cargo.toml" \
		--release \
		--target wasm32-wasip1 \
		--no-default-features \
		--features p1
}

require_wasip1_target
fetch_crate
build_artifact

mkdir -p "$(dirname "${DEST}")"
cp "${ARTIFACT}" "${DEST}"
echo "build-wasm: wasm core at ${DEST}"
