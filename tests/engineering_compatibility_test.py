#!/usr/bin/env python3
"""Static compatibility gates for NEO ADMIN's transitional server core."""

from __future__ import annotations

import argparse
from pathlib import Path


def require_text(path: Path, *needles: str) -> str:
    text = path.read_text(encoding="utf-8")
    for needle in needles:
        if needle not in text:
            raise AssertionError(f"{path}: required text is missing: {needle}")
    return text


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--server-source", required=True, type=Path)
    args = parser.parse_args()
    root = args.server_source.resolve()
    src = root / "src"

    builder = require_text(
        root / "AMBuilder",
        "neo_admin_sources",
        "compatibility_sources",
        "neo_admin_transport.cpp",
        "neo_admin_compatibility.cpp",
    )
    if builder.index("neo_admin_sources") > builder.index("compatibility_sources"):
        raise AssertionError("AMBuilder must describe the NEO ADMIN module before compatibility sources")

    require_text(
        root / "AMBuildScript",
        "cxx.cflags += ['-pthread']",
        "cxx.linkflags += ['-static-libstdc++', '-pthread', '-Wl,-z,noexecstack']",
    )
    require_text(
        root / "Dockerfile",
        "ARG HL2SDK_PROTOC_COMMIT=",
        "ENV HL2SDK_PROTOC_SHA256=",
        "raw.githubusercontent.com/alliedmodders/hl2sdk/",
    )
    require_text(
        src / "addresses.cpp",
        "Optional game-ban cleanup signature is unavailable",
        "return true;",
    )
    require_text(
        src / "detours.cpp",
        "NeoAdminCompatibility_CanCleanGameBans()",
        "addresses::sm_mapGcBanInformation",
    )
    require_text(
        src / "neo_admin_transport.cpp",
        "kMaximumPendingPackets",
        "void Run()",
        "g_coalesced",
    )
    require_text(
        src / "voicebridge_protocol.h",
        "kMessageServerCapabilities",
        "kCapabilityPlayerStateDelta",
        "kCapabilityAsyncOutbound",
        "RequestCapabilities = 52",
    )
    require_text(
        src / "neo_ptt.h",
        "NeoPtt_SetBuildId(std::string_view build_id)",
        "std::uint64_t outbound_coalesced = 0",
    )

    metadata = require_text(
        root / "plugin-metadata.json",
        '"display_name": "NEO ADMIN"',
        "transitional CS2Fixes compatibility core",
    )
    if '"author": "NEOCONFORMIST and the CS2Fixes contributors"' not in metadata:
        raise AssertionError("plugin metadata must retain upstream attribution")

    print("Engineering compatibility checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
