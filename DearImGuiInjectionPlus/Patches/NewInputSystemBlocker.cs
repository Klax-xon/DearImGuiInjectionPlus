using System;
using System.Linq;
using System.Reflection;

namespace DearImGuiInjectionPlus.Patches
{
    internal static class NewInputSystemBlocker
    {
        private static Type _inputSystemType;
        private static MethodInfo _disableDeviceMethod;
        private static MethodInfo _enableDeviceMethod;
        private static PropertyInfo _mouseCurrentProp;
        private static PropertyInfo _keyboardCurrentProp;
        private static bool _initialized;

        public static void SetBlocked(bool blocked)
        {
            if (!_initialized)
            {
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

            if (_inputSystemType == null) return;

            try
            {
                var mouse = _mouseCurrentProp?.GetValue(null);
                var keyboard = _keyboardCurrentProp?.GetValue(null);

                if (blocked)
                {
                    if (mouse != null && _disableDeviceMethod != null)
                        _disableDeviceMethod.Invoke(null, _disableDeviceMethod.GetParameters().Length == 2 ? new object[] { mouse, false } : new object[] { mouse });
                    if (keyboard != null && _disableDeviceMethod != null)
                        _disableDeviceMethod.Invoke(null, _disableDeviceMethod.GetParameters().Length == 2 ? new object[] { keyboard, false } : new object[] { keyboard });
                }
                else
                {
                    if (mouse != null && _enableDeviceMethod != null)
                        _enableDeviceMethod.Invoke(null, new object[] { mouse });
                    if (keyboard != null && _enableDeviceMethod != null)
                        _enableDeviceMethod.Invoke(null, new object[] { keyboard });
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[NewInputSystemBlocker] SetBlocked({blocked}) error: {ex}");
            }
        }
    }
}
