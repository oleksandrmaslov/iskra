#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 <version> [osx-arm64|osx-x64]" >&2
  exit 2
}

[[ $# -ge 1 && $# -le 2 ]] || usage
version=$1
rid=${2:-}

[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] || usage
[[ $(uname -s) == Darwin ]] || { echo "This package builder must run on macOS." >&2; exit 1; }

machine=$(uname -m)
case "$machine" in
  arm64) native_rid=osx-arm64 ;;
  x86_64) native_rid=osx-x64 ;;
  *) echo "Unsupported macOS architecture: $machine" >&2; exit 1 ;;
esac
rid=${rid:-$native_rid}
[[ $rid == "$native_rid" ]] || {
  echo "Refusing to label a $native_rid host build as $rid; use the matching native runner." >&2
  exit 1
}

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"

for command_name in dotnet pwsh sips iconutil hdiutil codesign ditto shasum; do
  command -v "$command_name" >/dev/null || { echo "$command_name not found" >&2; exit 1; }
done

build_args=(-Version "$version" -Runtimes "$rid" -NativePackage)
[[ ${ISKRA_ALLOW_DIRTY:-0} == 1 ]] && build_args+=(-AllowDirty)
[[ ${ISKRA_REQUIRE_TAG:-0} == 1 ]] && build_args+=(-RequireTag)
pwsh -NoProfile -File installer/build-unix-bundles.ps1 "${build_args[@]}"

bundle_name="Iskra-$version-$rid"
bundle_dir="$repo_root/artifacts/$bundle_name"
app="$bundle_dir/Iskra.app"
resources="$app/Contents/Resources"
info_plist="$app/Contents/Info.plist"
iconset="$repo_root/artifacts/.iskra-$version-$rid.iconset"
dmg_stage="$repo_root/artifacts/.dmg-$version-$rid"
zip_path="$repo_root/artifacts/$bundle_name.zip"
dmg_path="$repo_root/artifacts/$bundle_name.dmg"
tar_path="$repo_root/artifacts/$bundle_name.tar.gz"

chmod 0755 "$app/Contents/MacOS/Iskra" "$bundle_dir/Iskra.Cli"
"$bundle_dir/Iskra.Cli" --help >/dev/null

case "$iconset" in "$repo_root"/artifacts/.iskra-*.iconset) rm -rf -- "$iconset" ;; *) exit 1 ;; esac
case "$dmg_stage" in "$repo_root"/artifacts/.dmg-*) rm -rf -- "$dmg_stage" ;; *) exit 1 ;; esac
mkdir -p "$iconset" "$dmg_stage"

source_icon="$repo_root/docs/iskra.png"
sips -z 16 16 "$source_icon" --out "$iconset/icon_16x16.png" >/dev/null
sips -z 32 32 "$source_icon" --out "$iconset/icon_16x16@2x.png" >/dev/null
sips -z 32 32 "$source_icon" --out "$iconset/icon_32x32.png" >/dev/null
sips -z 64 64 "$source_icon" --out "$iconset/icon_32x32@2x.png" >/dev/null
sips -z 128 128 "$source_icon" --out "$iconset/icon_128x128.png" >/dev/null
sips -z 256 256 "$source_icon" --out "$iconset/icon_128x128@2x.png" >/dev/null
sips -z 256 256 "$source_icon" --out "$iconset/icon_256x256.png" >/dev/null
sips -z 512 512 "$source_icon" --out "$iconset/icon_256x256@2x.png" >/dev/null
sips -z 512 512 "$source_icon" --out "$iconset/icon_512x512.png" >/dev/null
sips -z 1024 1024 "$source_icon" --out "$iconset/icon_512x512@2x.png" >/dev/null
iconutil -c icns "$iconset" -o "$resources/iskra.icns"
/usr/libexec/PlistBuddy -c "Set :CFBundleIconFile iskra.icns" "$info_plist"

signing_identity=${ISKRA_MACOS_SIGNING_IDENTITY:-}
notary_profile=${ISKRA_MACOS_NOTARY_PROFILE:-}
if [[ ${ISKRA_REQUIRE_TAG:-0} == 1 && ( -z $signing_identity || -z $notary_profile ) ]]; then
  echo "Tagged macOS builds require Developer ID signing and notarization; engineering overrides are ignored." >&2
  exit 1
fi
if [[ -n $signing_identity ]]; then
  entitlements="$repo_root/installer/macos-hardened-runtime-entitlements.plist"
  [[ -f $entitlements ]] || { echo "Missing hardened-runtime entitlements: $entitlements" >&2; exit 1; }
  codesign --force --options runtime --timestamp --entitlements "$entitlements" --sign "$signing_identity" "$bundle_dir/Iskra.Cli"
  codesign --force --options runtime --timestamp --entitlements "$entitlements" --sign "$signing_identity" "$app/Contents/MacOS/Iskra"
  codesign --force --options runtime --timestamp --entitlements "$entitlements" --sign "$signing_identity" "$app"
  codesign --verify --strict --verbose=2 "$bundle_dir/Iskra.Cli"
  codesign --verify --deep --strict --verbose=2 "$app"
  "$bundle_dir/Iskra.Cli" --help >/dev/null
  plutil -replace signed -bool true "$bundle_dir/BUILD-METADATA.json"

  if [[ -n $notary_profile ]]; then
    command -v xcrun >/dev/null || { echo "xcrun not found" >&2; exit 1; }
    notary_zip="$repo_root/artifacts/.notary-$version-$rid.zip"
    rm -f -- "$notary_zip"
    ditto -c -k --sequesterRsrc --keepParent "$app" "$notary_zip"
    xcrun notarytool submit "$notary_zip" --keychain-profile "$notary_profile" --wait
    xcrun stapler staple "$app"
    rm -f -- "$notary_zip"
  elif [[ ${ISKRA_ALLOW_UNNOTARIZED:-0} != 1 ]]; then
    echo "Signing identity supplied without ISKRA_MACOS_NOTARY_PROFILE; refusing release output." >&2
    echo "Set ISKRA_ALLOW_UNNOTARIZED=1 only for engineering packages." >&2
    exit 1
  else
    echo "Producing a signed but unnotarized engineering package." >&2
  fi
else
  if [[ ${ISKRA_ALLOW_UNSIGNED:-0} != 1 ]]; then
    echo "No ISKRA_MACOS_SIGNING_IDENTITY supplied; refusing release output." >&2
    echo "Set ISKRA_ALLOW_UNSIGNED=1 only for engineering packages." >&2
    exit 1
  fi
  echo "Producing an unsigned engineering package because ISKRA_ALLOW_UNSIGNED=1." >&2
fi

rm -f -- "$bundle_dir/SHA256SUMS.txt"
(
  cd "$bundle_dir"
  # Every filename in this generated bundle is controlled by the builder, so
  # newline-delimited BSD sort is portable and deterministic on stock macOS.
  find . -type f ! -name SHA256SUMS.txt -print | LC_ALL=C sort |
    while IFS= read -r file; do
      hash=$(shasum -a 256 "$file" | awk '{print $1}')
      printf '%s  %s\n' "$hash" "${file#./}"
    done > SHA256SUMS.txt
)

commit_epoch=$(git show -s --format=%ct HEAD)
rm -f -- "$tar_path"
pwsh -NoProfile -File installer/New-PortableTarGz.ps1 \
  -SourceDirectory "$bundle_dir" \
  -DestinationPath "$tar_path" \
  -RootName "$bundle_name" \
  -ExecutablePathList "Iskra.app/Contents/MacOS/Iskra;Iskra.Cli" \
  -Timestamp "$(date -u -r "$commit_epoch" '+%Y-%m-%dT%H:%M:%SZ')" >/dev/null

cp -R "$app" "$dmg_stage/Iskra.app"
cp "$bundle_dir/Iskra.Cli" "$bundle_dir/README.txt" "$bundle_dir/SHA256SUMS.txt" "$dmg_stage/"
ln -s /Applications "$dmg_stage/Applications"

rm -f -- "$zip_path" "$dmg_path"
ditto -c -k --sequesterRsrc --keepParent "$bundle_dir" "$zip_path"
hdiutil create -volname "Iskra $version" -srcfolder "$dmg_stage" -ov -format UDZO "$dmg_path" >/dev/null

if [[ -n $signing_identity && -n $notary_profile ]]; then
  codesign --force --timestamp --sign "$signing_identity" "$dmg_path"
  xcrun notarytool submit "$dmg_path" --keychain-profile "$notary_profile" --wait
  xcrun stapler staple "$dmg_path"
fi

checksum_path="$repo_root/artifacts/Iskra-$version-$rid-native-SHA256SUMS.txt"
(
  cd "$repo_root/artifacts"
  shasum -a 256 "$(basename "$tar_path")" "$(basename "$zip_path")" "$(basename "$dmg_path")" > "$(basename "$checksum_path")"
)

rm -rf -- "$iconset" "$dmg_stage"
printf 'Built:\n  %s\n  %s\n  %s\n  %s\n' "$tar_path" "$zip_path" "$dmg_path" "$checksum_path"
