// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SharpEmu.Libs.VideoOut;

// In-application RenderDoc capture control. When renderdoc.dll is loaded into
// this process before the Vulkan instance is created, RenderDoc inserts its
// capture layer automatically; we then drive it through the in-app API
// (renderdoc_app.h) to name the capture file and trigger a frame capture on
// demand. This is Windows-only and entirely opt-in via SHARPEMU_RENDERDOC.
public static partial class RenderDocCapture
{
    // RENDERDOC_API_1_4_0 is an append-only struct of function pointers. We only
    // read the two entries we need by their fixed index (8 bytes each on x64).
    private const int IndexSetCaptureFilePathTemplate = 11;
    private const int IndexTriggerCapture = 15;
    private const int ApiVersion14 = 10400;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetApiDelegate(int version, out nint outApiPointers);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetCaptureFilePathTemplateDelegate(nint pathTemplateUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TriggerCaptureDelegate();

    private static readonly object Sync = new();
    private static TriggerCaptureDelegate? _triggerCapture;
    private static bool _triggered;

    public static bool IsActive { get; private set; }

    // Load renderdoc.dll (activating its Vulkan capture layer) and wire up the
    // in-app API. Call this BEFORE any Vulkan instance is created. Safe to call
    // more than once; only the first attempt does work.
    public static void Initialize(string dllPath, string captureFileTemplate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Sync)
        {
            if (IsActive)
            {
                return;
            }

            try
            {
                InitializeWindows(dllPath, captureFileTemplate);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RENDERDOC] init failed: {ex.Message}");
            }
        }
    }

    // Ask RenderDoc to capture the next presented frame. Fires at most once.
    public static void TriggerNextFrame()
    {
        if (!IsActive)
        {
            return;
        }

        lock (Sync)
        {
            if (_triggered || _triggerCapture is null)
            {
                return;
            }

            _triggered = true;
            try
            {
                _triggerCapture();
                Console.Error.WriteLine("[RENDERDOC] TriggerCapture requested (next frame)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RENDERDOC] trigger failed: {ex.Message}");
            }
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string moduleName);

    [SupportedOSPlatform("windows")]
    private static void InitializeWindows(string dllPath, string captureFileTemplate)
    {
        // When launched under renderdoccmd (the reliable way to capture Vulkan),
        // renderdoc.dll is already injected and has installed its capture layer.
        // Grab THAT module rather than LoadLibrary'ing our own copy, which would
        // pre-empt the layer and leave Vulkan un-hooked.
        var module = GetModuleHandleW("renderdoc.dll");
        if (module == 0)
        {
            module = string.IsNullOrWhiteSpace(dllPath)
                ? NativeLibrary.Load("renderdoc.dll")
                : NativeLibrary.Load(dllPath);
        }
        else
        {
            Console.Error.WriteLine("[RENDERDOC] using injected renderdoc.dll");
        }

        var getApiPtr = NativeLibrary.GetExport(module, "RENDERDOC_GetAPI");
        var getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(getApiPtr);
        if (getApi(ApiVersion14, out var api) != 1 || api == 0)
        {
            Console.Error.WriteLine("[RENDERDOC] RENDERDOC_GetAPI returned failure");
            return;
        }

        var setTemplatePtr = Marshal.ReadIntPtr(api, IndexSetCaptureFilePathTemplate * nint.Size);
        var triggerPtr = Marshal.ReadIntPtr(api, IndexTriggerCapture * nint.Size);
        if (setTemplatePtr == 0 || triggerPtr == 0)
        {
            Console.Error.WriteLine("[RENDERDOC] API table missing expected entries");
            return;
        }

        if (!string.IsNullOrWhiteSpace(captureFileTemplate))
        {
            var setTemplate = Marshal.GetDelegateForFunctionPointer<SetCaptureFilePathTemplateDelegate>(setTemplatePtr);
            var bytes = Encoding.UTF8.GetBytes(captureFileTemplate + "\0");
            var buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                setTemplate(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        _triggerCapture = Marshal.GetDelegateForFunctionPointer<TriggerCaptureDelegate>(triggerPtr);
        IsActive = true;
        Console.Error.WriteLine($"[RENDERDOC] active; capture template='{captureFileTemplate}'");
    }
}
