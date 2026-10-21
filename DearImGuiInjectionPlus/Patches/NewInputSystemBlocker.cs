using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace DearImGuiInjectionPlus.Patches
{
    internal static class NewInputSystemBlocker
    {
        private static Type _inputSystemType;
        private static MethodInfo _disableDeviceMethod;
        private static MethodInfo _enableDeviceMethod;
        private static PropertyInfo _devicesProp;
        private static PropertyInfo _devicesCountProp;
        private static MethodInfo _devicesIndexerGetter;
        private static PropertyInfo _deviceEnabledProp;
        private static PropertyInfo _mouseCurrentProp;
        private static PropertyInfo _keyboardCurrentProp;
        private static int _disableMethodParamCount;
        private static bool _initialized;
        private static bool _hasSearched;

        // Keyed by native IL2CPP pointer (or RuntimeHelpers.GetHashCode fallback) to avoid Il2CppObjectBase wrapper identity churn
        private static readonly Dictionary<IntPtr, object> _disabledDevices = new();
        private static volatile bool _hasDisabledDevices;
        private static readonly object _lock = new();

        public static void EnsureInitialized(bool force = false)
        {
            if (_initialized || (_hasSearched && !force)) return;
            _hasSearched = true;

            try
            {
                _inputSystemType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem")
                                   ?? Type.GetType("UnityEngine.InputSystem.InputSystem, Il2CppUnity.InputSystem");
                if (_inputSystemType == null)
                {
                    var inputAss = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name is "Unity.InputSystem" or "Il2CppUnity.InputSystem");
                    if (inputAss != null)
                    {
                        _inputSystemType = inputAss.GetType("UnityEngine.InputSystem.InputSystem");
                    }
                }

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
                    }

                    _deviceEnabledProp = deviceType?.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);

                    _disableDeviceMethod = _inputSystemType.GetMethod("DisableDevice", new[] { deviceType, typeof(bool) })
                                           ?? _inputSystemType.GetMethod("DisableDevice", new[] { deviceType });
                    _disableMethodParamCount = _disableDeviceMethod?.GetParameters().Length ?? 0;

                    _enableDeviceMethod = _inputSystemType.GetMethod("EnableDevice", new[] { deviceType });

                    var mouseType = inputAss.GetType("UnityEngine.InputSystem.Mouse");
                    _mouseCurrentProp = mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);

                    var keyboardType = inputAss.GetType("UnityEngine.InputSystem.Keyboard");
                    _keyboardCurrentProp = keyboardType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);

                    _initialized = true;
                    Log.Info("[NewInputSystemBlocker] Successfully discovered and initialized UnityEngine.InputSystem hooks.");
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

        private static void TrackAndDisableDevice(object device)
        {
            if (device == null) return;
            IntPtr key = GetDeviceKey(device);
            if (key == IntPtr.Zero) return;

            if (IsDeviceEnabled(device))
            {
                DisableDeviceInternal(device);
                _disabledDevices[key] = device;
                _hasDisabledDevices = true;
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
            if (!_initialized)
            {
                EnsureInitialized();
            }
            if (_inputSystemType == null) return;
            if (!DearImGuiInjectionPlus.IsCursorVisible || !DearImGuiInjectionPlus.BlockAllInputsWhenOpen) return;

            lock (_lock)
            {
                try
                {
                    bool handledAny = false;
                    if (_devicesProp != null && _devicesCountProp != null && _devicesIndexerGetter != null)
                    {
                        var devicesObj = _devicesProp.GetValue(null, null);
                        if (devicesObj != null)
                        {
                            int count = (int)_devicesCountProp.GetValue(devicesObj, null);
                            for (int i = 0; i < count; i++)
                            {
                                var device = _devicesIndexerGetter.Invoke(devicesObj, new object[] { i });
                                if (device == null) continue;
                                handledAny = true;
                                TrackAndDisableDevice(device);
                            }
                        }
                    }

                    if (!handledAny)
                    {
                        TrackAndDisableDevice(_mouseCurrentProp?.GetValue(null, null));
                        TrackAndDisableDevice(_keyboardCurrentProp?.GetValue(null, null));
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

                    var mouse = _mouseCurrentProp?.GetValue(null, null);
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
                if (!DearImGuiInjectionPlus.BlockAllInputsWhenOpen)
                    return;

                EnforceBlocked();
            }
            else
            {
                RestoreDevices();
            }
        }
    }
}
