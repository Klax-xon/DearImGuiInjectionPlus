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
using UnityEngineInternal.Input;

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
        private static volatile int _pointerDeviceId = -1;
        private static volatile int _keyboardDeviceId = -1;
        private static readonly HashSet<int> _mouseDeviceIds = new();
        private static readonly HashSet<int> _keyboardDeviceIds = new();
        private static bool _initialized;
        private static bool _hasLoggedInitialBlockDevices;
        private static readonly List<string> _discoveredDevices = new();

        private static int _notifyUpdateInterceptCount = 0;
        private static int _onUpdateInterceptCount = 0;

        public static bool IsNotifyUpdateHooked { get; internal set; }
        public static bool IsOnUpdateHooked { get; internal set; }
        public static int NotifyUpdateInterceptCount => _notifyUpdateInterceptCount;
        public static int OnUpdateInterceptCount => _onUpdateInterceptCount;
        public static int MouseDeviceIdsCount => _mouseDeviceIds.Count;
        public static IReadOnlyCollection<int> MouseDeviceIds => _mouseDeviceIds;
        public static int DisabledDevicesCount => _disabledDevices.Count;
        public static IReadOnlyList<string> DiscoveredDevices => _discoveredDevices;

        // Keyed by native IL2CPP pointer (or RuntimeHelpers.GetHashCode fallback) to avoid Il2CppObjectBase wrapper identity churn
        private static readonly Dictionary<IntPtr, object> _disabledDevices = new();
        private static volatile bool _hasDisabledDevices;
        private static readonly object _lock = new();

        public static void ApplyNativeInputHook(Harmony harmony, ref int applied, ref int skipped)
        {
            try
            {
                RuntimeHelpers.RunClassConstructor(typeof(NativeInputSystem).TypeHandle);

                var notifyUpdateMethod = typeof(NativeInputSystem).GetMethod(
                    nameof(NativeInputSystem.NotifyUpdate),
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (notifyUpdateMethod != null)
                {
                    var ptrField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(notifyUpdateMethod);
                    if (ptrField == null || (IntPtr)ptrField.GetValue(null) != IntPtr.Zero)
                    {
                        var prefixInfo = typeof(NewInputSystemBlocker).GetMethod(
                            nameof(NotifyUpdatePrefix),
                            BindingFlags.NonPublic | BindingFlags.Static);

                        harmony.Patch(notifyUpdateMethod, prefix: new HarmonyMethod(prefixInfo));
                        applied++;
                        IsNotifyUpdateHooked = true;
                        Log.Info("[NewInputSystemBlocker] Hooked UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate.");
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    skipped++;
                }

                var notifyDeviceMethod = typeof(NativeInputSystem).GetMethod(
                    nameof(NativeInputSystem.NotifyDeviceDiscovered),
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (notifyDeviceMethod != null)
                {
                    var devPrefix = typeof(NewInputSystemBlocker).GetMethod(
                        nameof(NotifyDeviceDiscoveredPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);

                    harmony.Patch(notifyDeviceMethod, prefix: new HarmonyMethod(devPrefix));
                    applied++;
                    Log.Info("[NewInputSystemBlocker] Hooked UnityEngineInternal.Input.NativeInputSystem.NotifyDeviceDiscovered.");
                }
                else
                {
                    skipped++;
                }

                var setOnUpdateMethod = typeof(NativeInputSystem).GetMethod(
                    "set_onUpdate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (setOnUpdateMethod != null)
                {
                    var setPrefix = typeof(NewInputSystemBlocker).GetMethod(
                        nameof(SetOnUpdatePrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);

                    harmony.Patch(setOnUpdateMethod, prefix: new HarmonyMethod(setPrefix));
                    applied++;
                    IsOnUpdateHooked = true;
                    Log.Info("[NewInputSystemBlocker] Hooked UnityEngineInternal.Input.NativeInputSystem.set_onUpdate.");
                }
                else
                {
                    skipped++;
                }

                // Probe whether onUpdate delegate is currently active
                try
                {
                    var current = NativeInputSystem.onUpdate;
                    if (current != null)
                    {
                        IsOnUpdateHooked = true;
                        Log.Info($"[NewInputSystemBlocker] Active NativeInputSystem.onUpdate callback found: {current.Pointer:X}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"[NewInputSystemBlocker] Could not inspect current onUpdate: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                skipped++;
                Log.Warning($"[NewInputSystemBlocker] Error hooking NativeInputSystem: {ex.Message}");
            }
        }

        private static void SetOnUpdatePrefix(NativeUpdateCallback value)
        {
            try
            {
                if (value != null)
                {
                    Log.Info($"[NewInputSystemBlocker] NativeInputSystem.set_onUpdate registered delegate: {value.Pointer:X}");
                    IsOnUpdateHooked = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[NewInputSystemBlocker] Error in SetOnUpdatePrefix: {ex.Message}");
            }
        }

        // Prefix for UnityEngineInternal.Input.NativeInputSystem.NotifyUpdate(NativeInputUpdateType updateType, IntPtr eventBuffer)
        private static bool NotifyUpdatePrefix(NativeInputUpdateType updateType, IntPtr eventBuffer)
        {
            int count = System.Threading.Interlocked.Increment(ref _notifyUpdateInterceptCount);
            if (count == 1)
            {
                Log.Info($"[NewInputSystemBlocker] First NotifyUpdate intercepted! updateType={updateType}, eventBuffer=0x{eventBuffer.ToInt64():X}");
            }

            if (eventBuffer == IntPtr.Zero)
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

            if (blockMouse && blockKeyboard)
            {
                Marshal.WriteInt32(eventBuffer, 8, 0);  // eventCount = 0
                Marshal.WriteInt32(eventBuffer, 12, 0); // sizeInBytes = 0
                return false; // Skip original NotifyUpdate entirely!
            }

            unsafe
            {
                FilterEventBuffer((NativeInputEventBuffer*)eventBuffer, blockMouse, blockKeyboard);
            }

            if (Marshal.ReadInt32(eventBuffer, 8) <= 0)
            {
                return false; // All events swallowed, skip original!
            }

            return true;
        }

        private static void NotifyDeviceDiscoveredPrefix(int deviceId, string deviceDescriptor)
        {
            try
            {
                Log.Info($"[NewInputSystemBlocker] Native device discovered: ID={deviceId}, Desc={deviceDescriptor}");
                if (string.IsNullOrEmpty(deviceDescriptor)) return;
                string lower = deviceDescriptor.ToLowerInvariant();
                if (lower.Contains("mouse") || lower.Contains("pointer") || lower.Contains("cursor"))
                {
                    _mouseDeviceIds.Add(deviceId);
                    _mouseDeviceId = deviceId;
                    _pointerDeviceId = deviceId;
                    Log.Info($"[NewInputSystemBlocker] Registered native pointer/mouse ID: {deviceId}");
                }
                else if (lower.Contains("keyboard") || lower.Contains("key"))
                {
                    _keyboardDeviceIds.Add(deviceId);
                    _keyboardDeviceId = deviceId;
                    Log.Info($"[NewInputSystemBlocker] Registered native keyboard ID: {deviceId}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[NewInputSystemBlocker] Error in NotifyDeviceDiscoveredPrefix: {ex.Message}");
            }
        }

        private static unsafe void FilterEventBuffer(NativeInputEventBuffer* buffer, bool blockMouse, bool blockKeyboard)
        {
            if (buffer == null || buffer->eventBuffer == IntPtr.Zero || buffer->eventCount <= 0 || buffer->sizeInBytes < 20)
                return;

            byte* raw = (byte*)buffer->eventBuffer.ToPointer();
            int totalSize = buffer->sizeInBytes;
            int eventCount = buffer->eventCount;

            int readOffset = 0;
            int writeOffset = 0;
            int keptCount = 0;

            for (int i = 0; i < eventCount && readOffset + 20 <= totalSize; i++)
            {
                byte* evPtr = raw + readOffset;
                ushort evSize = *(ushort*)(evPtr + 4);
                if (evSize < 20 || readOffset + evSize > totalSize)
                    break;

                ushort deviceId = *(ushort*)(evPtr + 6);

                bool isMouse = _mouseDeviceIds.Contains(deviceId) || deviceId == _mouseDeviceId || deviceId == _pointerDeviceId;
                bool isKeyboard = _keyboardDeviceIds.Contains(deviceId) || deviceId == _keyboardDeviceId;

                // Auto-discover device type from StateFormat FourCC at offset 20 if event is a State event
                if (!isMouse && !isKeyboard && evSize >= 24)
                {
                    int stateFormat = *(int*)(evPtr + 20);
                    // 'MOUS' = 0x4D4F5553, 'POIN' = 0x504F494E, 'TOUC' = 0x544F5543
                    if (stateFormat is 0x4D4F5553 or 0x504F494E or 0x544F5543)
                    {
                        isMouse = true;
                        _mouseDeviceIds.Add(deviceId);
                    }
                    // 'KEYB' = 0x4B455942
                    else if (stateFormat == 0x4B455942)
                    {
                        isKeyboard = true;
                        _keyboardDeviceIds.Add(deviceId);
                    }
                }

                bool shouldDrop = false;
                if (blockMouse && isMouse)
                {
                    shouldDrop = true;
                }
                else if (blockKeyboard && isKeyboard)
                {
                    shouldDrop = true;
                }

                int alignedSize = (evSize + 3) & ~3;

                if (!shouldDrop)
                {
                    if (writeOffset != readOffset)
                    {
                        Buffer.MemoryCopy(evPtr, raw + writeOffset, totalSize - writeOffset, evSize);
                    }
                    writeOffset += alignedSize;
                    keptCount++;
                }

                readOffset += alignedSize;
            }

            buffer->eventCount = keptCount;
            buffer->sizeInBytes = writeOffset;
        }

        public static void EnsureInitialized(bool force = false)
        {
            if (_initialized) return;

            try
            {
                if (_inputSystemType == null)
                {
                    _inputSystemType = ResolveInputSystemAssembly()?.GetType("UnityEngine.InputSystem.InputSystem");
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

        private static Assembly ResolveInputSystemAssembly()
        {
            // 1. Check already loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.GetType("UnityEngine.InputSystem.InputSystem") != null)
                    {
                        Log.Info($"[NewInputSystemBlocker] Found InputSystem in loaded assembly: {asm.FullName}");
                        return asm;
                    }
                }
                catch { }
            }

            // 2. Probe BepInEx search directories
            var searchDirs = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(global::BepInEx.Paths.BepInExRootPath))
                {
                    searchDirs.Add(Path.Combine(global::BepInEx.Paths.BepInExRootPath, "interop"));
                }
                if (!string.IsNullOrEmpty(global::BepInEx.Paths.CachePath))
                {
                    searchDirs.Add(Path.Combine(global::BepInEx.Paths.CachePath, "interop"));
                }
                if (global::BepInEx.Paths.DllSearchPaths != null)
                {
                    searchDirs.AddRange(global::BepInEx.Paths.DllSearchPaths);
                }
            }
            catch { }

            foreach (var dir in searchDirs.Distinct())
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(dir, "*.dll"))
                    {
                        string fname = Path.GetFileName(file);
                        if (fname.Contains("InputSystem", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var loaded = Assembly.LoadFrom(file);
                                if (loaded.GetType("UnityEngine.InputSystem.InputSystem") != null)
                                {
                                    Log.Info($"[NewInputSystemBlocker] Loaded InputSystem assembly from: {file}");
                                    return loaded;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"[NewInputSystemBlocker] Failed loading candidate {file}: {ex.Message}");
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
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

        private static string GetDeviceDescription(object device)
        {
            if (device == null) return "null";
            int id = TryGetDeviceId(device);
            string name = device.GetType().Name;
            return $"{name} (ID: {id})";
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
                Log.Info($"[NewInputSystemBlocker] Disabled device: {GetDeviceDescription(device)}");
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

        private static List<object> GetAllDevices()
        {
            var list = new List<object>();
            if (_devicesProp != null)
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
                            if (device != null) list.Add(device);
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
                                if (device != null) list.Add(device);
                            }
                        }
                    }
                }
            }

            var mouse = _mouseCurrentProp?.GetValue(null, null);
            if (mouse != null && !list.Contains(mouse)) list.Add(mouse);
            var pointer = _pointerCurrentProp?.GetValue(null, null);
            if (pointer != null && !list.Contains(pointer)) list.Add(pointer);
            var keyboard = _keyboardCurrentProp?.GetValue(null, null);
            if (keyboard != null && !list.Contains(keyboard)) list.Add(keyboard);

            // Register device IDs
            foreach (var d in list)
            {
                int id = TryGetDeviceId(d);
                if (id > 0)
                {
                    string name = d.GetType().Name.ToLowerInvariant();
                    if (name.Contains("mouse") || name.Contains("pointer"))
                    {
                        _mouseDeviceIds.Add(id);
                    }
                    else if (name.Contains("keyboard"))
                    {
                        _keyboardDeviceIds.Add(id);
                    }
                }
            }

            return list;
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
                    if (!_hasLoggedInitialBlockDevices)
                    {
                        _hasLoggedInitialBlockDevices = true;
                        var allDevs = GetAllDevices();
                        _discoveredDevices.Clear();
                        for (int i = 0; i < allDevs.Count; i++)
                        {
                            _discoveredDevices.Add(GetDeviceDescription(allDevs[i]));
                        }
                        Log.Info($"[NewInputSystemBlocker] Discovered {_discoveredDevices.Count} input devices on block toggle: {string.Join(", ", _discoveredDevices)}");
                    }

                    var mouse = _mouseCurrentProp?.GetValue(null, null);
                    if (mouse != null)
                    {
                        int mId = TryGetDeviceId(mouse);
                        if (mId > 0)
                        {
                            _mouseDeviceId = mId;
                            _mouseDeviceIds.Add(mId);
                        }
                    }

                    var pointer = _pointerCurrentProp?.GetValue(null, null);
                    if (pointer != null)
                    {
                        int pId = TryGetDeviceId(pointer);
                        if (pId > 0)
                        {
                            _pointerDeviceId = pId;
                            _mouseDeviceIds.Add(pId);
                        }
                    }

                    var keyboard = _keyboardCurrentProp?.GetValue(null, null);
                    if (keyboard != null)
                    {
                        int kId = TryGetDeviceId(keyboard);
                        if (kId > 0)
                        {
                            _keyboardDeviceId = kId;
                            _keyboardDeviceIds.Add(kId);
                        }
                    }

                    if (blockMouse)
                    {
                        if (mouse != null) TrackAndDisableDevice(mouse);
                        if (pointer != null && pointer != mouse) TrackAndDisableDevice(pointer);

                        var allDevices = GetAllDevices();
                        for (int i = 0; i < allDevices.Count; i++)
                        {
                            var dev = allDevices[i];
                            if (dev == null) continue;
                            string name = dev.GetType().Name.ToLowerInvariant();
                            if (name.Contains("mouse") || name.Contains("pointer") || name.Contains("touch"))
                            {
                                TrackAndDisableDevice(dev);
                            }
                        }
                    }
                    else
                    {
                        if (mouse != null) EnableAndUntrackDevice(mouse);
                        if (pointer != null) EnableAndUntrackDevice(pointer);
                    }

                    if (blockKeyboard)
                    {
                        if (keyboard != null) TrackAndDisableDevice(keyboard);

                        var allDevices = GetAllDevices();
                        for (int i = 0; i < allDevices.Count; i++)
                        {
                            var dev = allDevices[i];
                            if (dev == null) continue;
                            string name = dev.GetType().Name.ToLowerInvariant();
                            if (name.Contains("keyboard") || name.Contains("key"))
                            {
                                TrackAndDisableDevice(dev);
                            }
                        }
                    }
                    else
                    {
                        if (keyboard != null) EnableAndUntrackDevice(keyboard);
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
                    _hasLoggedInitialBlockDevices = false;

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

                    var pointer = _pointerCurrentProp?.GetValue(null, null);
                    if (pointer != null && !IsDeviceEnabled(pointer))
                    {
                        EnableDeviceInternal(pointer);
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
