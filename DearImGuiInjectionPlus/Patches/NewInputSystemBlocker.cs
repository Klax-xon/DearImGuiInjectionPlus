using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace DearImGuiInjectionPlus.Patches
{
    internal static class NewInputSystemBlocker
    {
        private static Type _inputSystemType;
        private static MethodInfo _disableDeviceMethod;
        private static MethodInfo _enableDeviceMethod;
        private static MethodInfo _resetDeviceMethod;
        private static PropertyInfo _devicesProp;
        private static PropertyInfo _devicesCountProp;
        private static MethodInfo _devicesIndexerGetter;
        private static PropertyInfo _devicesArrayFieldProp;
        private static PropertyInfo _devicesLengthFieldProp;
        private static PropertyInfo _deviceEnabledProp;
        private static PropertyInfo _deviceIdProp;
        private static PropertyInfo _mouseCurrentProp;
        private static PropertyInfo _pointerCurrentProp;
        private static PropertyInfo _keyboardCurrentProp;
        private static int _disableMethodParamCount;
        private static int _resetMethodParamCount;
        private static volatile int _mouseDeviceId = -1;
        private static volatile int _keyboardDeviceId = -1;
        private static bool _initialized;
        private static bool _hasSearched;

        // Keyed by native IL2CPP pointer (or RuntimeHelpers.GetHashCode fallback) to avoid Il2CppObjectBase wrapper identity churn
        private static readonly Dictionary<IntPtr, object> _disabledDevices = new();
        private static volatile bool _hasDisabledDevices;
        private static readonly object _lock = new();

        private static Assembly ResolveInteropAssembly(params string[] assemblyNames)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var name in assemblyNames)
            {
                var match = loaded.FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }

            try
            {
                string interopDir = Path.Combine(global::BepInEx.Paths.BepInExRootPath, "interop");
                if (Directory.Exists(interopDir))
                {
                    foreach (var name in assemblyNames)
                    {
                        string candidate = Path.Combine(interopDir, name + ".dll");
                        if (File.Exists(candidate))
                        {
                            return Assembly.LoadFrom(candidate);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[NewInputSystemBlocker] Failed probing interop assembly ({string.Join(", ", assemblyNames)}): {ex.Message}");
            }

            return null;
        }

        public static void ApplyNativeInputHook(Harmony harmony, ref int applied, ref int skipped)
        {
            try
            {
                var nativeInputType = Type.GetType("UnityEngineInternal.Input.NativeInputSystem, UnityEngine.InputModule")
                                      ?? ResolveInteropAssembly("UnityEngine.InputModule", "Il2CppUnityEngine.InputModule")
                                          ?.GetType("UnityEngineInternal.Input.NativeInputSystem");

                if (nativeInputType == null)
                {
                    skipped++;
                    return;
                }

                RuntimeHelpers.RunClassConstructor(nativeInputType.TypeHandle);

                var notifyUpdateMethod = nativeInputType.GetMethod(
                    "NotifyUpdate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (notifyUpdateMethod == null)
                {
                    skipped++;
                    return;
                }

                var ptrField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(notifyUpdateMethod);
                if (ptrField != null && (IntPtr)ptrField.GetValue(null) == IntPtr.Zero)
                {
                    skipped++;
                    return;
                }

                var prefixInfo = typeof(NewInputSystemBlocker).GetMethod(
                    nameof(NotifyUpdatePrefix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                harmony.Patch(notifyUpdateMethod, prefix: new HarmonyMethod { method = prefixInfo });
                applied++;
                Log.Info("[NewInputSystemBlocker] Hooked UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate.");
            }
            catch (Exception ex)
            {
                skipped++;
                Log.Warning($"[NewInputSystemBlocker] Skipped NativeInputSystem.NotifyUpdate hook: {ex.Message}");
            }
        }

        // Prefix for UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate(NativeInputUpdateType updateType, IntPtr eventBuffer)
        private static bool NotifyUpdatePrefix(IntPtr __1)
        {
            if (__1 == IntPtr.Zero)
                return true;

            if (InputManager.HasPendingKeyReleaseFlush)
            {
                InputManager.ConsumePendingKeyReleaseFlushFrame();
                return true;
            }

            bool blockMouse = UnityInputBlocker.ShouldBlockMouse();
            bool blockKeyboard = UnityInputBlocker.ShouldBlockKeyboard();

            if (!blockMouse && !blockKeyboard)
                return true;

            try
            {
                // NativeInputEventBuffer layout:
                // offset 0: void* eventBuffer (8 bytes on x64)
                // offset 8: int eventCount (4 bytes)
                // offset 12: int sizeInBytes (4 bytes)
                // offset 16: int capacityInBytes (4 bytes)
                if (blockMouse && blockKeyboard)
                {
                    Marshal.WriteInt32(__1, 8, 0);
                    Marshal.WriteInt32(__1, 12, 0);
                    return true;
                }

                // Selective swallowing: filter events in buffer by deviceId if known, or drop non-passthrough events
                IntPtr bufferPtr = Marshal.ReadIntPtr(__1, 0);
                int eventCount = Marshal.ReadInt32(__1, 8);
                int totalSize = Marshal.ReadInt32(__1, 12);

                if (bufferPtr == IntPtr.Zero || eventCount <= 0 || totalSize < 20)
                    return true;

                int kbId = _keyboardDeviceId;
                int mouseId = _mouseDeviceId;

                if (kbId <= 0 && mouseId <= 0)
                {
                    if (blockMouse)
                    {
                        Marshal.WriteInt32(__1, 8, 0);
                        Marshal.WriteInt32(__1, 12, 0);
                    }
                    return true;
                }

                int offset = 0;
                for (int i = 0; i < eventCount && offset + 20 <= totalSize; i++)
                {
                    IntPtr evPtr = bufferPtr + offset;
                    int evSize = (ushort)Marshal.ReadInt16(evPtr, 4);
                    if (evSize < 20)
                        break;

                    int deviceId = (ushort)Marshal.ReadInt16(evPtr, 6);
                    bool isKeyboardEvent = kbId > 0 && deviceId == kbId;
                    bool isMouseEvent = mouseId > 0 ? deviceId == mouseId : !isKeyboardEvent;

                    if ((blockMouse && isMouseEvent) || (blockKeyboard && isKeyboardEvent))
                    {
                        // Zero out InputEvent.type FourCC so Unity's InputManager.OnUpdate skips this event
                        Marshal.WriteInt32(evPtr, 0, 0);
                    }

                    offset += (evSize + 3) & ~3;
                }
            }
            catch
            {
                // Never throw inside native input update callback
            }

            return true;
        }

        public static void EnsureInitialized(bool force = false)
        {
            if (_initialized || (_hasSearched && !force)) return;
            _hasSearched = true;

            try
            {
                _inputSystemType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem")
                                   ?? Type.GetType("UnityEngine.InputSystem.InputSystem, Il2CppUnity.InputSystem")
                                   ?? ResolveInteropAssembly("Unity.InputSystem", "Il2CppUnity.InputSystem")
                                       ?.GetType("UnityEngine.InputSystem.InputSystem");

                if (_inputSystemType != null)
                {
                    var inputAss = _inputSystemType.Assembly;
                    var deviceType = inputAss.GetType("UnityEngine.InputSystem.InputDevice");

                    _devicesProp = _inputSystemType.GetProperty("devices", BindingFlags.Public | BindingFlags.Static);
                    if (_devicesProp != null)
                    {
                        var readOnlyArrayType = _devicesProp.PropertyType;
                        _devicesCountProp = readOnlyArrayType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                        _devicesIndexerGetter = readOnlyArrayType.GetMethod("get_Item", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                        _devicesArrayFieldProp = readOnlyArrayType.GetProperty("m_Array", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _devicesLengthFieldProp = readOnlyArrayType.GetProperty("m_Length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    _deviceEnabledProp = deviceType?.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
                    _deviceIdProp = deviceType?.GetProperty("deviceId", BindingFlags.Public | BindingFlags.Instance);

                    if (deviceType != null)
                    {
                        _disableDeviceMethod = _inputSystemType.GetMethod("DisableDevice", new[] { deviceType, typeof(bool) })
                                               ?? _inputSystemType.GetMethod("DisableDevice", new[] { deviceType });
                        _disableMethodParamCount = _disableDeviceMethod?.GetParameters().Length ?? 0;

                        _enableDeviceMethod = _inputSystemType.GetMethod("EnableDevice", new[] { deviceType });

                        _resetDeviceMethod = _inputSystemType.GetMethod("ResetDevice", new[] { deviceType, typeof(bool) })
                                             ?? _inputSystemType.GetMethod("ResetDevice", new[] { deviceType });
                        _resetMethodParamCount = _resetDeviceMethod?.GetParameters().Length ?? 0;
                    }

                    var mouseType = inputAss.GetType("UnityEngine.InputSystem.Mouse");
                    _mouseCurrentProp = mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                        ?? mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                    var pointerType = inputAss.GetType("UnityEngine.InputSystem.Pointer");
                    _pointerCurrentProp = pointerType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

                    var keyboardType = inputAss.GetType("UnityEngine.InputSystem.Keyboard");
                    _keyboardCurrentProp = keyboardType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                           ?? keyboardType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                    _initialized = true;
                    Log.Info($"[NewInputSystemBlocker] Initialized Unity.InputSystem hooks (DisableDevice: {_disableDeviceMethod != null}, ResetDevice: {_resetDeviceMethod != null}).");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[NewInputSystemBlocker] Init exception: {ex}");
            }
        }

        private static IntPtr GetDeviceKey(object device)
        {
            if (device is Il2CppObjectBase il2cppObj)
                return il2cppObj.Pointer;
            return (IntPtr)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(device);
        }

        private static int TryGetDeviceId(object device)
        {
            if (device == null || _deviceIdProp == null) return -1;
            try
            {
                return (int)_deviceIdProp.GetValue(device, null);
            }
            catch
            {
                return -1;
            }
        }

        private static void TrackAndDisableDevice(object device)
        {
            if (device == null) return;
            IntPtr key = GetDeviceKey(device);
            if (key == IntPtr.Zero) return;

            if (!_disabledDevices.ContainsKey(key) || IsDeviceEnabled(device))
            {
                ResetDeviceInternal(device);
                DisableDeviceInternal(device);
                _disabledDevices[key] = device;
                _hasDisabledDevices = true;
            }
        }

        private static void EnableAndUntrackDevice(object device)
        {
            if (device == null) return;
            IntPtr key = GetDeviceKey(device);
            if (key == IntPtr.Zero) return;

            if (_disabledDevices.Remove(key) || !IsDeviceEnabled(device))
            {
                EnableDeviceInternal(device);
            }
            _hasDisabledDevices = _disabledDevices.Count > 0;
        }

        private static void ResetDeviceInternal(object device)
        {
            if (device == null || _resetDeviceMethod == null) return;
            try
            {
                if (_resetMethodParamCount == 2)
                    _resetDeviceMethod.Invoke(null, new object[] { device, false });
                else
                    _resetDeviceMethod.Invoke(null, new object[] { device });
            }
            catch
            {
                // Ignore if stripped in IL2CPP
            }
        }

        private static void DisableDeviceInternal(object device)
        {
            if (device == null || _disableDeviceMethod == null) return;
            try
            {
                if (_disableMethodParamCount == 2)
                    _disableDeviceMethod.Invoke(null, new object[] { device, false });
                else
                    _disableDeviceMethod.Invoke(null, new object[] { device });
            }
            catch (Exception ex)
            {
                Log.Warning($"[NewInputSystemBlocker] DisableDeviceInternal error: {ex.Message}");
            }
        }

        private static void EnableDeviceInternal(object device)
        {
            if (device == null || _enableDeviceMethod == null) return;
            try
            {
                _enableDeviceMethod.Invoke(null, new object[] { device });
            }
            catch (Exception ex)
            {
                Log.Warning($"[NewInputSystemBlocker] EnableDeviceInternal error: {ex.Message}");
            }
        }

        private static bool IsDeviceEnabled(object device)
        {
            if (device == null) return false;
            if (_deviceEnabledProp != null)
            {
                try
                {
                    return (bool)_deviceEnabledProp.GetValue(device, null);
                }
                catch
                {
                    return true;
                }
            }
            return true;
        }

        public static void EnforceBlocked()
        {
            EnforceBlocked(UnityInputBlocker.ShouldBlockMouse(), UnityInputBlocker.ShouldBlockKeyboard());
        }

        public static void EnforceBlocked(bool blockMouse, bool blockKeyboard)
        {
            if (!_initialized)
            {
                EnsureInitialized();
            }
            if (_inputSystemType == null) return;
            if (!DearImGuiInjectionPlus.IsCursorVisible || (!blockMouse && !blockKeyboard))
            {
                RestoreDevicesIfNeeded();
                return;
            }

            lock (_lock)
            {
                try
                {
                    var mouse = _mouseCurrentProp?.GetValue(null, null) ?? _pointerCurrentProp?.GetValue(null, null);
                    if (mouse != null)
                    {
                        int mId = TryGetDeviceId(mouse);
                        if (mId > 0) _mouseDeviceId = mId;

                        if (blockMouse)
                            TrackAndDisableDevice(mouse);
                        else
                            EnableAndUntrackDevice(mouse);
                    }

                    var keyboard = _keyboardCurrentProp?.GetValue(null, null);
                    if (keyboard != null)
                    {
                        int kId = TryGetDeviceId(keyboard);
                        if (kId > 0) _keyboardDeviceId = kId;

                        if (blockKeyboard)
                            TrackAndDisableDevice(keyboard);
                        else
                            EnableAndUntrackDevice(keyboard);
                    }

                    if (blockMouse && blockKeyboard && DearImGuiInjectionPlus.BlockAllInputsWhenOpen && _devicesProp != null)
                    {
                        var devicesObj = _devicesProp.GetValue(null, null);
                        if (devicesObj != null)
                        {
                            if (_devicesCountProp != null && _devicesIndexerGetter != null)
                            {
                                int count = (int)_devicesCountProp.GetValue(devicesObj, null);
                                for (int i = 0; i < count; i++)
                                {
                                    var device = _devicesIndexerGetter.Invoke(devicesObj, new object[] { i });
                                    if (device != null)
                                    {
                                        TrackAndDisableDevice(device);
                                    }
                                }
                            }
                            else if (_devicesArrayFieldProp != null && _devicesLengthFieldProp != null)
                            {
                                var arr = _devicesArrayFieldProp.GetValue(devicesObj, null) as System.Collections.IList;
                                int len = (int)_devicesLengthFieldProp.GetValue(devicesObj, null);
                                if (arr != null)
                                {
                                    int max = Math.Min(len, arr.Count);
                                    for (int i = 0; i < max; i++)
                                    {
                                        var device = arr[i];
                                        if (device != null)
                                        {
                                            TrackAndDisableDevice(device);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[NewInputSystemBlocker] EnforceBlocked error: {ex.Message}");
                }
            }
        }

        public static void RestoreDevices()
        {
            if (!_initialized || _inputSystemType == null) return;

            lock (_lock)
            {
                try
                {
                    foreach (var kvp in _disabledDevices)
                    {
                        if (kvp.Value != null)
                        {
                            EnableDeviceInternal(kvp.Value);
                        }
                    }
                    _disabledDevices.Clear();
                    _hasDisabledDevices = false;

                    var mouse = _mouseCurrentProp?.GetValue(null, null) ?? _pointerCurrentProp?.GetValue(null, null);
                    if (mouse != null && !IsDeviceEnabled(mouse))
                    {
                        EnableDeviceInternal(mouse);
                    }

                    var keyboard = _keyboardCurrentProp?.GetValue(null, null);
                    if (keyboard != null && !IsDeviceEnabled(keyboard))
                    {
                        EnableDeviceInternal(keyboard);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[NewInputSystemBlocker] RestoreDevices error: {ex.Message}");
                }
            }
        }

        public static void RestoreDevicesIfNeeded()
        {
            if (_hasDisabledDevices)
            {
                RestoreDevices();
            }
        }

        public static void SetBlocked(bool blocked)
        {
            EnsureInitialized(force: true);
            if (_inputSystemType == null) return;

            if (blocked)
            {
                EnforceBlocked(UnityInputBlocker.ShouldBlockMouse(), UnityInputBlocker.ShouldBlockKeyboard());
            }
            else
            {
                RestoreDevices();
            }
        }
    }
}
