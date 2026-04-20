using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

internal static partial class Ole32
{
    // COM 초기화/해제는 [STAThread] 로 CLR 이 메인 스레드에서 자동 수행 — 명시적 P/Invoke 불필요.
    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, out IntPtr ppv);
}
