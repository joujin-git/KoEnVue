using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KoEnVue.Core.Native;

/// <summary>
/// HFONT 래퍼. ReleaseHandle에서 DeleteObject 호출.
/// HFONT는 앱 시작 시 1회 생성, DPI 변경 시에만 재생성.
/// </summary>
internal sealed class SafeFontHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFontHandle() : base(ownsHandle: true) { }
    public SafeFontHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        return Gdi32.DeleteObject(handle);
    }
}

/// <summary>
/// HBITMAP 래퍼. ReleaseHandle에서 DeleteObject 호출.
/// DIB 재생성 조건: DPI 변경, 스타일 변경, 크기 변경.
/// </summary>
internal sealed class SafeBitmapHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeBitmapHandle() : base(ownsHandle: true) { }
    public SafeBitmapHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        return Gdi32.DeleteObject(handle);
    }
}

/// <summary>
/// HICON 래퍼. ReleaseHandle에서 DestroyIcon 호출.
/// 트레이 아이콘 상태 변경 시 새 HICON 생성 후 이전 핸들 해제.
/// </summary>
internal sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeIconHandle() : base(ownsHandle: true) { }
    public SafeIconHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        // DestroyIcon은 User32.cs에 선언됨
        return User32.DestroyIcon(handle);
    }
}
