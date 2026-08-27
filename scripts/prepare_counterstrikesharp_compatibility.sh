#!/usr/bin/env bash
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: ./prepare_counterstrikesharp_compatibility.sh --cs2-root PATH [--check-only]

PATH may be the CS2 installation root, its game directory, or game/csgo.

CounterStrikeSharp releases can request an executable process stack. Newer
Linux hosts reject that request when CS2 is launched outside Steam Runtime 3.
This tool backs up the CounterStrikeSharp loader, clears only its executable-
stack ELF flag, and verifies the result. Run it again after updating
CounterStrikeSharp. Stop CS2 before applying the repair.
EOF
}

cs2_root=""
check_only=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --cs2-root)
            [[ $# -ge 2 ]] || { usage >&2; exit 2; }
            cs2_root=$2
            shift 2
            ;;
        --check-only)
            check_only=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ -z "$cs2_root" ]]; then
    usage >&2
    exit 2
fi

if [[ -f "$cs2_root/game/csgo/gameinfo.gi" ]]; then
    install_root=$(CDPATH= cd -- "$cs2_root" && pwd)
    csgo_dir="$install_root/game/csgo"
elif [[ -f "$cs2_root/csgo/gameinfo.gi" ]]; then
    game_dir=$(CDPATH= cd -- "$cs2_root" && pwd)
    csgo_dir="$game_dir/csgo"
    install_root=$(dirname -- "$game_dir")
elif [[ -f "$cs2_root/gameinfo.gi" && "$(basename -- "$cs2_root")" == "csgo" ]]; then
    csgo_dir=$(CDPATH= cd -- "$cs2_root" && pwd)
    game_dir=$(dirname -- "$csgo_dir")
    install_root=$(dirname -- "$game_dir")
else
    echo "Could not find game/csgo/gameinfo.gi below: $cs2_root" >&2
    exit 3
fi

css_loader="$csgo_dir/addons/counterstrikesharp/bin/linuxsteamrt64/counterstrikesharp.so"
if [[ ! -f "$css_loader" ]]; then
    echo "CounterStrikeSharp is not installed at: $css_loader"
    exit 0
fi

if ! command -v patchelf >/dev/null 2>&1; then
    echo "patchelf 0.18 or newer is required to inspect CounterStrikeSharp." >&2
    echo "Alternatively, launch CS2 inside Steam Linux Runtime 3 (sniper)." >&2
    exit 4
fi

stack_state=$(patchelf --print-execstack "$css_loader" 2>/dev/null | awk '{print $NF}') || {
    echo "This patchelf build does not support --print-execstack." >&2
    echo "Install patchelf 0.18 or newer, or use Steam Linux Runtime 3." >&2
    exit 4
}

case "$stack_state" in
    X)
        ;;
    -)
        echo "CounterStrikeSharp compatibility check passed: GNU_STACK is non-executable."
        exit 0
        ;;
    *)
        echo "Unexpected CounterStrikeSharp executable-stack state: $stack_state" >&2
        exit 5
        ;;
esac

if [[ $check_only -eq 1 ]]; then
    echo "CounterStrikeSharp requests an executable stack and needs repair." >&2
    exit 10
fi

game_binary="$install_root/game/bin/linuxsteamrt64/cs2"
for process_exe in /proc/[0-9]*/exe; do
    if [[ "$(readlink -f -- "$process_exe" 2>/dev/null || true)" == "$game_binary" ]]; then
        echo "The CS2 server is running. Stop it before repairing CounterStrikeSharp." >&2
        exit 6
    fi
done

timestamp=$(date -u +%Y%m%dT%H%M%SZ)
backup_dir="$csgo_dir/addons/counterstrikesharp/neo-admin-compat-backups/$timestamp"
mkdir -p -- "$backup_dir"
cp -p -- "$css_loader" "$backup_dir/counterstrikesharp.so"

patchelf --clear-execstack "$css_loader"
if [[ "$(patchelf --print-execstack "$css_loader" | awk '{print $NF}')" != "-" ]]; then
    cp -p -- "$backup_dir/counterstrikesharp.so" "$css_loader"
    echo "CounterStrikeSharp verification failed; the original loader was restored." >&2
    exit 7
fi

echo "CounterStrikeSharp compatibility repair completed."
echo "Loader: $css_loader"
echo "Backup: $backup_dir/counterstrikesharp.so"
echo "Restart CS2 and run 'meta list'; both CounterStrikeSharp and NEO ADMIN should appear."
