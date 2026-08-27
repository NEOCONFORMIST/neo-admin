#!/bin/bash
set -euo pipefail

# Valve's SDK bundle includes protoc in developer checkouts, but the public
# source package intentionally omits that binary. Use the runtime's compiler
# when the bundled one is unavailable so clean clones remain buildable.
bundled_protoc=sdk/devtools/bin/linux/protoc
bundled_sha=""
if [[ -f "$bundled_protoc" ]]; then
    bundled_sha="$(sha256sum "$bundled_protoc" | cut -d ' ' -f 1)"
fi
if [[ ! -x "$bundled_protoc" || "$bundled_sha" != "${HL2SDK_PROTOC_SHA256:-}" ]]; then
    mkdir -p "$(dirname "$bundled_protoc")"
    ln -sf /usr/local/bin/protoc "$bundled_protoc"
fi

rm -rf dockerbuild
mkdir dockerbuild
cd dockerbuild
pwd
python ../configure.py --enable-optimize --sdks cs2
ambuild
