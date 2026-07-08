#!/usr/bin/env bash
# Generate the node REST transport (models + typed operations) from the
# canonical OpenAPI spec shipped in the pinned keetanetwork-client crate.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# shellcheck source=scripts/pins.env
source "${ROOT}/scripts/pins.env"
# shellcheck source=scripts/fetch-crate.sh
source "${ROOT}/scripts/fetch-crate.sh"

BUILD_DIR="${ROOT}/.node-api-build"
CRATE_DIR="${BUILD_DIR}/${NODE_CLIENT_CRATE}-${NODE_CLIENT_VERSION}"
SPEC="${CRATE_DIR}/openapi/keetanet-node.yaml"
DEST="${ROOT}/src/KeetaNet.Anchor/Generated/Node/NodeApi.cs"

generate_client() {
	echo "generate-node-api: nswag openapi2csclient from ${SPEC}"
	mkdir -p "$(dirname "${DEST}")"
	(cd "${ROOT}" && dotnet tool restore >/dev/null && dotnet nswag openapi2csclient \
		"/input:${SPEC}" \
		/classname:NodeApi \
		/namespace:KeetaNet.Anchor.Generated.Node \
		"/output:${DEST}" \
		/jsonLibrary:SystemTextJson \
		/injectHttpClient:true \
		/disposeHttpClient:false \
		/generateClientInterfaces:false \
		/generateOptionalParameters:true \
		/generateNativeRecords:true \
		/useBaseUrl:true \
		/generateBaseUrlProperty:true \
		/exceptionClass:NodeApiException)
}

fetch_crate "${NODE_CLIENT_CRATE}" "${NODE_CLIENT_VERSION}" "${NODE_CLIENT_SHA256}" "${BUILD_DIR}"
generate_client
echo "generate-node-api: node transport at ${DEST}"
