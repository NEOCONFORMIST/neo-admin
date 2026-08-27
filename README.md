# Building NEO ADMIN from the release source

## Server plugin

Install Docker Desktop or Docker Engine, enter the `server` directory, and run:

```bash
docker compose up --build --abort-on-container-exit --exit-code-from cs2fixes-build
```

The CS2 payload is written below `server/dockerbuild/package/cs2`.
The Docker build obtains its own SDK and Metamod build dependencies. They are
not included in the NEO ADMIN drag-and-drop binary release; server owners must
install Metamod:Source separately.

## Windows application

Install the .NET 8 SDK and run from the source archive root:

```powershell
dotnet publish windows/NEO.Admin/NEO.Admin.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o dist/windows-x64
```

Create `appsettings.json` from `windows/NEO.Admin/appsettings.example.json`.
Do not place real access profiles, shared secrets, or server addresses in a
redistributable build.

## Android application

Install the .NET 9 SDK with the Android workload, a compatible Android SDK, and
a JDK. Then run from the source archive root:

```powershell
.\scripts\build_android.ps1
```

The signed APK is written to `dist/android/NEO-ADMIN-Android.apk`. On the first
build, the script creates a private signing key under
`%LOCALAPPDATA%/NEO ADMIN/android-signing`. Keep that key private and backed up;
Android requires future updates to use the same signing certificate.
