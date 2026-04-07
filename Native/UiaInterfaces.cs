using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace KoEnVue.Native;

// ================================================================
// IUIAutomation (CUIAutomation coclass)
// GUID: 30cbe57d-d9d0-452a-ab13-7ac5ac4825ee
// vtable: IUnknown(3) + CompareElements(3) + CompareRuntimeIds(4)
//         + GetRootElement(5) + ElementFromHandle(6)
// ================================================================

[GeneratedComInterface]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
internal partial interface IUIAutomation
{
    // slot 3: CompareElements
    [PreserveSig]
    int Reserved_CompareElements(nint el1, nint el2, out int areSame);

    // slot 4: CompareRuntimeIds
    [PreserveSig]
    int Reserved_CompareRuntimeIds(nint runtimeId1, nint runtimeId2, out int areSame);

    // slot 5: GetRootElement
    [PreserveSig]
    int Reserved_GetRootElement(out nint root);

    // slot 6: ElementFromHandle — 사용
    [PreserveSig]
    int ElementFromHandle(nint hwnd, out nint element);
}

// ================================================================
// IUIAutomationElement
// GUID: d22108aa-8ac5-49a5-837b-37bbb3d7591e
// vtable: IUnknown(3) + 13 placeholder (slot 3~15) + GetCurrentPattern(slot 16)
// ================================================================

[GeneratedComInterface]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
internal partial interface IUIAutomationElement
{
    // slot 3: SetFocus
    [PreserveSig] int Reserved_3(nint a);
    // slot 4: GetRuntimeId
    [PreserveSig] int Reserved_4(out nint a);
    // slot 5: FindFirst
    [PreserveSig] int Reserved_5(int scope, nint condition, out nint found);
    // slot 6: FindAll
    [PreserveSig] int Reserved_6(int scope, nint condition, out nint found);
    // slot 7: FindFirstBuildCache
    [PreserveSig] int Reserved_7(int scope, nint condition, nint cacheRequest, out nint found);
    // slot 8: FindAllBuildCache
    [PreserveSig] int Reserved_8(int scope, nint condition, nint cacheRequest, out nint found);
    // slot 9: BuildUpdatedCache
    [PreserveSig] int Reserved_9(nint cacheRequest, out nint updated);
    // slot 10: GetCurrentPropertyValue
    [PreserveSig] int Reserved_10(int propertyId, out nint retVal);
    // slot 11: GetCurrentPropertyValueEx
    [PreserveSig] int Reserved_11(int propertyId, int ignoreDefault, out nint retVal);
    // slot 12: GetCachedPropertyValue
    [PreserveSig] int Reserved_12(int propertyId, out nint retVal);
    // slot 13: GetCachedPropertyValueEx
    [PreserveSig] int Reserved_13(int propertyId, int ignoreDefault, out nint retVal);
    // slot 14: GetCurrentPatternAs
    [PreserveSig] int Reserved_14(int patternId, ref Guid riid, out nint patternObject);
    // slot 15: GetCachedPatternAs
    [PreserveSig] int Reserved_15(int patternId, ref Guid riid, out nint patternObject);

    // slot 16: GetCurrentPattern — 사용
    [PreserveSig]
    int GetCurrentPattern(int patternId, out nint patternObject);
}

// ================================================================
// IUIAutomationTextPattern2
// GUID: 506a921a-fcc9-409f-b3b6-2de54d46a84d
// vtable: IUnknown(3) + IUIAutomationTextPattern 6개(slot 3~8)
//         + RangeFromAnnotation(slot 9) + GetCaretRange(slot 10)
// ================================================================

[GeneratedComInterface]
[Guid("506a921a-fcc9-409f-b3b6-2de54d46a84d")]
internal partial interface IUIAutomationTextPattern2
{
    // --- IUIAutomationTextPattern 메서드 (slot 3~8) ---
    // slot 3: RangeFromPoint
    [PreserveSig] int Reserved_RangeFromPoint(double x, double y, out nint range);
    // slot 4: RangeFromChild
    [PreserveSig] int Reserved_RangeFromChild(nint child, out nint range);
    // slot 5: GetSelection
    [PreserveSig] int Reserved_GetSelection(out nint ranges);
    // slot 6: GetVisibleRanges
    [PreserveSig] int Reserved_GetVisibleRanges(out nint ranges);
    // slot 7: get_DocumentRange
    [PreserveSig] int Reserved_DocumentRange(out nint range);
    // slot 8: get_SupportedTextSelection
    [PreserveSig] int Reserved_SupportedTextSelection(out int supportedTextSelection);

    // --- IUIAutomationTextPattern2 고유 ---
    // slot 9: RangeFromAnnotation
    [PreserveSig] int Reserved_RangeFromAnnotation(nint annotation, out nint range);

    // slot 10: GetCaretRange — 사용
    [PreserveSig]
    int GetCaretRange([MarshalAs(UnmanagedType.Bool)] out bool isActive, out nint range);
}

// ================================================================
// IUIAutomationTextRange
// GUID: a543cc6a-f4ae-494b-8239-c814481187a8
// vtable: IUnknown(3) + 7 placeholder (slot 3~9) + GetBoundingRectangles(slot 10)
// ================================================================

[GeneratedComInterface]
[Guid("a543cc6a-f4ae-494b-8239-c814481187a8")]
internal partial interface IUIAutomationTextRange
{
    // slot 3: Clone
    [PreserveSig] int Reserved_Clone(out nint clonedRange);
    // slot 4: Compare
    [PreserveSig] int Reserved_Compare(nint range, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
    // slot 5: CompareEndpoints
    [PreserveSig] int Reserved_CompareEndpoints(int srcEndPoint, nint range, int targetEndPoint, out int compValue);
    // slot 6: ExpandToEnclosingUnit
    [PreserveSig] int Reserved_ExpandToEnclosingUnit(int textUnit);
    // slot 7: FindAttribute
    [PreserveSig] int Reserved_FindAttribute(int attr, nint val, int backward, out nint found);
    // slot 8: FindText
    [PreserveSig] int Reserved_FindText(nint text, int backward, int ignoreCase, out nint found);
    // slot 9: GetAttributeValue
    [PreserveSig] int Reserved_GetAttributeValue(int attr, out nint value);

    // slot 10: GetBoundingRectangles — 사용
    // SAFEARRAY(double) 반환 → out nint로 받고 OleAut32 SafeArray API로 추출
    [PreserveSig]
    int GetBoundingRectangles(out nint boundingRects);
}
