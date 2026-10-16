using System.Runtime.InteropServices;

namespace WitchDrawer.Native.Windows;

internal static class DesktopWindowLayerPolicy
{
    // Cache the decision so window messages never query the Windows edition.
    internal static bool IsEnabled { get; } = DetectCompatibilityPath();

    internal static bool MatchesReportedSystem(Version version, uint productType) =>
        IsWindows11DesktopSurfaceVersion(version) && IsProfessionalFamily(productType);

    // Windows 11 的桌面可视表面是 WorkerW 而不是 Progman：靠「盒子归 Progman 属主」挂住的旧路径
    // 会被外壳的属主带一起重整，整组盒子被盖到宿主下面（2026-10-16 在 22621 Pro 上实测复现：
    // 点了 Win+D 后四个盒子整组排到桌面宿主之下，且长时间不恢复）。因此把「不设属主 + 常驻保持
    // 在桌面宿主之上」的层维护路径从 23H2 Workstation 扩到 22H2/23H2 的专业版族。
    private static bool IsWindows11DesktopSurfaceVersion(Version version) =>
        version.Major == 10 && version.Minor == 0 && version.Build is 22621 or 22631;

    // PRODUCT_PROFESSIONAL(0x30) / _N(0x31) / PRODUCT_PRO_WORKSTATION(0xA1) / _N(0xA2)
    private static bool IsProfessionalFamily(uint productType) =>
        productType is 0x00000030 or 0x00000031 or 0x000000A1 or 0x000000A2;

    private static bool DetectCompatibilityPath()
    {
        var version = Environment.OSVersion.Version;
        if (!OperatingSystem.IsWindows() || !IsWindows11DesktopSurfaceVersion(version))
        {
            return false;
        }

        // Use the native SKU instead of a localized edition name. Query failure
        // keeps the existing ownership path; monthly update revisions are irrelevant.
        return GetProductInfo(10, 0, 0, 0, out var productType)
            && MatchesReportedSystem(version, productType);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProductInfo(
        uint majorVersion, uint minorVersion, uint servicePackMajor,
        uint servicePackMinor, out uint productType);
}
