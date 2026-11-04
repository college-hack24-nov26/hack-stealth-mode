using UnityEngine;

namespace StealthMode
{
    /// <summary>
    /// One line of subtitles low in the Engineer's view: who is talking and what they said (radio and voices in the air),
    /// so nothing is lost in a loud room. It drifts after the head like the start prompt, sits ~20 degrees below the eye
    /// line, and draws over the world (the text shader ignores depth), so the container in front of his face can't hide it.
    /// Also the big centre text for COVER SUCCESSFUL.
    /// </summary>
    public class Caption : MonoBehaviour
    {
        static Caption I;
        Transform head;
        TextMesh text, shadow;
        float until;
        bool big;

        public static void Show(Transform head, string speaker, string line, float seconds)
        {
            var c = Get(head);
            c.big = false;
            var s = (speaker != null ? $"<color=#F2C14E>{speaker}</color>   " : "") + string.Join("\n", System.Array.ConvertAll(line.Split('\n'), l => Wrap(l, 40)));
            c.Set(s, 72, 0.0036f);
            c.until = Time.time + seconds;
        }

        public static void Big(Transform head, string line, float seconds)
        {
            var c = Get(head);
            c.big = true;
            c.Set(line, 120, 0.006f);
            c.until = Time.time + seconds;
        }

        static Caption Get(Transform head)
        {
            if (!I)
            {
                var go = new GameObject("Captions");
                I = go.AddComponent<Caption>();
                I.shadow = I.Line(new Color(0, 0, 0, 0.9f), new Vector3(0.003f, -0.003f, 0.002f));
                I.text = I.Line(new Color(0.96f, 0.97f, 0.94f), Vector3.zero);
            }
            I.head = head;
            return I;
        }

        TextMesh Line(Color c, Vector3 offset)
        {
            var go = new GameObject("text"); go.transform.SetParent(transform, false); go.transform.localPosition = offset;
            var tm = go.AddComponent<TextMesh>();
            tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = new Material(tm.font.material) { renderQueue = 4500 };   // over the fade-to-black veil too
            tm.color = c; tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center; tm.richText = true;
            return tm;
        }

        void Set(string s, int size, float charSize)
        {
            foreach (var tm in new[] { text, shadow })
            {
                tm.fontSize = size; tm.characterSize = charSize;
                tm.text = tm == shadow ? System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", "") : s;
                tm.fontStyle = FontStyle.Bold;
                tm.gameObject.SetActive(true);
            }
        }

        static string Wrap(string s, int width)
        {
            var sb = new System.Text.StringBuilder(); int col = 0;
            foreach (var w in s.Split(' '))
            {
                if (col > 0 && col + w.Length + 1 > width) { sb.Append('\n'); col = 0; }
                else if (col > 0) { sb.Append(' '); col++; }
                sb.Append(w); col += w.Length;
            }
            return sb.ToString();
        }

        void LateUpdate()
        {
            if (Time.time > until) { text.gameObject.SetActive(false); shadow.gameObject.SetActive(false); }
            if (!head) return;
            var f = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (f.sqrMagnitude < 0.01f) f = Vector3.forward;
            var down = big ? 0f : 20f;
            var dir = Quaternion.AngleAxis(down, Vector3.Cross(Vector3.up, f)) * f;
            var want = head.position + dir * (big ? 1.4f : 1.0f);
            transform.position = Vector3.Lerp(transform.position == Vector3.zero ? want : transform.position, want, Time.deltaTime * 4f);
            transform.rotation = Quaternion.LookRotation(transform.position - head.position, Vector3.up);
        }
    }
}
