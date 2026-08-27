# NEO ADMIN for Android

This native .NET for Android client uses the same authenticated UDP protocol and
separate Windows-admin permission model as the NEO ADMIN desktop application.

## Current mobile controls

- Initial Owner claim or existing administrator ID/access key.
- Live server connection and permission-aware session state.
- Live players, SourceTV, SteamID64, team, health, and voice activity.
- Kick, slay, respawn, team moves, and weapon/item grants.
- Server chat, map selector, match controls, and bot controls.
- Server health and authenticated server-console commands.

The Android app stores its connection profile in private app storage and opts
out of Android cloud backup. Uninstalling the app removes that profile.

## Build

Run from the repository root:

```powershell
.\scripts\build_android.ps1
```

The sideloadable APK is copied to `dist/android/NEO-ADMIN-Android.apk`. The
first Release build creates a private signing key under
`%LOCALAPPDATA%/NEO ADMIN/android-signing`. Keep that directory backed up;
future APK updates must use the same key.
