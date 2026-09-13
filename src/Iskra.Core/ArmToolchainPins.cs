namespace Iskra.Core;

/// <summary>
/// The Arm GNU Toolchain build Iskra installs and trusts, mirroring
/// <c>installer/arm-toolchain.pins.ps1</c> so the in-app installer and the
/// packaged setup EXE can never deliver two different compilers to the factory
/// floor under the same claim.
/// <para>The SHA-256 is the supply-chain control, not the URL: a download that
/// does not match the pin is deleted instead of executed. Keep every value here
/// in step with the PowerShell pins file — <c>ArmToolchainPinsTests</c> fails
/// the build if the two drift.</para>
/// </summary>
public static class ArmToolchainPins
{
    public const string Version = "15.2.rel1";

    /// <summary>Windows installer bundled by the setup EXE and fetched by the in-app installer.</summary>
    public const string WindowsFileName =
        "arm-gnu-toolchain-15.2.rel1-mingw-w64-i686-arm-none-eabi.msi";

    public const string WindowsUrl =
        "https://developer.arm.com/-/media/Files/downloads/gnu/15.2.rel1/binrel/"
        + "arm-gnu-toolchain-15.2.rel1-mingw-w64-i686-arm-none-eabi.msi";

    public const string WindowsSha256 =
        "6606feaf791fdbe83f8c6cfbb7db6429f778fb3444ea21b80a7c4d28f84f5dc8";

    /// <summary>
    /// Largest download accepted before the transfer is abandoned. The pinned
    /// MSI is well under this; the bound stops an unbounded body from filling
    /// a station's disk.
    /// </summary>
    public const long MaxInstallerBytes = 512L * 1024 * 1024;
}
