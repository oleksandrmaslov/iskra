#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 <version> [linux-x64|linux-arm64]" >&2
  exit 2
}

[[ $# -ge 1 && $# -le 2 ]] || usage
version=$1
rid=${2:-}

[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] || usage

# Debian sorts a hyphenated revision after the same upstream version with no
# revision. Map SemVer prereleases to '~' so 2.2.0~rc.1 correctly sorts before
# 2.2.0 while the app and artifact names retain their SemVer spelling.
deb_version=${version/-/\~}
[[ $(uname -s) == Linux ]] || { echo "This package builder must run on Linux." >&2; exit 1; }

machine=$(uname -m)
case "$machine" in
  x86_64) native_rid=linux-x64; deb_arch=amd64 ;;
  aarch64|arm64) native_rid=linux-arm64; deb_arch=arm64 ;;
  *) echo "Unsupported Linux architecture: $machine" >&2; exit 1 ;;
esac
rid=${rid:-$native_rid}
[[ $rid == "$native_rid" ]] || {
  echo "Refusing to label a $native_rid host build as $rid; use the matching native runner." >&2
  exit 1
}

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"

command -v dotnet >/dev/null || { echo "dotnet not found" >&2; exit 1; }
command -v pwsh >/dev/null || { echo "pwsh not found" >&2; exit 1; }
command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found" >&2; exit 1; }

build_args=(-Version "$version" -Runtimes "$rid" -NativePackage)
[[ ${ISKRA_ALLOW_DIRTY:-0} == 1 ]] && build_args+=(-AllowDirty)
[[ ${ISKRA_REQUIRE_TAG:-0} == 1 ]] && build_args+=(-RequireTag)
pwsh -NoProfile -File installer/build-unix-bundles.ps1 "${build_args[@]}"

if [[ ${ISKRA_REQUIRE_TAG:-0} == 1 && -z ${ISKRA_LINUX_SIGNING_KEY_ID:-} ]]; then
  echo "Tagged Linux builds require ISKRA_LINUX_SIGNING_KEY_ID; unsigned overrides are ignored." >&2
  exit 1
fi

bundle_name="Iskra-$version-$rid"
bundle_dir="$repo_root/artifacts/$bundle_name"
package_root="$repo_root/artifacts/.deb-$version-$deb_arch"
deb_path="$repo_root/artifacts/iskra_${version}_${deb_arch}.deb"

chmod 0755 "$bundle_dir/Iskra.Avalonia" "$bundle_dir/Iskra.Cli" "$bundle_dir/install.sh" "$bundle_dir/uninstall.sh"
"$bundle_dir/Iskra.Cli" --help >/dev/null

case "$package_root" in
  "$repo_root"/artifacts/.deb-*) rm -rf -- "$package_root" ;;
  *) echo "Refusing unsafe package staging path: $package_root" >&2; exit 1 ;;
esac
mkdir -p \
  "$package_root/DEBIAN" \
  "$package_root/opt/iskra" \
  "$package_root/usr/bin" \
  "$package_root/usr/share/applications" \
  "$package_root/usr/share/icons/hicolor/256x256/apps" \
  "$package_root/lib/udev/rules.d"

install -m 0755 "$bundle_dir/Iskra.Avalonia" "$package_root/opt/iskra/Iskra.Avalonia"
install -m 0755 "$bundle_dir/Iskra.Cli" "$package_root/opt/iskra/Iskra.Cli"
install -m 0644 "$bundle_dir/iskra.png" "$package_root/opt/iskra/iskra.png"
install -m 0644 "$bundle_dir/iskra.png" "$package_root/usr/share/icons/hicolor/256x256/apps/iskra.png"
install -m 0644 "$bundle_dir/iskra.desktop" "$package_root/usr/share/applications/iskra.desktop"
install -m 0644 "$bundle_dir/99-black-magic-probe.rules" "$package_root/lib/udev/rules.d/99-black-magic-probe.rules"
ln -s /opt/iskra/Iskra.Cli "$package_root/usr/bin/iskra-cli"

installed_size=$(du -sk "$package_root/opt" "$package_root/usr" "$package_root/lib" | awk '{sum += $1} END {print sum}')
cat > "$package_root/DEBIAN/control" <<EOF
Package: iskra
Version: $deb_version
Section: devel
Priority: optional
Architecture: $deb_arch
Installed-Size: $installed_size
Maintainer: Iskra maintainers <noreply@github.com>
Depends: ca-certificates, libc6, libgcc-s1, libgssapi-krb5-2, libicu74, libssl3t64, libstdc++6, tzdata, zlib1g, libx11-6, libice6, libsm6, libfontconfig1, libsecret-tools, gdb-multiarch | gdb-arm-none-eabi
Homepage: https://github.com/oleksandrmaslov/iskra
Description: Factory firmware flasher for Black Magic Probe
 Iskra verifies signed firmware catalogs, flashes ARM Cortex-M targets through
 Black Magic Probe, and records every attempt in SQLite.
EOF

cat > "$package_root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if command -v udevadm >/dev/null 2>&1; then
  udevadm control --reload-rules || true
  udevadm trigger --subsystem-match=tty --action=add || true
fi
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications || true
fi
exit 0
EOF

cat > "$package_root/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
if command -v udevadm >/dev/null 2>&1; then
  udevadm control --reload-rules || true
fi
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database /usr/share/applications || true
fi
exit 0
EOF
chmod 0755 "$package_root/DEBIAN/postinst" "$package_root/DEBIAN/postrm"

rm -f -- "$deb_path"
dpkg-deb --root-owner-group --build "$package_root" "$deb_path"
dpkg-deb --info "$deb_path"
dpkg-deb --contents "$deb_path" >/dev/null

if command -v lintian >/dev/null 2>&1; then
  lintian --no-tag-display-limit "$deb_path"
fi

tar_path="$repo_root/artifacts/$bundle_name.tar.gz"
checksum_path="$repo_root/artifacts/Iskra-$version-$rid-native-SHA256SUMS.txt"
(
  cd "$repo_root/artifacts"
  sha256sum "$(basename "$tar_path")" "$(basename "$deb_path")" > "$(basename "$checksum_path")"
)

signature_path=""
if [[ -n ${ISKRA_LINUX_SIGNING_KEY_ID:-} ]]; then
  command -v gpg >/dev/null || { echo "gpg is required for release signing" >&2; exit 1; }
  signature_path="$checksum_path.asc"
  gpg --batch --yes --local-user "$ISKRA_LINUX_SIGNING_KEY_ID" --armor --detach-sign \
    --output "$signature_path" "$checksum_path"
elif [[ ${ISKRA_REQUIRE_TAG:-0} == 1 || ${ISKRA_ALLOW_UNSIGNED:-0} != 1 ]]; then
  echo "No ISKRA_LINUX_SIGNING_KEY_ID supplied. Set ISKRA_ALLOW_UNSIGNED=1 only for engineering packages." >&2
  exit 1
else
  echo "Producing an unsigned engineering .deb because ISKRA_ALLOW_UNSIGNED=1." >&2
fi

rm -rf -- "$package_root"
printf 'Built:\n  %s\n  %s\n  %s\n' "$tar_path" "$deb_path" "$checksum_path"
[[ -z $signature_path ]] || printf '  %s\n' "$signature_path"
