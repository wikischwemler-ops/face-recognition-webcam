using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using FaceTrackingApp.Models;
using Windows.Devices.Enumeration;

namespace FaceTrackingApp.Services
{
    /// <summary>
    /// Liest die aktuell im Geräte-Manager aktivierten Kameras.
    /// <list type="bullet">
    /// <item>DirectShow liefert Namen, Gerätepfad und exakt den Index, den OpenCV (CAP_DSHOW) verwendet.</item>
    /// <item>Windows.Devices.Enumeration liefert Einbauort (EnclosureLocation) und Container-ID → intern/extern.</item>
    /// </list>
    /// </summary>
    public static class CameraEnumerator
    {
        // Gerätecontainer "dieser Computer": alle fest eingebauten Geräte gehören dazu.
        private static readonly Guid LocalMachineContainerId = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");
        private const string ContainerIdProperty = "System.Devices.ContainerId";

        public static async Task<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken ct = default)
        {
            // DirectShow-Enumeration (COM) im Threadpool, damit die UI nie blockiert.
            var dshowTask = Task.Run(EnumerateDirectShow, ct);

            Dictionary<string, DeviceInformation> winrtById = new(StringComparer.OrdinalIgnoreCase);
            List<DeviceInformation> winrtAll = new();
            try
            {
                var infos = await DeviceInformation.FindAllAsync(
                    DeviceInformation.GetAqsFilterFromDeviceClass(DeviceClass.VideoCapture),
                    new[] { ContainerIdProperty });
                foreach (var info in infos)
                {
                    winrtAll.Add(info);
                    winrtById[NormalizeDeviceId(info.Id)] = info;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinRT-Kameraabfrage fehlgeschlagen: {ex.Message}");
            }

            var dshow = await dshowTask.ConfigureAwait(false);
            var result = new List<CameraInfo>(dshow.Count);

            foreach (var dev in dshow)
            {
                winrtById.TryGetValue(NormalizeDeviceId(dev.DevicePath), out var info);
                // Fallback für Kameras, deren DirectShow-Pfad anders aussieht (z. B. Sensorgruppen)
                info ??= winrtAll.FirstOrDefault(w => string.Equals(w.Name, dev.Name, StringComparison.OrdinalIgnoreCase));

                if (info is not null && !info.IsEnabled)
                    continue; // im Geräte-Manager deaktiviert

                result.Add(new CameraInfo(dev.Index, dev.Name, dev.DevicePath, Classify(dev, info)));
            }

            return result;
        }

        /// <summary>Überwacht An-/Abstecken bzw. Aktivieren/Deaktivieren von Kameras.</summary>
        public static DeviceWatcher CreateWatcher() => DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);

        private static CameraKind Classify(DirectShowDevice dev, DeviceInformation? info)
        {
            if (info is not null)
            {
                var panel = info.EnclosureLocation?.Panel ?? Panel.Unknown;
                if (panel != Panel.Unknown || info.EnclosureLocation?.InLid == true || info.EnclosureLocation?.InDock == true)
                    return CameraKind.Intern;

                if (info.Properties.TryGetValue(ContainerIdProperty, out var c) && c is Guid container && container == LocalMachineContainerId)
                    return CameraKind.Intern;
            }

            var path = dev.DevicePath.ToLowerInvariant();
            if (string.IsNullOrEmpty(path) || !path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return CameraKind.Virtuell; // z. B. OBS Virtual Camera (reiner DirectShow-Filter)
            if (path.Contains(@"\\?\root#", StringComparison.Ordinal) || path.Contains("vcam", StringComparison.Ordinal))
                return CameraKind.Virtuell;
            if (path.Contains("usb#", StringComparison.Ordinal) || path.Contains("usbvideo", StringComparison.Ordinal))
                return CameraKind.Extern;

            return info is null ? CameraKind.Unbekannt : CameraKind.Extern;
        }

        /// <summary>
        /// "\\?\USB#VID_046D&amp;PID_085B&amp;MI_00#7&amp;1a2b#{guid}\GLOBAL" → "\\?\usb#vid_046d&amp;pid_085b&amp;mi_00#7&amp;1a2b".
        /// DirectShow und WinRT verwenden unterschiedliche Interface-GUIDs, der Instanzteil ist identisch.
        /// </summary>
        private static string NormalizeDeviceId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return string.Empty;
            var s = id.ToLowerInvariant();
            int cut = s.LastIndexOf("#{", StringComparison.Ordinal);
            return cut > 0 ? s[..cut] : s;
        }

        // ------------------------------------------------------------------ DirectShow (COM)

        private sealed record DirectShowDevice(int Index, string Name, string DevicePath);

        private static readonly Guid CLSID_SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
        private static readonly Guid CLSID_VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
        private static readonly Guid IID_IPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");

        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator([In] ref Guid pType, out IEnumMoniker? ppEnumMoniker, int dwFlags);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag
        {
            [PreserveSig]
            int Read([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
                     [Out, MarshalAs(UnmanagedType.Struct)] out object? pVar,
                     [In] IntPtr pErrorLog);

            [PreserveSig]
            int Write([In, MarshalAs(UnmanagedType.LPWStr)] string pszPropName,
                      [In, MarshalAs(UnmanagedType.Struct)] ref object pVar);
        }

        private static List<DirectShowDevice> EnumerateDirectShow()
        {
            var list = new List<DirectShowDevice>();
            object? devEnumObj = null;
            IEnumMoniker? enumMoniker = null;
            try
            {
                var type = Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum, throwOnError: true)!;
                devEnumObj = Activator.CreateInstance(type);
                if (devEnumObj is not ICreateDevEnum devEnum)
                    return list;

                var category = CLSID_VideoInputDeviceCategory;
                int hr = devEnum.CreateClassEnumerator(ref category, out enumMoniker, 0);
                if (hr != 0 || enumMoniker is null)
                    return list; // S_FALSE = keine Videogeräte

                var monikers = new IMoniker[1];
                int index = 0;
                while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    var moniker = monikers[0];
                    string name = $"Kamera {index}";
                    string path = string.Empty;
                    try
                    {
                        var iid = IID_IPropertyBag;
                        moniker.BindToStorage(null!, null, ref iid, out object bagObj);
                        if (bagObj is IPropertyBag bag)
                        {
                            name = ReadString(bag, "FriendlyName") ?? name;
                            path = ReadString(bag, "DevicePath") ?? string.Empty;
                            Marshal.ReleaseComObject(bag);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Kamera {index}: Eigenschaften nicht lesbar: {ex.Message}");
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(moniker);
                    }

                    // Index muss jeder DirectShow-Position entsprechen, sonst öffnet OpenCV die falsche Kamera
                    list.Add(new DirectShowDevice(index, name, path));
                    index++;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DirectShow-Enumeration fehlgeschlagen: {ex.Message}");
            }
            finally
            {
                if (enumMoniker is not null) Marshal.ReleaseComObject(enumMoniker);
                if (devEnumObj is not null) Marshal.ReleaseComObject(devEnumObj);
            }
            return list;
        }

        private static string? ReadString(IPropertyBag bag, string name)
        {
            try
            {
                return bag.Read(name, out var value, IntPtr.Zero) == 0 ? value as string : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
