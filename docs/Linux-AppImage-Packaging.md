# Linux AppImage Packaging

Use the existing self-contained Linux publish output as the AppImage input.

## 1. Publish the Linux build

```bash
dotnet publish ./AiyoPerps/AiyoPerps.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "./publish/linux-x64"
```

## 2. Prepare `appimagetool`

The build script looks for `appimagetool` in one of these locations:

1. `appimagetool` in `PATH`
2. `./tools/appimagetool-x86_64.AppImage`
3. the path set in `APPIMAGETOOL`

## 3. Build the AppImage

Run this on Linux:

```bash
chmod +x ./scripts/appimage/build-appimage.sh
./scripts/appimage/build-appimage.sh
```

Optional arguments:

```bash
./scripts/appimage/build-appimage.sh ./publish/linux-x64 ./artifacts/appimage
```

Optional version override:

```bash
AIYOPERPS_APPIMAGE_VERSION=2026.04.03 ./scripts/appimage/build-appimage.sh
```

## Output

The script creates:

- `./artifacts/appimage/AiyoPerps.AppDir`
- `./artifacts/appimage/AiyoPerps-<version>-x86_64.AppImage`

## Notes

- The current `dotnet publish` output already contains the Avalonia application payload as a self-contained single-file executable, so no extra Avalonia files need to be copied into `AppDir`.
- AppStream metadata is generated from `./packaging/linux/appimage/aiyoperps.appdata.xml` and copied to `usr/share/metainfo/app.aiyo.perps.appdata.xml` during packaging.
- System libraries reported by `ldd` are host dependencies, not files expected in your publish folder.
- On Linux, if the install location is read-only, AiyoPerps now falls back from `./db` to `~/.config/AiyoPerps`.
