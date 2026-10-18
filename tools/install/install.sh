#!/bin/sh
set -eu

root="${CELLA_HOME:-$HOME/.cella}"
base="https://github.com/Anixias/Cella/releases/latest/download"
asset="cella-launcher-linux-x64"

if [ "$(uname -s)" != "Linux" ] || [ "$(uname -m)" != "x86_64" ]; then
	echo "Cella needs 64-bit x86 Linux" >&2
	exit 1
fi

temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT

echo "Downloading the Cella launcher"
curl -fsSL -o "$temporary/$asset" "$base/$asset"
curl -fsSL -o "$temporary/SHA256SUMS" "$base/SHA256SUMS"
if ! (cd "$temporary" && grep "  $asset\$" SHA256SUMS | sha256sum -c - >/dev/null 2>&1); then
	echo "The launcher doesn't match its published checksum" >&2
	exit 1
fi

mkdir -p "$root"
install -m 755 "$temporary/$asset" "$root/cella"
"$root/cella" self install
"$root/cella" install
