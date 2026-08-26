#!/usr/bin/env python3
from __future__ import annotations

import argparse
import pathlib
import struct
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

from protocol import MESSAGE_SERVER_HEALTH, parse_packet  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--server-source",
        type=pathlib.Path,
        default=ROOT / "server_patch",
        help="Directory containing voicebridge_protocol.cpp and voicebridge_protocol.h",
    )
    parser.add_argument(
        "--skip-compile",
        action="store_true",
        help="Verify an existing tests/.build/fixture.bin without compiling it",
    )
    args = parser.parse_args()
    protocol_source = args.server_source.resolve()
    protocol_cpp = protocol_source / "voicebridge_protocol.cpp"
    protocol_header = protocol_source / "voicebridge_protocol.h"
    if not protocol_cpp.is_file() or not protocol_header.is_file():
        parser.error(f"protocol sources not found in {protocol_source}")

    build_dir = ROOT / "tests" / ".build"
    build_dir.mkdir(exist_ok=True)
    executable = build_dir / "protocol_fixture"
    fixture = build_dir / "fixture.bin"
    health_fixture = build_dir / "health_fixture.bin"

    if not args.skip_compile:
        subprocess.run(
            [
                "clang++",
                "-std=c++20",
                "-Wall",
                "-Wextra",
                "-Werror",
                f"-I{protocol_source}",
                str(protocol_cpp),
                str(ROOT / "tests" / "protocol_fixture.cpp"),
                "-o",
                str(executable),
            ],
            check=True,
        )
        subprocess.run(
            [str(executable), str(fixture), str(health_fixture)],
            check=True,
        )
    elif not fixture.is_file() or not health_fixture.is_file():
        parser.error(
            f"existing fixtures not found: {fixture}, {health_fixture}"
        )

    packet = parse_packet(fixture.read_bytes(), "0123456789abcdef0123456789abcdef")
    assert packet.audio_format == 2
    assert packet.sequence == 1234
    assert packet.tick == 5678
    assert packet.steam_id == 76561198012345678
    assert packet.player_name == "Fixture Player"
    assert packet.packet_offsets == [0, 3]
    assert packet.payload == bytes([1, 2, 3, 0x11, 0x12])

    tampered = bytearray(fixture.read_bytes())
    tampered[20] ^= 0x01
    try:
        parse_packet(bytes(tampered), "0123456789abcdef0123456789abcdef")
    except ValueError as error:
        assert "authentication" in str(error)
    else:
        raise AssertionError("Tampered packet was accepted")

    health = parse_packet(
        health_fixture.read_bytes(),
        "0123456789abcdef0123456789abcdef",
    )
    assert health.message_type == MESSAGE_SERVER_HEALTH
    assert health.sequence == 4321
    assert health.tick == 9876
    assert health.steam_id == 3661
    assert health.player_slot == 3
    assert health.sample_rate == 64
    assert struct.unpack("<f", struct.pack("<i", health.sequence_bytes))[0] == 64.0
    assert struct.unpack("<f", struct.pack("<I", health.section_number))[0] == 23.5
    assert (
        struct.unpack(
            "<f",
            struct.pack("<I", health.uncompressed_sample_offset),
        )[0]
        == 47.25
    )
    assert health.num_packets == 2
    assert health.player_name == "DEV-health-test"
    assert health.packet_offsets == []
    assert health.payload == b""

    print(
        "Protocol self-test passed: voice and server-health "
        "C++ serializers agree with the Python verifier."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
