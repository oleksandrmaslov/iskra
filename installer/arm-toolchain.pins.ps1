# Single source of truth for the bundled Arm GNU Toolchain prerequisite.
#
# Both installer builders (WPF and Avalonia) dot-source this file. The SHA-256
# is the supply-chain control: a toolchain MSI that does not match is deleted
# rather than bundled. Keep the version, filename, URL, and hash in step — a
# split between the two setup EXEs would mean two different compilers reaching
# the factory floor under the same claim.
#
# To move to a new toolchain release:
#   1. Update all four values below together.
#   2. Delete installer/deps/ so the pinned MSI is re-downloaded and re-verified.
#   3. Update the version-specific DirectorySearch/FileSearch paths in
#      Product*.wxs and Bundle*.wxs, which look for the installed layout.

$ArmToolchainVersion  = "15.2.rel1"
$ArmToolchainFileName = "arm-gnu-toolchain-15.2.rel1-mingw-w64-i686-arm-none-eabi.msi"
$ArmToolchainUrl      = "https://developer.arm.com/-/media/Files/downloads/gnu/15.2.rel1/binrel/arm-gnu-toolchain-15.2.rel1-mingw-w64-i686-arm-none-eabi.msi"
$ArmToolchainSha256   = "6606feaf791fdbe83f8c6cfbb7db6429f778fb3444ea21b80a7c4d28f84f5dc8"
