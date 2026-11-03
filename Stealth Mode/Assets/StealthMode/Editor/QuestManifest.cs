using System.IO;
using UnityEditor.Android;

namespace StealthMode.EditorTools
{
    /// <summary>
    /// Quest build: passthrough + boundaryless (no guardian / floor setup prompts; the app shows the real room itself),
    /// and Bluetooth LE (the link to the Mac relay): scan + connect, no location needed.
    /// </summary>
    public class QuestManifest : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 999;

        static readonly string[] Features = { "com.oculus.feature.PASSTHROUGH", "com.oculus.feature.BOUNDARYLESS_APP" };
        static readonly string[] Permissions =
        {
            "<uses-permission android:name=\"android.permission.BLUETOOTH_SCAN\" android:usesPermissionFlags=\"neverForLocation\" />",
            "<uses-permission android:name=\"android.permission.BLUETOOTH_CONNECT\" />",
            "<uses-feature android:name=\"android.hardware.bluetooth_le\" android:required=\"false\" />",
            "<uses-permission android:name=\"android.permission.RECORD_AUDIO\" />",   // the Engineer's voice (no push-to-talk)
        };

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var file = Path.Combine(path, "src/main/AndroidManifest.xml");
            if (!File.Exists(file)) return;
            var xml = File.ReadAllText(file);
            foreach (var f in Features)
                if (!xml.Contains(f))
                    xml = xml.Replace("</manifest>", $"  <uses-feature android:name=\"{f}\" android:required=\"true\" />\n</manifest>");
            foreach (var p in Permissions)
                if (!xml.Contains(p.Split('"')[1]))
                    xml = xml.Replace("</manifest>", $"  {p}\n</manifest>");
            File.WriteAllText(file, xml);
        }
    }
}
