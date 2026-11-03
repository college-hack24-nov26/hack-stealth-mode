using System.IO;
using UnityEditor.Android;

namespace StealthMode.EditorTools
{
    /// <summary>
    /// Quest build: passthrough + boundaryless (no guardian / floor setup prompts; the app shows the real room itself).
    /// </summary>
    public class QuestManifest : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 999;

        static readonly string[] Features = { "com.oculus.feature.PASSTHROUGH", "com.oculus.feature.BOUNDARYLESS_APP" };

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var file = Path.Combine(path, "src/main/AndroidManifest.xml");
            if (!File.Exists(file)) return;
            var xml = File.ReadAllText(file);
            foreach (var f in Features)
                if (!xml.Contains(f))
                    xml = xml.Replace("</manifest>", $"  <uses-feature android:name=\"{f}\" android:required=\"true\" />\n</manifest>");
            File.WriteAllText(file, xml);
        }
    }
}
