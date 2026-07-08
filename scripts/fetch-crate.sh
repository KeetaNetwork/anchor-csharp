# Shared pinned-crate fetch for the generation scripts. Source this file and
# call `fetch_crate`.

checksum() {
	local file="$1"
	if command -v sha256sum >/dev/null 2>&1; then
		sha256sum "${file}" | cut -d' ' -f1
	else
		shasum -a 256 "${file}" | cut -d' ' -f1
	fi
}

# fetch_crate <name> <version> <sha256> <build_dir>
#
# Download the pinned crates.io release into `<build_dir>`, verify its SHA-256,
# and extract it to `<build_dir>/<name>-<version>` (skipping work already done).
fetch_crate() {
	local name="$1" version="$2" expected="$3" build_dir="$4"
	local tarball="${build_dir}/${name}-${version}.crate"
	local crate_dir="${build_dir}/${name}-${version}"
	local url="https://crates.io/api/v1/crates/${name}/${version}/download"

	mkdir -p "${build_dir}"
	if [[ ! -f "${tarball}" ]]; then
		echo "fetch-crate: downloading ${name} ${version} from crates.io"
		# crates.io rejects requests without a User-Agent.
		curl --fail --silent --show-error --location \
			--proto '=https' --tlsv1.2 \
			--user-agent "anchor-csharp fetch-crate (https://github.com/KeetaNetwork/anchor-csharp)" \
			--output "${tarball}" "${url}"
	fi

	local actual
	actual="$(checksum "${tarball}")"
	if [[ "${actual}" != "${expected}" ]]; then
		rm -f "${tarball}"
		echo "fetch-crate: checksum mismatch for ${tarball}" >&2
		echo "  expected: ${expected}" >&2
		echo "  actual:   ${actual}" >&2
		exit 1
	fi

	if [[ ! -d "${crate_dir}" ]]; then
		tar --extract --gzip --file "${tarball}" --directory "${build_dir}"
	fi
}
