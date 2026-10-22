using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DearImGuiInjectionPlus.Windows;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Startup;
using UnityEngine;

namespace DearImGuiInjectionPlus.Patches
{
    [HarmonyPatch(typeof(Input))]
    internal static class UnityInputBlocker
    {
        internal static Vector3 LastMousePosition = Vector3.zero;
        private static bool _resetInputAxesSupported = true;

        public static int NativeICallHooksApplied { get; internal set; }
        public static int NativeICallHooksSkipped { get; internal set; }
        public static int InternalPatchesApplied { get; internal set; }
        public static int InternalPatchesSkipped { get; internal set; }

        private static int _firstAxisLogged = 0;
        private static int _firstMouseButtonLogged = 0;

        public static bool HasInterceptedAxis => _firstAxisLogged != 0;
        public static bool HasInterceptedMouseButton => _firstMouseButtonLogged != 0;

        private static void LogFirstAxisIntercept()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _firstAxisLogged, 1, 0) == 0)
            {
                Log.Info("[UnityInputBlocker] Intercepted active game poll for GetAxis while ImGui is capturing input.");
            }
        }

        private static void LogFirstMouseButtonIntercept()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _firstMouseButtonLogged, 1, 0) == 0)
            {
                Log.Info("[UnityInputBlocker] Intercepted active game poll for GetMouseButton while ImGui is capturing input.");
            }
        }

        private static readonly List<IDetour> _nativeDetours = new();
        private static readonly List<Delegate> _pinnedDelegates = new();
        private static readonly HashSet<IntPtr> _hookedICallAddresses = new();

        public static bool IsAddressAlreadyHooked(IntPtr functionPointer)
        {
            return functionPointer != IntPtr.Zero && _hookedICallAddresses.Contains(functionPointer);
        }

        public static bool IsKeyIntHooked => _origGetKeyInt != null;
        public static bool IsKeyDownIntHooked => _origGetKeyDownInt != null;
        public static bool IsKeyUpIntHooked => _origGetKeyUpInt != null;
        public static bool IsMouseButtonHooked => _origGetMouseButton != null;
        public static bool IsMouseButtonDownHooked => _origGetMouseButtonDown != null;
        public static bool IsMouseButtonUpHooked => _origGetMouseButtonUp != null;
        public static bool IsAnyKeyHooked => _origGetAnyKey != null;
        public static bool IsAnyKeyDownHooked => _origGetAnyKeyDown != null;
        public static bool IsMousePositionInjectedHooked => _origGetMousePositionInjected != null;
        public static bool IsMouseScrollDeltaInjectedHooked => _origGetMouseScrollDeltaInjected != null;
        public static bool IsMousePositionDeltaInjectedHooked => _origGetMousePositionDeltaInjected != null;
        public static bool IsCursorLockStateHooked => _origSetCursorLockState != null;
        public static bool IsCursorVisibleHooked => _origSetCursorVisible != null;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte d_IntToBool(int arg0);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte d_VoidToBool();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate float d_IntPtrToFloat(IntPtr arg0);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate float d_PtrLenToFloat(IntPtr ptr, int len);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte d_IntPtrToBool(IntPtr arg0);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte d_PtrLenToBool(IntPtr ptr, int len);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private unsafe delegate void d_Vector3Out(Vector3* ret);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private unsafe delegate void d_Vector2Out(Vector2* ret);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_SetCursorLockState(CursorLockMode value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_SetCursorVisible(byte value);

        private static d_IntToBool _origGetMouseButton;
        private static d_IntToBool _origGetMouseButtonDown;
        private static d_IntToBool _origGetMouseButtonUp;

        private static d_IntToBool _origGetKeyInt;
        private static d_IntToBool _origGetKeyDownInt;
        private static d_IntToBool _origGetKeyUpInt;

        private static d_VoidToBool _origGetAnyKey;
        private static d_VoidToBool _origGetAnyKeyDown;

        private static d_Vector3Out _origGetMousePositionInjected;
        private static d_Vector2Out _origGetMouseScrollDeltaInjected;
        private static d_Vector3Out _origGetMousePositionDeltaInjected;

        private static d_IntPtrToFloat _origGetAxisInjected;
        private static d_IntPtrToFloat _origGetAxisRawInjected;
        private static d_IntPtrToBool _origGetButtonInjected;
        private static d_IntPtrToBool _origGetButtonDownInjected;
        private static d_IntPtrToBool _origGetButtonUpInjected;
        private static d_IntPtrToBool _origGetKeyStringInjected;
        private static d_IntPtrToBool _origGetKeyDownStringInjected;
        private static d_IntPtrToBool _origGetKeyUpStringInjected;

        private static d_PtrLenToFloat _origGetAxisUnmanaged;
        private static d_PtrLenToFloat _origGetAxisRawUnmanaged;
        private static d_PtrLenToBool _origGetButtonUnmanaged;
        private static d_PtrLenToBool _origGetButtonDownUnmanaged;
        private static d_PtrLenToBool _origGetButtonUpUnmanaged;
        private static d_PtrLenToBool _origGetKeyStringUnmanaged;
        private static d_PtrLenToBool _origGetKeyDownStringUnmanaged;
        private static d_PtrLenToBool _origGetKeyUpStringUnmanaged;

        private static d_IntPtrToFloat _origLegacyGetAxis;
        private static d_IntPtrToFloat _origLegacyGetAxisRaw;
        private static d_IntPtrToBool _origLegacyGetButton;
        private static d_IntPtrToBool _origLegacyGetButtonDown;
        private static d_IntPtrToBool _origLegacyGetButtonUp;

        private static d_SetCursorLockState _origSetCursorLockState;
        private static d_SetCursorVisible _origSetCursorVisible;

        internal static bool ShouldBlockMouse()
        {
            if (!DearImGuiInjectionPlus.IsCursorVisible)
            {
                return false;
            }

            if (!DearImGuiInjectionPlus.BlockAllInputsWhenOpen)
            {
                return DearImGuiInjectionPlus.WantCaptureMouse;
            }

            return true;
        }

        internal static bool ShouldBlockKeyboard()
        {
            if (!DearImGuiInjectionPlus.IsCursorVisible)
            {
                return false;
            }

            if (!DearImGuiInjectionPlus.BlockAllInputsWhenOpen)
            {
                return DearImGuiInjectionPlus.WantCaptureKeyboard;
            }

            return true;
        }

        internal static bool ShouldBlockKeyCode(KeyCode key)
        {
            if (key is >= KeyCode.Mouse0 and <= KeyCode.Mouse6)
            {
                return ShouldBlockMouse();
            }

            return ShouldBlockKeyboard();
        }

        internal static void TryResetInputAxes()
        {
            if (!_resetInputAxesSupported)
                return;

            try
            {
                Input.ResetInputAxes();
            }
            catch
            {
                _resetInputAxesSupported = false;
            }
        }

        // =====================================================================
        // A. Direct UnityPlayer.dll Native ICall Detours (Defeats C++ /O2 Inlining)
        // =====================================================================
        // In IL2CPP builds compiled with /O2, 1-line ICall wrappers on UnityEngine.Input
        // (GetMouseButton, GetMouseButtonDown, GetKeyInt, get_mousePosition_Injected, etc.)
        // and UnityEngine.Cursor (set_lockState, set_visible) have CallerCount = 0 in
        // GameAssembly.dll because the C++ compiler inlines the wrapper directly into
        // every caller. Each caller resolves and invokes the UnityPlayer.dll ICall pointer
        // directly, bypassing HarmonyX's detour on the GameAssembly.dll wrapper.
        internal static unsafe void ApplyNativeICallHooks(ref int applied, ref int skipped)
        {
            int startApplied = applied;
            int startSkipped = skipped;

            // 1. Mouse Buttons (int -> bool)
            HookICall("UnityEngine.Input::GetMouseButton(System.Int32)", "UnityEngine.Input::GetMouseButton",
                new d_IntToBool(Detour_GetMouseButton), out _origGetMouseButton, ref applied, ref skipped);
            HookICall("UnityEngine.Input::GetMouseButtonDown(System.Int32)", "UnityEngine.Input::GetMouseButtonDown",
                new d_IntToBool(Detour_GetMouseButtonDown), out _origGetMouseButtonDown, ref applied, ref skipped);
            HookICall("UnityEngine.Input::GetMouseButtonUp(System.Int32)", "UnityEngine.Input::GetMouseButtonUp",
                new d_IntToBool(Detour_GetMouseButtonUp), out _origGetMouseButtonUp, ref applied, ref skipped);

            // 2. KeyCode Int ICalls (KeyCode -> bool, includes KeyCode.Mouse0..Mouse6)
            HookICall("UnityEngine.Input::GetKeyInt(UnityEngine.KeyCode)", "UnityEngine.Input::GetKeyInt",
                new d_IntToBool(Detour_GetKeyInt), out _origGetKeyInt, ref applied, ref skipped);
            HookICall("UnityEngine.Input::GetKeyDownInt(UnityEngine.KeyCode)", "UnityEngine.Input::GetKeyDownInt",
                new d_IntToBool(Detour_GetKeyDownInt), out _origGetKeyDownInt, ref applied, ref skipped);
            HookICall("UnityEngine.Input::GetKeyUpInt(UnityEngine.KeyCode)", "UnityEngine.Input::GetKeyUpInt",
                new d_IntToBool(Detour_GetKeyUpInt), out _origGetKeyUpInt, ref applied, ref skipped);

            // 3. AnyKey ICalls (void -> bool)
            HookICall("UnityEngine.Input::get_anyKey()", "UnityEngine.Input::get_anyKey",
                new d_VoidToBool(Detour_GetAnyKey), out _origGetAnyKey, ref applied, ref skipped);
            HookICall("UnityEngine.Input::get_anyKeyDown()", "UnityEngine.Input::get_anyKeyDown",
                new d_VoidToBool(Detour_GetAnyKeyDown), out _origGetAnyKeyDown, ref applied, ref skipped);

            // 4. Mouse Position, Scroll Delta & Mouse Position Delta _Injected ICalls
            HookICall("UnityEngine.Input::get_mousePosition_Injected(UnityEngine.Vector3&)", "UnityEngine.Input::get_mousePosition_Injected",
                new d_Vector3Out(Detour_GetMousePositionInjected), out _origGetMousePositionInjected, ref applied, ref skipped);
            HookICall("UnityEngine.Input::get_mouseScrollDelta_Injected(UnityEngine.Vector2&)", "UnityEngine.Input::get_mouseScrollDelta_Injected",
                new d_Vector2Out(Detour_GetMouseScrollDeltaInjected), out _origGetMouseScrollDeltaInjected, ref applied, ref skipped);
            HookICall("UnityEngine.Input::get_mousePositionDelta_Injected(UnityEngine.Vector3&)", "UnityEngine.Input::get_mousePositionDelta_Injected",
                new d_Vector3Out(Detour_GetMousePositionDeltaInjected), out _origGetMousePositionDeltaInjected, ref applied, ref skipped);

            // 5. Unity 2023.3 / Unity 6 InputUnsafeUtility *_Injected(ManagedSpanWrapper&) ICalls
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetAxis_Injected(UnityEngine.Bindings.ManagedSpanWrapper&)",
                "UnityEngine.Internal.InputUnsafeUtility::GetAxis_Injected",
                new d_IntPtrToFloat(Detour_GetAxisInjected), out _origGetAxisInjected, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetAxisRaw_Injected(UnityEngine.Bindings.ManagedSpanWrapper&)",
                "UnityEngine.Internal.InputUnsafeUtility::GetAxisRaw_Injected",
                new d_IntPtrToFloat(Detour_GetAxisRawInjected), out _origGetAxisRawInjected, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetButton_Injected(UnityEngine.Bindings.ManagedSpanWrapper&)",
                "UnityEngine.Internal.InputUnsafeUtility::GetButton_Injected",
                new d_IntPtrToBool(Detour_GetButtonInjected), out _origGetButtonInjected, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetButtonDown_Injected(UnityEngine.Bindings.ManagedSpanWrapper&)",
                "UnityEngine.Internal.InputUnsafeUtility::GetButtonDown_Injected",
                new d_IntPtrToBool(Detour_GetButtonDownInjected), out _origGetButtonDownInjected, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetButtonUp_Injected(UnityEngine.Bindings.ManagedSpanWrapper&)",
                "UnityEngine.Internal.InputUnsafeUtility::GetButtonUp_Injected",
                new d_IntPtrToBool(Detour_GetButtonUpInjected), out _origGetButtonUpInjected, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetKeyString_Injected(UnityEngine.Bindings.ManagedSpanWrapper&)",
                "UnityEngine.Internal.InputUnsafeUtility::GetKeyString_Injected",
                new d_IntPtrToBool(Detour_GetKeyStringInjected), out _origGetKeyStringInjected, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetKeyDownString_Injected(UnityEngine.Bindings.ManagedSpanWrapper&)",
                "UnityEngine.Internal.InputUnsafeUtility::GetKeyDownString_Injected",
                new d_IntPtrToBool(Detour_GetKeyDownStringInjected), out _origGetKeyDownStringInjected, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetKeyUpString_Injected(UnityEngine.Bindings.ManagedSpanWrapper&)",
                "UnityEngine.Internal.InputUnsafeUtility::GetKeyUpString_Injected",
                new d_IntPtrToBool(Detour_GetKeyUpStringInjected), out _origGetKeyUpStringInjected, ref applied, ref skipped);

            // 6. Unity 2022.2 - 2023.2 InputUnsafeUtility *__Unmanaged(byte*, int) ICalls
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetAxis__Unmanaged(System.Byte*,System.Int32)",
                "UnityEngine.Internal.InputUnsafeUtility::GetAxis__Unmanaged",
                new d_PtrLenToFloat(Detour_GetAxisUnmanaged), out _origGetAxisUnmanaged, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetAxisRaw__Unmanaged(System.Byte*,System.Int32)",
                "UnityEngine.Internal.InputUnsafeUtility::GetAxisRaw__Unmanaged",
                new d_PtrLenToFloat(Detour_GetAxisRawUnmanaged), out _origGetAxisRawUnmanaged, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetButton__Unmanaged(System.Byte*,System.Int32)",
                "UnityEngine.Internal.InputUnsafeUtility::GetButton__Unmanaged",
                new d_PtrLenToBool(Detour_GetButtonUnmanaged), out _origGetButtonUnmanaged, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetButtonDown__Unmanaged(System.Byte*,System.Int32)",
                "UnityEngine.Internal.InputUnsafeUtility::GetButtonDown__Unmanaged",
                new d_PtrLenToBool(Detour_GetButtonDownUnmanaged), out _origGetButtonDownUnmanaged, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetButtonUp__Unmanaged(System.Byte*,System.Int32)",
                "UnityEngine.Internal.InputUnsafeUtility::GetButtonUp__Unmanaged",
                new d_PtrLenToBool(Detour_GetButtonUpUnmanaged), out _origGetButtonUpUnmanaged, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetKeyString__Unmanaged(System.Byte*,System.Int32)",
                "UnityEngine.Internal.InputUnsafeUtility::GetKeyString__Unmanaged",
                new d_PtrLenToBool(Detour_GetKeyStringUnmanaged), out _origGetKeyStringUnmanaged, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetKeyDownString__Unmanaged(System.Byte*,System.Int32)",
                "UnityEngine.Internal.InputUnsafeUtility::GetKeyDownString__Unmanaged",
                new d_PtrLenToBool(Detour_GetKeyDownStringUnmanaged), out _origGetKeyDownStringUnmanaged, ref applied, ref skipped);
            HookICall("UnityEngine.Internal.InputUnsafeUtility::GetKeyUpString__Unmanaged(System.Byte*,System.Int32)",
                "UnityEngine.Internal.InputUnsafeUtility::GetKeyUpString__Unmanaged",
                new d_PtrLenToBool(Detour_GetKeyUpStringUnmanaged), out _origGetKeyUpStringUnmanaged, ref applied, ref skipped);

            // 7. Unity 2019 - 2022.1 Legacy Input::GetAxis / GetAxisRaw / GetButton(System.String) ICalls
            HookICall("UnityEngine.Input::GetAxis(System.String)", "UnityEngine.Input::GetAxis",
                new d_IntPtrToFloat(Detour_LegacyGetAxis), out _origLegacyGetAxis, ref applied, ref skipped);
            HookICall("UnityEngine.Input::GetAxisRaw(System.String)", "UnityEngine.Input::GetAxisRaw",
                new d_IntPtrToFloat(Detour_LegacyGetAxisRaw), out _origLegacyGetAxisRaw, ref applied, ref skipped);
            HookICall("UnityEngine.Input::GetButton(System.String)", "UnityEngine.Input::GetButton",
                new d_IntPtrToBool(Detour_LegacyGetButton), out _origLegacyGetButton, ref applied, ref skipped);
            HookICall("UnityEngine.Input::GetButtonDown(System.String)", "UnityEngine.Input::GetButtonDown",
                new d_IntPtrToBool(Detour_LegacyGetButtonDown), out _origLegacyGetButtonDown, ref applied, ref skipped);
            HookICall("UnityEngine.Input::GetButtonUp(System.String)", "UnityEngine.Input::GetButtonUp",
                new d_IntPtrToBool(Detour_LegacyGetButtonUp), out _origLegacyGetButtonUp, ref applied, ref skipped);

            // 8. UnityEngine.Cursor ICalls (set_lockState, set_visible) - also C++-inlined (CallerCount: 0)
            HookICall("UnityEngine.Cursor::set_lockState(UnityEngine.CursorLockMode)", "UnityEngine.Cursor::set_lockState",
                new d_SetCursorLockState(Detour_SetCursorLockState), out _origSetCursorLockState, ref applied, ref skipped);
            HookICall("UnityEngine.Cursor::set_visible(System.Boolean)", "UnityEngine.Cursor::set_visible",
                new d_SetCursorVisible(Detour_SetCursorVisible), out _origSetCursorVisible, ref applied, ref skipped);

            NativeICallHooksApplied = applied - startApplied;
            NativeICallHooksSkipped = skipped - startSkipped;
        }

        private static void HookICall<TDelegate>(
            string icallWithArgs,
            string icallWithoutArgs,
            TDelegate detourDelegate,
            out TDelegate originalTrampoline,
            ref int applied,
            ref int skipped) where TDelegate : Delegate
        {
            originalTrampoline = null;
            if (detourDelegate == null)
            {
                skipped++;
                Log.Debug($"[DearImGuiInjectionPlus] Skipped native ICall hook: {icallWithoutArgs ?? icallWithArgs} (detour delegate is null)");
                return;
            }

            try
            {
                if (Il2CppInteropRuntime.Instance?.DetourProvider == null)
                {
                    skipped++;
                    Log.Warning($"[UnityInputBlocker] DetourProvider unavailable for native ICall hook: {icallWithoutArgs ?? icallWithArgs}");
                    return;
                }

                IntPtr ptr = IL2CPP.il2cpp_resolve_icall(icallWithArgs);
                if (ptr == IntPtr.Zero && !string.IsNullOrEmpty(icallWithoutArgs))
                {
                    ptr = IL2CPP.il2cpp_resolve_icall(icallWithoutArgs);
                }

                if (ptr == IntPtr.Zero || !_hookedICallAddresses.Add(ptr))
                {
                    skipped++;
                    Log.Debug($"[DearImGuiInjectionPlus] Skipped native ICall hook: {icallWithoutArgs ?? icallWithArgs} (ICall resolution returned null or duplicate)");
                    return;
                }

                _pinnedDelegates.Add(detourDelegate);
                var detour = Il2CppInteropRuntime.Instance.DetourProvider.Create(ptr, detourDelegate);
                detour.Apply();
                originalTrampoline = detour.GenerateTrampoline<TDelegate>();
                _nativeDetours.Add(detour);
                applied++;
                Log.Info($"[DearImGuiInjectionPlus] Applied native ICall hook: {icallWithoutArgs ?? icallWithArgs}");
            }
            catch (Exception ex)
            {
                skipped++;
                Log.Warning($"[UnityInputBlocker] Graceful fallback: failed to hook native ICall {icallWithoutArgs ?? icallWithArgs}: {ex.Message}");
            }
        }

        // =====================================================================
        // B. Harmony Patches for UnityEngine.Internal.InputUnsafeUtility & SendMouseEvents
        // =====================================================================
        // In Unity 2022.2+ / Unity 6, Input.GetAxis / GetAxisRaw / GetButton* are C++-inlined
        // into calls to UnityEngine.Internal.InputUnsafeUtility (CallerCount: 57 for GetAxis,
        // 47 for GetAxisRaw, 6/7/4 for GetButton*). We patch both InputUnsafeUtility and
        // SendMouseEvents.DoSendMouseEvents (3D Collider OnMouseDown/OnMouseOver raycasts).
        internal static void ApplyInternalHarmonyPatches(Harmony harmony, ref int applied, ref int skipped)
        {
            int startApplied = applied;
            int startSkipped = skipped;
            const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            try
            {
                var inputAsm = typeof(Input).Assembly;

                // 1. UnityEngine.Internal.InputUnsafeUtility
                var unsafeUtilType = inputAsm.GetType("UnityEngine.Internal.InputUnsafeUtility");
                if (unsafeUtilType != null)
                {
                    RuntimeHelpers.RunClassConstructor(unsafeUtilType.TypeHandle);

                    var blockAxisPrefix = new HarmonyMethod
                    {
                        method = typeof(UnityInputBlocker).GetMethod(nameof(BlockAxis), AllStatic)
                    };
                    var blockButtonPrefix = new HarmonyMethod
                    {
                        method = typeof(UnityInputBlocker).GetMethod(nameof(BlockButton), AllStatic)
                    };

                    TryPatchGeneratedMethod(harmony, FindMethodSafely(unsafeUtilType, "GetAxis", AllStatic, new[] { typeof(string) }),
                        blockAxisPrefix, ref applied, ref skipped);
                    TryPatchGeneratedMethod(harmony, FindMethodSafely(unsafeUtilType, "GetAxisRaw", AllStatic, new[] { typeof(string) }),
                        blockAxisPrefix, ref applied, ref skipped);
                    TryPatchGeneratedMethod(harmony, FindMethodSafely(unsafeUtilType, "GetButton", AllStatic, new[] { typeof(string) }),
                        blockButtonPrefix, ref applied, ref skipped);
                    TryPatchGeneratedMethod(harmony, FindMethodSafely(unsafeUtilType, "GetButtonDown", AllStatic, new[] { typeof(string) }),
                        blockButtonPrefix, ref applied, ref skipped);
                    TryPatchGeneratedMethod(harmony, FindMethodSafely(unsafeUtilType, "GetButtonUp", AllStatic, new[] { typeof(string) }),
                        blockButtonPrefix, ref applied, ref skipped);

                    TryPatchGeneratedMethod(harmony, FindMethodSafely(unsafeUtilType, "GetAxis_Injected", AllStatic),
                        blockAxisPrefix, ref applied, ref skipped);
                    TryPatchGeneratedMethod(harmony, FindMethodSafely(unsafeUtilType, "GetAxisRaw_Injected", AllStatic),
                        blockAxisPrefix, ref applied, ref skipped);
                    TryPatchGeneratedMethod(harmony, FindMethodSafely(unsafeUtilType, "GetButton_Injected", AllStatic),
                        blockButtonPrefix, ref applied, ref skipped);
                    TryPatchGeneratedMethod(harmony, FindMethodSafely(unsafeUtilType, "GetButtonDown_Injected", AllStatic),
                        blockButtonPrefix, ref applied, ref skipped);
                    TryPatchGeneratedMethod(harmony, FindMethodSafely(unsafeUtilType, "GetButtonUp_Injected", AllStatic),
                        blockButtonPrefix, ref applied, ref skipped);
                }

                // 2. UnityEngine.SendMouseEvents.DoSendMouseEvents (3D Collider OnMouseDown/OnMouseDrag/OnMouseOver)
                var sendMouseEventsType = inputAsm.GetType("UnityEngine.SendMouseEvents");
                if (sendMouseEventsType != null)
                {
                    RuntimeHelpers.RunClassConstructor(sendMouseEventsType.TypeHandle);
                    var doSendMethod = FindMethodSafely(sendMouseEventsType, "DoSendMouseEvents", AllStatic);
                    var blockSendMousePrefix = new HarmonyMethod
                    {
                        method = typeof(UnityInputBlocker).GetMethod(nameof(BlockSendMouseEvents), AllStatic)
                    };
                    TryPatchGeneratedMethod(harmony, doSendMethod, blockSendMousePrefix, ref applied, ref skipped);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[UnityInputBlocker] Error applying internal InputLegacyModule patches: {ex.Message}");
            }

            InternalPatchesApplied = applied - startApplied;
            InternalPatchesSkipped = skipped - startSkipped;
        }

        private static MethodInfo FindMethodSafely(Type type, string name, BindingFlags flags, Type[] paramTypes = null)
        {
            if (type == null || string.IsNullOrEmpty(name))
                return null;

            try
            {
                if (paramTypes != null)
                    return type.GetMethod(name, flags, null, paramTypes, null);
                return type.GetMethod(name, flags);
            }
            catch (AmbiguousMatchException)
            {
                var methods = type.GetMethods(flags);
                if (paramTypes != null)
                {
                    for (int i = 0; i < methods.Length; i++)
                    {
                        var m = methods[i];
                        if (m.Name != name) continue;
                        var pars = m.GetParameters();
                        if (pars.Length != paramTypes.Length) continue;
                        bool match = true;
                        for (int j = 0; j < pars.Length; j++)
                        {
                            if (pars[j].ParameterType != paramTypes[j])
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match) return m;
                    }
                }
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name == name) return methods[i];
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Debug($"[UnityInputBlocker] FindMethodSafely for {type.Name}.{name} exception: {ex.Message}");
                return null;
            }
        }

        private static void TryPatchGeneratedMethod(
            Harmony harmony,
            MethodInfo targetMethod,
            HarmonyMethod prefix,
            ref int applied,
            ref int skipped)
        {
            if (harmony == null || targetMethod == null || prefix?.method == null)
            {
                skipped++;
                Log.Debug("[DearImGuiInjectionPlus] Skipped internal patch: harmony, target method or prefix was null");
                return;
            }

            try
            {
                // Verify return type compatibility if prefix accesses __result
                var prefixMethod = prefix.method;
                var prefixParams = prefixMethod.GetParameters();
                var targetParams = targetMethod.GetParameters();

                for (int i = 0; i < prefixParams.Length; i++)
                {
                    var p = prefixParams[i];
                    if (p.Name == "__result")
                    {
                        var expectedType = p.ParameterType.IsByRef
                            ? p.ParameterType.GetElementType()
                            : p.ParameterType;

                        if (targetMethod.ReturnType != expectedType)
                        {
                            skipped++;
                            Log.Debug($"[DearImGuiInjectionPlus] Skipped internal patch for {targetMethod.DeclaringType?.Name}.{targetMethod.Name}: return type mismatch (Target: {targetMethod.ReturnType.Name}, Prefix expects: {expectedType?.Name})");
                            return;
                        }
                    }
                    else if (p.Name != null && p.Name.StartsWith("__") && int.TryParse(p.Name.Substring(2), out int argIdx))
                    {
                        if (argIdx >= targetParams.Length)
                        {
                            skipped++;
                            Log.Debug($"[DearImGuiInjectionPlus] Skipped internal patch for {targetMethod.DeclaringType?.Name}.{targetMethod.Name}: parameter {p.Name} out of range (Target parameter count: {targetParams.Length})");
                            return;
                        }
                    }
                }

                var nativePtrField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(targetMethod);
                if (nativePtrField == null || (IntPtr)nativePtrField.GetValue(null) == IntPtr.Zero)
                {
                    skipped++;
                    Log.Debug($"[DearImGuiInjectionPlus] Skipped internal patch for {targetMethod.DeclaringType?.Name}.{targetMethod.Name} (NativeMethodInfoPtr is null/zero or field accessor - stripped in IL2CPP)");
                    return;
                }

                IntPtr methodInfoPtr = (IntPtr)nativePtrField.GetValue(null);
                IntPtr methodPointer = Marshal.ReadIntPtr(methodInfoPtr);
                if (methodPointer != IntPtr.Zero && IsAddressAlreadyHooked(methodPointer))
                {
                    skipped++;
                    Log.Info($"[DearImGuiInjectionPlus] Skipped internal patch for {targetMethod.DeclaringType?.Name}.{targetMethod.Name}: already intercepted by native ICall hook (0x{methodPointer.ToInt64():X})");
                    return;
                }

                harmony.Patch(targetMethod, prefix: prefix);
                applied++;
                Log.Info($"[DearImGuiInjectionPlus] Applied: {targetMethod.DeclaringType?.FullName}.{targetMethod.Name}");
            }
            catch (Exception ex)
            {
                skipped++;
                Log.Warning($"[UnityInputBlocker] Graceful fallback: could not patch internal method {targetMethod.DeclaringType?.Name}.{targetMethod.Name}: {ex.Message}");
            }
        }

        internal static void DisposeNativeICallHooks()
        {
            foreach (var detour in _nativeDetours)
            {
                try
                {
                    detour?.Dispose();
                }
                catch
                {
                    // Ignore during engine teardown
                }
            }
            _nativeDetours.Clear();
            _hookedICallAddresses.Clear();
        }

        // =====================================================================
        // Native ICall Detour Callbacks
        // =====================================================================
        private static byte Detour_GetMouseButton(int button)
        {
            if (ShouldBlockMouse())
            {
                LogFirstMouseButtonIntercept();
                return 0;
            }
            return _origGetMouseButton != null ? _origGetMouseButton(button) : (byte)0;
        }

        private static byte Detour_GetMouseButtonDown(int button)
        {
            if (ShouldBlockMouse())
            {
                LogFirstMouseButtonIntercept();
                return 0;
            }
            return _origGetMouseButtonDown != null ? _origGetMouseButtonDown(button) : (byte)0;
        }

        private static byte Detour_GetMouseButtonUp(int button)
        {
            if (ShouldBlockMouse())
            {
                LogFirstMouseButtonIntercept();
                return 0;
            }
            return _origGetMouseButtonUp != null ? _origGetMouseButtonUp(button) : (byte)0;
        }

        private static byte Detour_GetKeyInt(int key)
        {
            if (ShouldBlockKeyCode((KeyCode)key))
                return 0;
            return _origGetKeyInt != null ? _origGetKeyInt(key) : (byte)0;
        }

        private static byte Detour_GetKeyDownInt(int key)
        {
            if (ShouldBlockKeyCode((KeyCode)key))
                return 0;
            return _origGetKeyDownInt != null ? _origGetKeyDownInt(key) : (byte)0;
        }

        private static byte Detour_GetKeyUpInt(int key)
        {
            if (ShouldBlockKeyCode((KeyCode)key))
                return 0;
            return _origGetKeyUpInt != null ? _origGetKeyUpInt(key) : (byte)0;
        }

        private static byte Detour_GetAnyKey()
        {
            if (ShouldBlockKeyboard() || ShouldBlockMouse())
                return 0;
            return _origGetAnyKey != null ? _origGetAnyKey() : (byte)0;
        }

        private static byte Detour_GetAnyKeyDown()
        {
            if (ShouldBlockKeyboard() || ShouldBlockMouse())
                return 0;
            return _origGetAnyKeyDown != null ? _origGetAnyKeyDown() : (byte)0;
        }

        private static unsafe void Detour_GetMousePositionInjected(Vector3* ret)
        {
            if (ret == null)
                return;

            if (ShouldBlockMouse())
            {
                *ret = LastMousePosition;
                return;
            }

            if (_origGetMousePositionInjected != null)
            {
                _origGetMousePositionInjected(ret);
                if (!DearImGuiInjectionPlus.IsCursorVisible)
                {
                    LastMousePosition = *ret;
                }
            }
        }

        private static unsafe void Detour_GetMouseScrollDeltaInjected(Vector2* ret)
        {
            if (ret == null)
                return;

            if (ShouldBlockMouse())
            {
                *ret = Vector2.zero;
                return;
            }

            _origGetMouseScrollDeltaInjected?.Invoke(ret);
        }

        private static unsafe void Detour_GetMousePositionDeltaInjected(Vector3* ret)
        {
            if (ret == null)
                return;

            if (ShouldBlockMouse())
            {
                *ret = Vector3.zero;
                return;
            }

            _origGetMousePositionDeltaInjected?.Invoke(ret);
        }

        private static float Detour_GetAxisInjected(IntPtr axisNameSpan)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
            {
                LogFirstAxisIntercept();
                return 0f;
            }
            return _origGetAxisInjected != null ? _origGetAxisInjected(axisNameSpan) : 0f;
        }

        private static float Detour_GetAxisRawInjected(IntPtr axisNameSpan)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
            {
                LogFirstAxisIntercept();
                return 0f;
            }
            return _origGetAxisRawInjected != null ? _origGetAxisRawInjected(axisNameSpan) : 0f;
        }

        private static byte Detour_GetButtonInjected(IntPtr buttonNameSpan)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetButtonInjected != null ? _origGetButtonInjected(buttonNameSpan) : (byte)0;
        }

        private static byte Detour_GetButtonDownInjected(IntPtr buttonNameSpan)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetButtonDownInjected != null ? _origGetButtonDownInjected(buttonNameSpan) : (byte)0;
        }

        private static byte Detour_GetButtonUpInjected(IntPtr buttonNameSpan)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetButtonUpInjected != null ? _origGetButtonUpInjected(buttonNameSpan) : (byte)0;
        }

        private static byte Detour_GetKeyStringInjected(IntPtr nameSpan)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetKeyStringInjected != null ? _origGetKeyStringInjected(nameSpan) : (byte)0;
        }

        private static byte Detour_GetKeyDownStringInjected(IntPtr nameSpan)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetKeyDownStringInjected != null ? _origGetKeyDownStringInjected(nameSpan) : (byte)0;
        }

        private static byte Detour_GetKeyUpStringInjected(IntPtr nameSpan)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetKeyUpStringInjected != null ? _origGetKeyUpStringInjected(nameSpan) : (byte)0;
        }

        private static float Detour_GetAxisUnmanaged(IntPtr ptr, int len)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
            {
                LogFirstAxisIntercept();
                return 0f;
            }
            return _origGetAxisUnmanaged != null ? _origGetAxisUnmanaged(ptr, len) : 0f;
        }

        private static float Detour_GetAxisRawUnmanaged(IntPtr ptr, int len)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
            {
                LogFirstAxisIntercept();
                return 0f;
            }
            return _origGetAxisRawUnmanaged != null ? _origGetAxisRawUnmanaged(ptr, len) : 0f;
        }

        private static byte Detour_GetButtonUnmanaged(IntPtr ptr, int len)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetButtonUnmanaged != null ? _origGetButtonUnmanaged(ptr, len) : (byte)0;
        }

        private static byte Detour_GetButtonDownUnmanaged(IntPtr ptr, int len)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetButtonDownUnmanaged != null ? _origGetButtonDownUnmanaged(ptr, len) : (byte)0;
        }

        private static byte Detour_GetButtonUpUnmanaged(IntPtr ptr, int len)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetButtonUpUnmanaged != null ? _origGetButtonUpUnmanaged(ptr, len) : (byte)0;
        }

        private static byte Detour_GetKeyStringUnmanaged(IntPtr ptr, int len)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetKeyStringUnmanaged != null ? _origGetKeyStringUnmanaged(ptr, len) : (byte)0;
        }

        private static byte Detour_GetKeyDownStringUnmanaged(IntPtr ptr, int len)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetKeyDownStringUnmanaged != null ? _origGetKeyDownStringUnmanaged(ptr, len) : (byte)0;
        }

        private static byte Detour_GetKeyUpStringUnmanaged(IntPtr ptr, int len)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origGetKeyUpStringUnmanaged != null ? _origGetKeyUpStringUnmanaged(ptr, len) : (byte)0;
        }

        private static float Detour_LegacyGetAxis(IntPtr axisName)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
            {
                LogFirstAxisIntercept();
                return 0f;
            }
            return _origLegacyGetAxis != null ? _origLegacyGetAxis(axisName) : 0f;
        }

        private static float Detour_LegacyGetAxisRaw(IntPtr axisName)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
            {
                LogFirstAxisIntercept();
                return 0f;
            }
            return _origLegacyGetAxisRaw != null ? _origLegacyGetAxisRaw(axisName) : 0f;
        }

        private static byte Detour_LegacyGetButton(IntPtr buttonName)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origLegacyGetButton != null ? _origLegacyGetButton(buttonName) : (byte)0;
        }

        private static byte Detour_LegacyGetButtonDown(IntPtr buttonName)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origLegacyGetButtonDown != null ? _origLegacyGetButtonDown(buttonName) : (byte)0;
        }

        private static byte Detour_LegacyGetButtonUp(IntPtr buttonName)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
                return 0;
            return _origLegacyGetButtonUp != null ? _origLegacyGetButtonUp(buttonName) : (byte)0;
        }

        private static void Detour_SetCursorLockState(CursorLockMode value)
        {
            if (!CursorLockStatePatch.SuppressStateCapture)
            {
                CursorLockStatePatch.DesiredGameLockState = value;
            }
            if (DearImGuiInjectionPlus.IsCursorVisible)
            {
                value = CursorLockMode.None;
                User32.ClipCursor(IntPtr.Zero);
            }
            _origSetCursorLockState?.Invoke(value);
        }

        private static void Detour_SetCursorVisible(byte value)
        {
            if (!CursorVisiblePatch.SuppressStateCapture)
            {
                CursorVisiblePatch.DesiredGameVisible = value != 0;
            }
            if (DearImGuiInjectionPlus.IsCursorVisible)
            {
                value = 1;
            }
            _origSetCursorVisible?.Invoke(value);
        }

        private static bool BlockSendMouseEvents()
        {
            return !ShouldBlockMouse();
        }

        // =====================================================================
        // C. Harmony Patches on UnityEngine.Input Wrappers
        // =====================================================================

        // 1. Zero out mouse axes (stops camera scripts polling Input.GetAxis)
        [HarmonyPatch(typeof(Input), nameof(Input.GetAxis), typeof(string))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw), typeof(string))]
        [HarmonyPrefix]
        private static bool BlockAxis(ref float __result)
        {
            if (ShouldBlockMouse() || ShouldBlockKeyboard())
            {
                LogFirstAxisIntercept();
                __result = 0f;
                return false;
            }
            return true;
        }

        // 2. Block key states (stops character movement scripts polling Input.GetKey & C++-inlined GetKeyInt)
        // Handled natively via Detour_GetKeyInt / Detour_GetKeyDownInt / Detour_GetKeyUpInt.
        // Retained as internal helper for managed dynamic fallback if native ICall hook is unavailable.
        internal static bool BlockKey(KeyCode __0, ref bool __result)
        {
            if (ShouldBlockKeyCode(__0))
            {
                __result = false;
                return false;
            }
            return true;
        }

        // 3. Block GetButton (Fire1, Fire2, Jump, etc.)
        [HarmonyPatch(typeof(Input), nameof(Input.GetButton), typeof(string))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonDown), typeof(string))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonUp), typeof(string))]
        [HarmonyPrefix]
        private static bool BlockButton(ref bool __result)
        {
            if (ShouldBlockKeyboard() || ShouldBlockMouse())
            {
                __result = false;
                return false;
            }
            return true;
        }

        // 4. Block GetKey string overloads ("mouse 0", "mouse 1", "mouse 2", "mouse 3", "mouse 4")
        [HarmonyPatch(typeof(Input), nameof(Input.GetKey), typeof(string))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), typeof(string))]
        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), typeof(string))]
        [HarmonyPrefix]
        private static bool BlockKeyString(ref bool __result)
        {
            if (ShouldBlockKeyboard() || ShouldBlockMouse())
            {
                __result = false;
                return false;
            }
            return true;
        }

        // 5. Zero out scroll wheel deltas in legacy input (both property getter and C++-inlined _Injected icall)
        [HarmonyPatch(typeof(Input), nameof(Input.mouseScrollDelta), MethodType.Getter)]
        [HarmonyPrefix]
        private static bool BlockScrollDelta(ref Vector2 __result)
        {
            if (ShouldBlockMouse())
            {
                __result = Vector2.zero;
                return false;
            }
            return true;
        }

        // Handled natively via Detour_GetMouseScrollDeltaInjected.
        internal static bool BlockScrollDeltaInjected(ref Vector2 __0)
        {
            if (ShouldBlockMouse())
            {
                __0 = Vector2.zero;
                return false;
            }
            return true;
        }

        // 6. Block mouse button states (stops shooting / interacting polling Input.GetMouseButton)
        // Handled natively via Detour_GetMouseButton / Detour_GetMouseButtonDown / Detour_GetMouseButtonUp.
        // Retained as internal helper for managed dynamic fallback if native ICall hook is unavailable.
        internal static bool BlockMouseButton(ref bool __result)
        {
            if (ShouldBlockMouse())
            {
                LogFirstMouseButtonIntercept();
                __result = false;
                return false;
            }
            return true;
        }

        // 7. Block general key flags
        // Handled natively via Detour_GetAnyKey / Detour_GetAnyKeyDown.
        // Retained as internal helper for managed dynamic fallback if native ICall hook is unavailable.
        internal static bool BlockAnyKey(ref bool __result)
        {
            if (ShouldBlockKeyboard() || ShouldBlockMouse())
            {
                __result = false;
                return false;
            }
            return true;
        }

        // 8. Block input text stream (stops text fields from receiving typed characters)
        [HarmonyPatch(typeof(Input), nameof(Input.inputString), MethodType.Getter)]
        [HarmonyPrefix]
        private static bool BlockInputString(ref string __result)
        {
            if (ShouldBlockKeyboard())
            {
                __result = string.Empty;
                return false;
            }
            return true;
        }

        // 9. Freeze mousePosition (both property getter and C++-inlined _Injected icall)
        [HarmonyPatch(typeof(Input), nameof(Input.mousePosition), MethodType.Getter)]
        [HarmonyPrefix]
        private static bool BlockMousePosition(ref Vector3 __result)
        {
            if (ShouldBlockMouse())
            {
                __result = LastMousePosition;
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(Input), nameof(Input.mousePosition), MethodType.Getter)]
        [HarmonyPostfix]
        private static void TrackMousePosition(Vector3 __result)
        {
            if (!DearImGuiInjectionPlus.IsCursorVisible)
            {
                LastMousePosition = __result;
            }
        }

        // Handled natively via Detour_GetMousePositionInjected.
        internal static bool BlockMousePositionInjected(ref Vector3 __0)
        {
            if (ShouldBlockMouse())
            {
                __0 = LastMousePosition;
                return false;
            }
            return true;
        }

        internal static void TrackMousePositionInjected(ref Vector3 __0)
        {
            if (!DearImGuiInjectionPlus.IsCursorVisible)
            {
                LastMousePosition = __0;
            }
        }

        // 10. Zero out mousePositionDelta (Unity 2022.2+ / Unity 6 3D camera delta property & _Injected icall)
        [HarmonyPatch(typeof(Input), "mousePositionDelta", MethodType.Getter)]
        [HarmonyPrefix]
        private static bool BlockMousePositionDelta(ref Vector3 __result)
        {
            if (ShouldBlockMouse())
            {
                __result = Vector3.zero;
                return false;
            }
            return true;
        }

        // Handled natively via Detour_GetMousePositionDeltaInjected.
        internal static bool BlockMousePositionDeltaInjected(ref Vector3 __0)
        {
            if (ShouldBlockMouse())
            {
                __0 = Vector3.zero;
                return false;
            }
            return true;
        }
    }
}
