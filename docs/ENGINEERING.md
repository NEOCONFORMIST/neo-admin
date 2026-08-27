# NEO ADMIN Server Engineering

## Source boundaries

`AMBuilder` separates the server source into two explicit groups:

- `neo_admin_sources` owns the remote administration protocol, authenticated
  sessions, accounts, permissions, audit records, discipline data, SQLite
  persistence, map overviews, and outbound transport.
- `compatibility_sources` contains the inherited CS2Fixes engine integration
  that is still needed to load safely on current CS2 builds.

The released plugin has its own Metamod loader (`neo_admin.vdf`) and binary
identity (`neo_admin.so`). The `addons/cs2fixes` support directory remains in the
package temporarily because the compatibility core still reads game data and
configuration from that location. It contains no second plugin binary.

## Threading rule

CS2 engine objects may only be accessed on the game thread. The outbound network
worker receives deep-copied plain data, signs it, and sends UDP packets without
holding or dereferencing engine pointers. Its queue is bounded; stale position
updates are coalesced and are the first packets discarded under backpressure.

## Wire compatibility

The protocol retains major version 1. New clients request a capability response
after authentication. Older clients do not request it and continue to receive
only message types they understand. Features that change wire behavior must add
a capability bit and remain optional until the client confirms support.

## Engine compatibility

Core engine addresses are required. Optional signatures, such as the inherited
game-ban cleanup hook, fail closed and disable only that feature. A missing
optional signature must never leave a null detour target or prevent the remote
administration service from starting.

## Validation

`scripts/build_test_deploy_server.ps1 -NoDeploy` is the release gate. It builds
inside Steam Runtime 3, runs protocol, persistence, gameplay, compatibility, and
packaging checks, and verifies a non-executable GNU stack. Deployment is allowed
only after the complete gate succeeds.
