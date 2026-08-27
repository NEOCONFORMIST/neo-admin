#!/bin/bash
set -euo pipefail

# Valve's SDK bundle includes protoc in developer checkouts, but the public
# source package intentionally omits that binary. Use the runtime's compiler
# when the bundled one is unavailable so clean clones remain buildable.
if [[ ! -x sdk/devtools/bin/linux/protoc ]]; then
    mkdir -p sdk/devtools/bin/linux
    ln -sf "$(command -v protoc)" sdk/devtools/bin/linux/protoc
fi

rm -rf dockerbuild
mkdir dockerbuild
cd dockerbuild
pwd
python ../configure.py --enable-optimize --sdks cs2
ambuild
