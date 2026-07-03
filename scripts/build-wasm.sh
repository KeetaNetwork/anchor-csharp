#!/usr/bin/env bash
# Build the P1 wasm core from the pinned crates.io release and place it where
# the library embeds it.
set -euo pipefail

CRATE_NAME="keetanetwork-anchor-client-wasi"
CRATE_VERSION="0.1.1"
CRATE_SHA256="dd5c19da144a320d23d5b0b1af392ea6c94f6b7cb584e490bcf42155c768d9d6"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="${ROOT}/.wasm-build"
TARBALL="${BUILD_DIR}/${CRATE_NAME}-${CRATE_VERSION}.crate"
CRATE_DIR="${BUILD_DIR}/${CRATE_NAME}-${CRATE_VERSION}"
ARTIFACT="${CRATE_DIR}/target/wasm32-wasip1/release/keetanetwork_anchor_client_wasi.wasm"
DEST="${ROOT}/src/KeetaNet.Anchor/wasm/keetanetwork_anchor_client_wasi.wasm"
DOWNLOAD_URL="https://crates.io/api/v1/crates/${CRATE_NAME}/${CRATE_VERSION}/download"

checksum() {
	local file="$1"
	if command -v sha256sum >/dev/null 2>&1; then
		sha256sum "${file}" | cut -d' ' -f1
	else
		shasum -a 256 "${file}" | cut -d' ' -f1
	fi
}

# rasn-compiler probes `$CARGO_HOME/bin/rustfmt` and `$CARGO`-adjacent
# `rustfmt` without the .exe suffix. When both miss it silently emits
# unformatted bindings that break the keetanetwork-asn1 build. Shim
# extension-less copies at both probe paths and fail loudly when they
# cannot be provisioned or executed.
shim_rustfmt_for_windows() {
	[[ "${OS:-}" == "Windows_NT" ]] || return 0

	echo "build-wasm: windows rustfmt shim (CARGO_HOME=${CARGO_HOME:-<unset>})"

	# `$CARGO` inside build scripts points at the toolchain's cargo, so the
	# adjacent-probe directory is the toolchain bin, not the rustup proxies.
	local toolchain_bin
	toolchain_bin="$(dirname "$(cygpath -u "$(rustup which cargo)")")"
	echo "build-wasm: toolchain bin is ${toolchain_bin}"

	if [[ ! -f "${toolchain_bin}/rustfmt.exe" ]]; then
		echo "build-wasm: rustfmt missing from the toolchain; installing the component"
		rustup component add rustfmt
	fi

	copy_bare_rustfmt "${toolchain_bin}"
	"${toolchain_bin}/rustfmt" --version

	# Cover the `$CARGO_HOME/bin` probe too, via the rustup proxy when one
	# exists there (proxies dispatch on their basename).
	local cargo_bin
	cargo_bin="$(cygpath -u "${CARGO_HOME:-${USERPROFILE}/.cargo}")/bin"
	if [[ -f "${cargo_bin}/rustfmt.exe" ]]; then
		copy_bare_rustfmt "${cargo_bin}"
	fi

	echo "build-wasm: bare rustfmt shims verified"
}

# MSYS transparently resolves an extension-less `rustfmt` path to
# `rustfmt.exe`, so POSIX tools cannot create the bare copy (cp reports
# "same file") and `-f` checks lie. Copy with PowerShell, which uses the
# literal name, and verify via directory entries.
copy_bare_rustfmt() {
	local bin_dir="$1"
	local src dest
	src="$(cygpath -w "${bin_dir}/rustfmt.exe")"
	dest="$(cygpath -w "${bin_dir}/rustfmt")"
	powershell -NoProfile -Command "Copy-Item -LiteralPath '${src}' -Destination '${dest}' -Force"

	if [[ -z "$(find "${bin_dir}" -maxdepth 1 -name rustfmt)" ]]; then
		echo "build-wasm: failed to shim bare rustfmt in ${bin_dir}" >&2
		exit 1
	fi
	echo "build-wasm: bare rustfmt shimmed in ${bin_dir}"
}

# A cached target dir can hold codegen output produced while rustfmt was
# unreachable (actions/cache saves even from failed jobs). Purge the
# keetanetwork-asn1 build-script outputs so its codegen always reruns.
purge_stale_asn1_outputs() {
	local build_root="${CRATE_DIR}/target/wasm32-wasip1/release"
	if compgen -G "${build_root}/build/keetanetwork-asn1-*" >/dev/null; then
		echo "build-wasm: purging cached keetanetwork-asn1 codegen so it reruns"
		rm -rf "${build_root}"/build/keetanetwork-asn1-* "${build_root}"/.fingerprint/keetanetwork-asn1-*
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
	if [[ ! -f "${TARBALL}" ]]; then
		echo "build-wasm: downloading ${CRATE_NAME} ${CRATE_VERSION} from crates.io"
		# crates.io rejects requests without a User-Agent.
		curl --fail --silent --show-error --location \
			--proto '=https' --tlsv1.2 \
			--user-agent "anchor-csharp build-wasm (https://github.com/KeetaNetwork/anchor-csharp)" \
			--output "${TARBALL}" "${DOWNLOAD_URL}"
	fi

	actual="$(checksum "${TARBALL}")"
	if [[ "${actual}" != "${CRATE_SHA256}" ]]; then
		rm -f "${TARBALL}"
		echo "build-wasm: checksum mismatch for ${TARBALL}" >&2
		echo "  expected: ${CRATE_SHA256}" >&2
		echo "  actual:   ${actual}" >&2
		exit 1
	fi

	if [[ ! -d "${CRATE_DIR}" ]]; then
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

shim_rustfmt_for_windows
require_wasip1_target
fetch_crate
purge_stale_asn1_outputs
build_artifact

mkdir -p "$(dirname "${DEST}")"
cp "${ARTIFACT}" "${DEST}"
echo "build-wasm: wasm core at ${DEST}"
