using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DearImGuiInjectionPlus.Patches
{
    internal static class NewInputSystemBlocker
    {
        private static Type _inputSystemType;
        private static MethodInfo _disableDeviceMethod;
        private static MethodInfo _enableDeviceMethod;
        private static PropertyInfo _devicesProp;
        private static PropertyInfo _deviceEnabledProp;
        private static PropertyInfo _mouseCurrentProp;
        private static PropertyInfo _keyboardCurrentProp;
        private static bool _initialized;
        private static bool _hasSearched;
        private static readonly HashSet<object> _disabledDevices = new();
        private static readonly object _lock = new();

        public static void EnsureInitialized(bool force = false)
        {
            if (_initialized || (_hasSearched && !force)) return;
            _hasSearched = true;

            try
            {
                _inputSystemType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
                if (_inputSystemType == null)
                {
                    var inputAss = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "Unity.InputSystem");
                    if (inputAss != null)
                    {
                        _inputSystemType = inputAss.GetType("UnityEngine.InputSystem.InputSystem");
                    }
                }

                if (_inputSystemType != null)
                {
                    var inputAss = _inputSystemType.Assembly;
                    var deviceType = inputAss.GetType("UnityEngine.InputSystem.InputDevice")
                                     ?? Type.GetType("UnityEngine.InputSystem.InputDevice, Unity.InputSystem");

                    _devicesProp = _inputSystemType.GetProperty("devices", BindingFlags.Public | BindingFlags.Static);
                    _deviceEnabledProp = deviceType?.GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);

                    _disableDeviceMethod = _inputSystemType.GetMethod("DisableDevice", new[] { deviceType, typeof(bool) })
                                           ?? _inputSystemType.GetMethod("DisableDevice", new[] { deviceType });
                    _enableDeviceMethod = _inputSystemType.GetMethod("EnableDevice", new[] { deviceType });

                    var mouseType = inputAss.GetType("UnityEngine.InputSystem.Mouse")
                                    ?? Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
                    _mouseCurrentProp = mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);

                    var keyboardType = inputAss.GetType("UnityEngine.InputSystem.Keyboard")
                                       ?? Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
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

        private static void DisableDeviceInternal(object device)
        {
            if (device == null || _disableDeviceMethod == null) return;
            try
            {
                if (_disableDeviceMethod.GetParameters().Length == 2)
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
                    if (_devicesProp != null)
                    {
                        var devicesObj = _devicesProp.GetValue(null, null);
                        if (devicesObj is System.Collections.IEnumerable deviceList)
                        {
                            foreach (var device in deviceList)
                            {
                                if (device == null) continue;
                                handledAny = true;
                                if (IsDeviceEnabled(device))
                                {
                                    DisableDeviceInternal(device);
                                    _disabledDevices.Add(device);
                                }
                            }
                        }
                    }

                    if (!handledAny)
                    {
                        var mouse = _mouseCurrentProp?.GetValue(null, null);
                        var keyboard = _keyboardCurrentProp?.GetValue(null, null);

                        if (mouse != null && IsDeviceEnabled(mouse))
                        {
                            DisableDeviceInternal(mouse);
                            _disabledDevices.Add(mouse);
                        }
                        if (keyboard != null && IsDeviceEnabled(keyboard))
                        {
                            DisableDeviceInternal(keyboard);
                            _disabledDevices.Add(keyboard);
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
                    foreach (var device in _disabledDevices)
                    {
                        if (device != null)
                        {
                            EnableDeviceInternal(device);
                        }
                    }
                    _disabledDevices.Clear();

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
            if (_disabledDevices.Count > 0)
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
