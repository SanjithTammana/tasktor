using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

// UIAutomationClient.h, declared for source-generated COM (no runtime marshalling, Native AOT-safe). Only the slots this
// client calls carry real signatures; the rest are placeholders that keep the vtable order and are never called.
#pragma warning disable SA1201, SA1600, CA1707, IDE1006
namespace Tasktor.Capture.UIAutomation.Com;

internal static class UiaIds
{
    public static readonly Guid ClsidCuiAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
    public static readonly Guid IidTextPattern = new("32eba289-3583-42c9-9c59-3b6d9a1e9b6a");

    public const int TreeScopeElement = 1, TreeScopeChildren = 2, TreeScopeSubtree = 7;
    public const int PropBoundingRectangle = 30001, PropProcessId = 30002, PropControlType = 30003, PropName = 30005,
        PropAutomationId = 30011, PropIsPassword = 30019, PropNativeWindowHandle = 30020, PropIsOffscreen = 30022,
        PropIsTextPatternAvailable = 30040, PropIsValuePatternAvailable = 30043, PropValueValue = 30045;
    public const int PatternText = 10014;
    public const int EventTextChanged = 20015;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UiaPoint
{
    public int X;
    public int Y;
}

/// <summary>A VARIANT, blittable (24 bytes on 64-bit). Read with <see cref="UiaVariant"/>, which clears it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RawVariant
{
    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Value;
    public nint Value2;
}

internal static unsafe partial class UiaVariant
{
    private const ushort VtI4 = 3, VtBstr = 8, VtBool = 11;

    public static RawVariant Bool(bool value) => new() { Vt = VtBool, Value = value ? -1 : 0 };

    public static int? Int(RawVariant v)
    {
        try
        {
            return v.Vt == VtI4 ? unchecked((int)v.Value) : null;
        }
        finally
        {
            Clear(ref v);
        }
    }

    public static bool? Bool(RawVariant v)
    {
        try
        {
            return v.Vt == VtBool ? unchecked((short)v.Value) != 0 : null;
        }
        finally
        {
            Clear(ref v);
        }
    }

    public static string? String(RawVariant v)
    {
        try
        {
            return v.Vt == VtBstr && v.Value != 0 ? Marshal.PtrToStringBSTR(v.Value) : null;
        }
        finally
        {
            Clear(ref v);
        }
    }

    public static void Clear(ref RawVariant v)
    {
        if (v.Vt != 0 && OperatingSystem.IsWindows())
        {
            fixed (RawVariant* p = &v)
            {
                VariantClear(p);
            }
        }
    }

    [LibraryImport("oleaut32.dll")]
    private static partial int VariantClear(RawVariant* v);
}

[GeneratedComInterface]
[Guid("352ffba8-0973-437c-a61f-f64cafd81df9")]
internal partial interface IUIAutomationCondition
{
}

[GeneratedComInterface]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
internal partial interface IUIAutomation
{
    void CompareElements();
    void CompareRuntimeIds();
    void GetRootElement();
    IUIAutomationElement ElementFromHandle(nint hwnd);
    void ElementFromPoint();
    void GetFocusedElement();
    void GetRootElementBuildCache();
    IUIAutomationElement ElementFromHandleBuildCache(nint hwnd, IUIAutomationCacheRequest cacheRequest);
    IUIAutomationElement ElementFromPointBuildCache(UiaPoint pt, IUIAutomationCacheRequest cacheRequest);
    void GetFocusedElementBuildCache();
    void CreateTreeWalker();
    void GetControlViewWalker();
    void GetContentViewWalker();
    void GetRawViewWalker();
    void GetRawViewCondition();
    IUIAutomationCondition GetControlViewCondition();
    void GetContentViewCondition();
    IUIAutomationCacheRequest CreateCacheRequest();
    void CreateTrueCondition();
    void CreateFalseCondition();
    IUIAutomationCondition CreatePropertyCondition(int propertyId, RawVariant value);
    void CreatePropertyConditionEx();
    IUIAutomationCondition CreateAndCondition(IUIAutomationCondition condition1, IUIAutomationCondition condition2);
    void CreateAndConditionFromArray();
    void CreateAndConditionFromNativeArray();
    void CreateOrCondition();
    void CreateOrConditionFromArray();
    void CreateOrConditionFromNativeArray();
    void CreateNotCondition();
    void AddAutomationEventHandler();
    void RemoveAutomationEventHandler();
    void AddPropertyChangedEventHandlerNativeArray();
    void AddPropertyChangedEventHandler();
    void RemovePropertyChangedEventHandler();
    void AddStructureChangedEventHandler();
    void RemoveStructureChangedEventHandler();
    void AddFocusChangedEventHandler(IUIAutomationCacheRequest? cacheRequest, IUIAutomationFocusChangedEventHandler handler);
    void RemoveFocusChangedEventHandler(IUIAutomationFocusChangedEventHandler handler);
    void RemoveAllEventHandlers();
    void IntNativeArrayToSafeArray();
    void IntSafeArrayToNativeArray();
    void RectToVariant();
    void VariantToRect();
    void SafeArrayToRectNativeArray();
    void CreateProxyFactoryEntry();
    void GetProxyFactoryMapping();
    void GetPropertyProgrammaticName();
    void GetPatternProgrammaticName();
    void PollForPotentialSupportedPatterns();
    void PollForPotentialSupportedProperties();
    void CheckNotSupported();
    void GetReservedNotSupportedValue();
    void GetReservedMixedAttributeValue();
    void ElementFromIAccessible();
    void ElementFromIAccessibleBuildCache();
}

[GeneratedComInterface]
[Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a")]
internal partial interface IUIAutomation2 : IUIAutomation
{
    void GetAutoSetFocus();
    void SetAutoSetFocus(int autoSetFocus);
    uint GetConnectionTimeout();
    void SetConnectionTimeout(uint milliseconds);
    uint GetTransactionTimeout();
    void SetTransactionTimeout(uint milliseconds);
}

[GeneratedComInterface]
[Guid("73d768da-9b51-4b89-936e-c209290973e7")]
internal partial interface IUIAutomation3 : IUIAutomation2
{
    void AddTextEditTextChangedEventHandler();
    void RemoveTextEditTextChangedEventHandler();
}

[GeneratedComInterface]
[Guid("1189c02a-05f8-4319-8e21-e817e3db2860")]
internal partial interface IUIAutomation4 : IUIAutomation3
{
    void AddChangesEventHandler();
    void RemoveChangesEventHandler();
}

[GeneratedComInterface]
[Guid("25f700c8-d816-4057-a9dc-3cbdee77e256")]
internal partial interface IUIAutomation5 : IUIAutomation4
{
    void AddNotificationEventHandler();
    void RemoveNotificationEventHandler();
}

[GeneratedComInterface]
[Guid("aae072da-29e3-413d-87a7-192dbf81ed10")]
internal partial interface IUIAutomation6 : IUIAutomation5
{
    IUIAutomationEventHandlerGroup CreateEventHandlerGroup();
    void AddEventHandlerGroup(IUIAutomationElement element, IUIAutomationEventHandlerGroup handlerGroup);
    void RemoveEventHandlerGroup(IUIAutomationElement element, IUIAutomationEventHandlerGroup handlerGroup);
    void GetConnectionRecoveryBehavior();
    void SetConnectionRecoveryBehavior(int behavior);
    void GetCoalesceEvents();
    void SetCoalesceEvents(int coalesce);
    void AddActiveTextPositionChangedEventHandler();
    void RemoveActiveTextPositionChangedEventHandler();
}

[GeneratedComInterface]
[Guid("c9ee12f2-c13b-4408-997c-639914377f4e")]
internal partial interface IUIAutomationEventHandlerGroup
{
    void AddActiveTextPositionChangedEventHandler();
    void AddAutomationEventHandler(int eventId, int scope, IUIAutomationCacheRequest? cacheRequest, IUIAutomationEventHandler handler);
    void AddChangesEventHandler();
    void AddNotificationEventHandler();
    unsafe void AddPropertyChangedEventHandler(int scope, IUIAutomationCacheRequest? cacheRequest, IUIAutomationPropertyChangedEventHandler handler, int* propertyArray, int propertyCount);
    void AddStructureChangedEventHandler(int scope, IUIAutomationCacheRequest? cacheRequest, IUIAutomationStructureChangedEventHandler handler);
    void AddTextEditTextChangedEventHandler();
}

[GeneratedComInterface]
[Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03")]
internal partial interface IUIAutomationCacheRequest
{
    void AddProperty(int propertyId);
    void AddPattern(int patternId);
    void Clone();
    void GetTreeScope();
    void SetTreeScope(int scope);
    void GetTreeFilter();
    void SetTreeFilter(IUIAutomationCondition filter);
    void GetAutomationElementMode();
    void SetAutomationElementMode(int mode);
}

[GeneratedComInterface]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
internal partial interface IUIAutomationElement
{
    void SetFocus();
    void GetRuntimeId();
    void FindFirst();
    void FindAll();
    void FindFirstBuildCache();
    void FindAllBuildCache();
    void BuildUpdatedCache();
    RawVariant GetCurrentPropertyValue(int propertyId);
    void GetCurrentPropertyValueEx();
    RawVariant GetCachedPropertyValue(int propertyId);
    void GetCachedPropertyValueEx();
    nint GetCurrentPatternAs(int patternId, in Guid riid);
    void GetCachedPatternAs();
    void GetCurrentPattern();
    void GetCachedPattern();
    void GetCachedParent();
    IUIAutomationElementArray? GetCachedChildren();
}

[GeneratedComInterface]
[Guid("14314595-b4bc-4055-95f2-58f2e42c9855")]
internal partial interface IUIAutomationElementArray
{
    int GetLength();
    IUIAutomationElement GetElement(int index);
}

[GeneratedComInterface]
[Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a")]
internal partial interface IUIAutomationTextPattern
{
    void RangeFromPoint();
    void RangeFromChild();
    void GetSelection();
    IUIAutomationTextRangeArray GetVisibleRanges();
    void GetDocumentRange();
    void GetSupportedTextSelection();
}

[GeneratedComInterface]
[Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6")]
internal partial interface IUIAutomationTextRangeArray
{
    int GetLength();
    IUIAutomationTextRange GetElement(int index);
}

[GeneratedComInterface]
[Guid("a543cc6a-f4ae-494b-8239-c814481187a8")]
internal partial interface IUIAutomationTextRange
{
    void Clone();
    void Compare();
    void CompareEndpoints();
    void ExpandToEnclosingUnit();
    void FindAttribute();
    void FindText();
    void GetAttributeValue();
    void GetBoundingRectangles();
    void GetEnclosingElement();
    [return: MarshalAs(UnmanagedType.BStr)]
    string? GetText(int maxLength);
}

[GeneratedComInterface]
[Guid("146c3c17-f12e-4e22-8c27-f894b9b79c69")]
internal partial interface IUIAutomationEventHandler
{
    void HandleAutomationEvent(IUIAutomationElement sender, int eventId);
}

[GeneratedComInterface]
[Guid("40cd37d4-c756-4b0c-8c6f-bddfeeb13b50")]
internal partial interface IUIAutomationPropertyChangedEventHandler
{
    void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, RawVariant newValue);
}

[GeneratedComInterface]
[Guid("e81d1b4e-11c5-42f8-9754-e7036c79f054")]
internal partial interface IUIAutomationStructureChangedEventHandler
{
    void HandleStructureChangedEvent(IUIAutomationElement sender, int changeType, nint runtimeId);
}

[GeneratedComInterface]
[Guid("c270f6b5-5c69-4290-9745-7a7f97169468")]
internal partial interface IUIAutomationFocusChangedEventHandler
{
    void HandleFocusChangedEvent(IUIAutomationElement sender);
}
