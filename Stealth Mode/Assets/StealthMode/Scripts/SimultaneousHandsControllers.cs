using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace StealthMode
{
    /// <summary>
    /// Meta multimodal on plain Unity OpenXR: turns on XR_META_simultaneous_hands_and_controllers when the session begins,
    /// so the left hand stays hand-tracked while the right hand holds the rifle controller (Quest only switches between
    /// hands and controllers otherwise). Enum value from the Khronos openxr.h registry header.
    /// </summary>
#if UNITY_EDITOR
    [OpenXRFeature(UiName = "Meta: Simultaneous Hands and Controllers",
        BuildTargetGroups = new[] { BuildTargetGroup.Android },
        Company = "Stealth Mode",
        Desc = "Left hand tracked while the right hand holds a controller (XR_META_simultaneous_hands_and_controllers).",
        OpenxrExtensionStrings = Extension,
        Version = "1.0.0",
        FeatureId = Id)]
#endif
    public class SimultaneousHandsControllers : OpenXRFeature
    {
        public const string Extension = "XR_META_simultaneous_hands_and_controllers";
        public const string Id = "com.stealthmode.feature.multimodal";
        const int TypeResumeInfo = 1000532002;   // XR_TYPE_SIMULTANEOUS_HANDS_AND_CONTROLLERS_TRACKING_RESUME_INFO_META

        public static bool Active { get; private set; }

        ulong instance;

        [StructLayout(LayoutKind.Sequential)]
        struct ResumeInfo { public int type; public IntPtr next; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GetInstanceProcAddr(ulong xrInstance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int ResumeTracking(ulong xrSession, ref ResumeInfo info);

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            instance = xrInstance;
            return true;   // never block startup: without the extension the app runs as before
        }

        protected override void OnSessionBegin(ulong xrSession)
        {
            if (!OpenXRRuntime.IsExtensionEnabled(Extension)) { Debug.Log("[StealthMode] multimodal not offered by the runtime"); return; }
            try
            {
                var gpa = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddr>(xrGetInstanceProcAddr);
                if (gpa(instance, "xrResumeSimultaneousHandsAndControllersTrackingMETA", out var fn) != 0 || fn == IntPtr.Zero) return;
                var resume = Marshal.GetDelegateForFunctionPointer<ResumeTracking>(fn);
                var info = new ResumeInfo { type = TypeResumeInfo, next = IntPtr.Zero };
                int result = resume(xrSession, ref info);
                Active = result == 0;
                Debug.Log("[StealthMode] multimodal resume -> " + result);
            }
            catch (Exception e) { Debug.LogWarning("[StealthMode] multimodal failed: " + e.Message); }
        }
    }
}
