using UnityEngine;
using UnityEngine.InputSystem;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using UnityEngine.InputSystem.DualShock;
#endif

namespace ProjectSoullike
{
    // 패드 진동 한 곳. Gamepad.SetMotorSpeeds 대신 이것을 부른다.
    // Unity Input System은 DualSense에 USB 형식 출력 보고서만 보내서 블루투스 연결이면 진동이 조용히 실패한다.
    // Windows에서 블루투스 DualSense를 찾으면 블루투스 출력 보고서(0x31, 78바이트, CRC32)를 HID로 직접 써서 울린다.
    // low = 왼쪽 저주파, high = 오른쪽 고주파(0 ~ 1).
    public static class PadRumble
    {
        public static void Set(Gamepad pad, float low, float high)
        {
            if (pad == null) return;
            // 설정의 패드 진동 끄기·세기를 모든 진동(전투·입장 연출)에 한 곳에서 적용한다. 끄는 값(0)은 그대로 0이다.
            float scale = GameSettings.Vibration ? GameSettings.VibrationStrength : 0f;
            low = Mathf.Clamp01(low * scale);
            high = Mathf.Clamp01(high * scale);
            pad.SetMotorSpeeds(low, high);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (pad is DualSenseGamepadHID) DualSenseBluetooth.Set(low, high);
#endif
        }

        public static void Stop(Gamepad pad) => Set(pad, 0f, 0f);

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        // 블루투스 DualSense 출력. 쓰기는 몇 ms 막힐 수 있어 전용 스레드에서 마지막 값만 보낸다.
        private static class DualSenseBluetooth
        {
            private const ushort SonyVendor = 0x054C;
            private const ushort DualSenseProduct = 0x0CE6, DualSenseEdgeProduct = 0x0DF2;
            private const int BluetoothInputLength = 78; // USB는 64
            private const int ReportLength = 78;

            private static readonly object Gate = new object();
            private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
            private static Thread _thread;
            private static byte _low, _high;
            private static bool _dirty;
            private static bool _quit;
            private static float _nextSearch;

            public static void Set(float low, float high)
            {
                byte l = (byte)Mathf.RoundToInt(Mathf.Clamp01(low) * 255f);
                byte h = (byte)Mathf.RoundToInt(Mathf.Clamp01(high) * 255f);
                lock (Gate)
                {
                    if (_thread == null)
                    {
                        // 블루투스 패드가 없으면 매 프레임 장치를 뒤지지 않도록 잠시 쉰다.
                        if (Time.unscaledTime < _nextSearch) return;
                        _nextSearch = Time.unscaledTime + 3f;
                        _quit = false;
                        _thread = new Thread(Run) { IsBackground = true, Name = "DualSense BT Rumble" };
                        _thread.Start();
                        Application.quitting += Shutdown;
                    }
                    if (l == _low && h == _high && !_dirty) return;
                    _low = l;
                    _high = h;
                    _dirty = true;
                }
                Wake.Set();
            }

            private static void Shutdown()
            {
                lock (Gate) { _low = 0; _high = 0; _dirty = true; _quit = true; }
                Wake.Set();
            }

            private static void Run()
            {
                SafeFileHandle handle = Open(out int outputLength);
                if (handle == null)
                {
                    lock (Gate) _thread = null; // 블루투스 DualSense가 없다. USB면 Input System이 울린다.
                    return;
                }
                Debug.Log("[PadRumble] Bluetooth DualSense found, sending rumble directly.");
                var buffer = new byte[Math.Max(outputLength, ReportLength)];
                byte sequence = 0;
                try
                {
                    while (true)
                    {
                        Wake.WaitOne(500);
                        byte low, high;
                        bool quit;
                        lock (Gate)
                        {
                            if (!_dirty && !_quit) continue;
                            low = _low; high = _high; quit = _quit; _dirty = false;
                        }
                        Fill(buffer, sequence, low, high);
                        sequence = (byte)((sequence + 1) & 0x0F);
                        if (!WriteFile(handle, buffer, buffer.Length, out _, IntPtr.Zero) &&
                            !HidD_SetOutputReport(handle, buffer, ReportLength))
                        {
                            Debug.LogWarning($"[PadRumble] Bluetooth write failed ({Marshal.GetLastWin32Error()}).");
                            break; // 연결이 끊겼다. 다음 진동 때 다시 찾는다.
                        }
                        if (quit) break;
                    }
                }
                finally
                {
                    handle.Dispose();
                    lock (Gate) _thread = null;
                }
            }

            // 출력 보고서 0x31: [0]=0x31 [1]=순번<<4 [2]=0x10, [3..49]=공통 출력, [74..77]=CRC32(0xA2 + [0..73]).
            private static void Fill(byte[] b, byte sequence, byte low, byte high)
            {
                Array.Clear(b, 0, b.Length);
                b[0] = 0x31;
                b[1] = (byte)(sequence << 4);
                b[2] = 0x10;
                const int c = 3;
                b[c + 0] = 0x03;       // valid_flag0: 호환 진동 + 햅틱 선택
                b[c + 2] = high;       // 오른쪽(고주파)
                b[c + 3] = low;        // 왼쪽(저주파)
                b[c + 38] = 0x04;      // valid_flag2: 새 펌웨어 호환 진동
                uint crc = Crc32(0xFFFFFFFF, 0xA2);
                for (int i = 0; i < ReportLength - 4; i++) crc = Crc32(crc, b[i]);
                crc = ~crc;
                b[74] = (byte)crc;
                b[75] = (byte)(crc >> 8);
                b[76] = (byte)(crc >> 16);
                b[77] = (byte)(crc >> 24);
            }

            private static uint Crc32(uint crc, byte value)
            {
                crc ^= value;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                return crc;
            }

            private static SafeFileHandle Open(out int outputLength)
            {
                outputLength = 0;
                HidD_GetHidGuid(out Guid hidGuid);
                IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
                if (set == new IntPtr(-1)) return null;
                try
                {
                    var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data); i++)
                    {
                        string path = GetPath(set, ref data);
                        if (path == null) continue;
                        var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // RW, share RW, OPEN_EXISTING
                        if (handle.IsInvalid) { handle.Dispose(); continue; }
                        if (IsBluetoothDualSense(handle, out outputLength)) return handle;
                        handle.Dispose();
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
                return null;
            }

            private static bool IsBluetoothDualSense(SafeFileHandle handle, out int outputLength)
            {
                outputLength = 0;
                var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                if (!HidD_GetAttributes(handle, ref attributes) || attributes.VendorID != SonyVendor ||
                    (attributes.ProductID != DualSenseProduct && attributes.ProductID != DualSenseEdgeProduct)) return false;
                if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) return false;
                try
                {
                    if (HidP_GetCaps(preparsed, out HIDP_CAPS caps) != 0x00110000) return false; // HIDP_STATUS_SUCCESS
                    outputLength = caps.OutputReportByteLength;
                    return caps.InputReportByteLength == BluetoothInputLength;
                }
                finally { HidD_FreePreparsedData(preparsed); }
            }

            private static string GetPath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) return null;
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) return null;
                    return Marshal.PtrToStringUni(detail + 4);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

            [StructLayout(LayoutKind.Sequential)]
            private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

            [StructLayout(LayoutKind.Sequential)]
            private struct HIDP_CAPS
            {
                public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
                public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                    NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                    NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
            }

            [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
            [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
            [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
            [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
            [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
            [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] report, int length);

            [DllImport("setupapi.dll", SetLastError = true)]
            private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
            [DllImport("setupapi.dll", SetLastError = true)]
            private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
            [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
            [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int length, out int written, IntPtr overlapped);
        }
#endif
    }
}
