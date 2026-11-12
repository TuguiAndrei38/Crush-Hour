using UnityEngine;
using UnityEngine.Rendering;

namespace CrushHour
{
    /// <summary>
    /// Step 7: the HUD's information on room objects. Two slanted info screens on the middle of the desk, either side
    /// of the minimap (incident log on the operator's left, team on the right), and the digital clock on the side
    /// wall (show clock, people inside, speed). The game fills the texts (<see cref="CrushHourGame"/>, ControlRoom part).
    /// </summary>
    public partial class ControlRoom
    {
        public TextMesh LogTitle { get; private set; }
        public TextMesh LogText { get; private set; }
        public TextMesh TeamTitle { get; private set; }
        public TextMesh TeamText { get; private set; }
        public TextMesh ClockText { get; private set; }
        GameObject roomShell;

        // Info screens: on the middle desk segment between the minimap console and the wing joints, in front of the
        // monitor stands (desk-local z -0.58..-0.31), tilted back 53° and low, so the monitors behind stay visible.
        const float InfoWidth = 0.50f, InfoHeight = 0.22f;
        static readonly Vector3 InfoNormal = new Vector3(0f, 0.8f, 0.6f).normalized;
        static readonly Vector3 InfoUp = new Vector3(0f, 0.6f, -0.8f).normalized;

        void SetupInfo()
        {
            // operator's left is +X in the room
            var left = new Vector3(0.57f, 0.75f + 0.075f, DeskPos.z - 0.445f);
            var right = new Vector3(-0.57f, 0.75f + 0.075f, DeskPos.z - 0.445f);
            (LogTitle, LogText) = InfoScreen("Incident Log Screen", left, "INCIDENT LOG");
            (TeamTitle, TeamText) = InfoScreen("Team Screen", right, "TEAM");
            SetupClock();
        }

        (TextMesh title, TextMesh body) InfoScreen(string name, Vector3 localCenter, string heading)
        {
            var root = new GameObject(name).transform;
            root.SetParent(Root, false);
            root.localPosition = localCenter;
            // TextMesh and Quad show their front to -forward: forward points into the screen.
            root.localRotation = Quaternion.LookRotation(-InfoNormal, InfoUp);

            // the bezel (its back reaches down to the desk) and the glass
            Prim(root, PrimitiveType.Cube, RoomMaterial("M_Monitor_Plastic"), new Vector3(0f, 0f, 0.03f), new Vector3(InfoWidth + 0.03f, InfoHeight + 0.03f, 0.06f), Quaternion.identity);
            var glass = new Material(RoomMaterial("M_Board_CrewScreen")) { name = name + " glass" };
            glass.SetColor("_BaseColor", new Color(0.02f, 0.03f, 0.04f));
            glass.EnableKeyword("_EMISSION");
            glass.SetColor("_EmissionColor", new Color(0.015f, 0.03f, 0.05f));
            ownedBoardMaterials.Add(glass);
            Prim(root, PrimitiveType.Quad, glass, Vector3.zero, new Vector3(InfoWidth, InfoHeight, 1f), Quaternion.identity);

            var title = InfoText(root, name + " title", new Vector3(-InfoWidth * 0.47f, InfoHeight * 0.45f, -0.002f), 0.016f, TextAnchor.UpperLeft);
            title.text = $"<b>{heading}</b>";
            title.color = new Color(1f, 0.78f, 0.2f);
            var body = InfoText(root, name + " text", new Vector3(-InfoWidth * 0.47f, InfoHeight * 0.28f, -0.002f), 0.0105f, TextAnchor.UpperLeft);
            body.color = new Color(0.85f, 0.9f, 0.95f);
            return (title, body);
        }

        TextMesh InfoText(Transform parent, string name, Vector3 localPos, float height, TextAnchor anchor)
        {
            if (boardFont == null) boardFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var text = new GameObject(name).AddComponent<TextMesh>();
            text.transform.SetParent(parent, false);
            text.transform.localPosition = localPos;
            text.font = boardFont;
            text.fontSize = 64;
            text.characterSize = height / 6.4f;
            text.anchor = anchor;
            text.alignment = TextAlignment.Left;
            text.richText = true;
            text.lineSpacing = 1.05f;
            var mr = text.GetComponent<MeshRenderer>();
            mr.sharedMaterial = boardFont.material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            text.gameObject.layer = RoomLayer;
            return text;
        }

        GameObject Prim(Transform parent, PrimitiveType type, Material material, Vector3 localPos, Vector3 scale, Quaternion localRot)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(localPos, localRot);
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            go.layer = RoomLayer;
            return go;
        }

        /// <summary>Text on the room's digital wall clock (the M_Clock_Screen quad on the side wall).</summary>
        void SetupClock()
        {
            if (roomShell == null) return;
            Renderer screen = null;
            foreach (var r in roomShell.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.name.StartsWith("M_Clock_Screen")) screen = r;
            if (screen == null) return;

            // The clock hangs on a side wall: face into the room.
            var c = screen.bounds.center;
            var toRoom = Root.position + Root.forward * 4.5f - c;
            toRoom = Vector3.Project(toRoom, Root.right).normalized;
            var holder = new GameObject("Wall Clock Text").transform;
            holder.SetParent(Root, true);
            holder.position = c + toRoom * 0.004f;
            holder.rotation = Quaternion.LookRotation(-toRoom, Root.up);
            ClockText = InfoText(holder, "Clock", Vector3.zero, 0.075f, TextAnchor.MiddleCenter);
            ClockText.alignment = TextAlignment.Center;
            ClockText.color = new Color(1f, 0.25f, 0.18f);
        }
    }
}
