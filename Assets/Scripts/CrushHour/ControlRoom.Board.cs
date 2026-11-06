using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CrushHour
{
    /// <summary>
    /// Step 5: the exits control board on the desk. One covered toggle switch + lamp + label per exit (venue.doors
    /// order; entrances get only the lamp), a crew display, 9 wall-camera buttons + AUTO, and START. The game says what
    /// each exit is doing (<see cref="UpdateBoard"/>) and gets the switch / START clicks through <see cref="onSwitch"/>
    /// and <see cref="onStart"/>.
    /// </summary>
    public partial class ControlRoom
    {
        public enum ExitState { Closed, Opening, Open, Blocked }

        public struct ExitInfo
        {
            public string name;
            public bool entrance;
            public ExitState state;
            public string blockReason;
            public float perMinute;
        }

        /// <summary>A switch was clicked (door index). Return false when it was refused (no free gate crew).</summary>
        [NonSerialized] public Func<int, bool> onSwitch;
        /// <summary>START was pressed.</summary>
        [NonSerialized] public Action onStart;

        // Board geometry (ControlBoard_Layout.json): panel tilted 12°, front lip 2 cm; u across, v up the slope, w out.
        const float BoardTilt = 12f, BoardLip = 0.020f;
        static readonly float[] SwitchU = { -0.495f, -0.415f, -0.335f, -0.255f, -0.175f, -0.095f, -0.015f, 0.065f };
        const float SwitchV = 0.115f, LampV = 0.205f, LabelV = 0.042f;
        static readonly float[] CamU = { 0.155f, 0.197f, 0.239f, 0.281f, 0.323f };
        static readonly float[] CamV = { 0.120f, 0.072f };
        const float LeverOn = 24f, LeverOff = -24f, CoverOpen = -105f, PressDepth = 0.004f;

        class BoardSwitch
        {
            public Transform lever, cover;
            public Renderer lamp;
            public Material lampMaterial;
            public TextMesh label;
            public string labelText;
            public float leverAngle = LeverOff, coverAngle, rejectUntil = -99f, rejectKick;
        }

        class BoardButton
        {
            public Transform pivot;
            public Renderer cap;
            public Material material;
            public Color baseColor;
            public float pressedUntil = -99f;
            public int camera;   // 1..9, 0 = AUTO, -1 = START
        }

        readonly List<BoardSwitch> switches = new();
        readonly List<BoardButton> boardButtons = new();
        TextMesh crewText;
        ExitInfo[] exits = Array.Empty<ExitInfo>();
        int crewsFree, crewsTotal;
        bool planning;
        string boardHover;
        Font boardFont;

        static Vector3 PanelNormal => new(0f, Mathf.Cos(BoardTilt * Mathf.Deg2Rad), Mathf.Sin(BoardTilt * Mathf.Deg2Rad));
        static Vector3 PanelUp => new(0f, Mathf.Sin(BoardTilt * Mathf.Deg2Rad), -Mathf.Cos(BoardTilt * Mathf.Deg2Rad));

        /// <summary>A point on the panel in the board's Unity space (the OBJ's u is mirrored to -x).</summary>
        static Vector3 OnPanel(float u, float v, float w) =>
            new Vector3(-u, BoardLip, -0.012f) + v * PanelUp + w * PanelNormal;

        /// <summary>Finds the board's parts, gives the moving ones pivots and adds the label texts. Once, after Build.</summary>
        void SetupBoard()
        {
            if (Board == null) return;
            boardFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var parts = new Dictionary<string, Renderer>();
            foreach (var r in Board.GetComponentsInChildren<Renderer>(true)) parts[r.name] = r;
            var root = Board.transform;

            for (int i = 0; i < SwitchU.Length; i++)
            {
                var sw = new BoardSwitch();
                float u = SwitchU[i];
                if (parts.TryGetValue($"Switch_{i + 1}_Lever", out var lever)) sw.lever = Pivot(root, lever.transform, OnPanel(u, SwitchV, 0.009f), $"Switch {i + 1} lever pivot");
                if (parts.TryGetValue($"Switch_{i + 1}_Cover", out var cover)) sw.cover = Pivot(root, cover.transform, OnPanel(u, SwitchV + 0.026f, 0.004f), $"Switch {i + 1} cover pivot");
                if (parts.TryGetValue($"Switch_{i + 1}_Lamp", out var lamp))
                {
                    sw.lamp = lamp;
                    sw.lampMaterial = new Material(lamp.sharedMaterial) { name = $"Exit lamp {i + 1}" };
                    sw.lampMaterial.EnableKeyword("_EMISSION");
                    lamp.sharedMaterial = sw.lampMaterial;
                    ownedBoardMaterials.Add(sw.lampMaterial);
                }
                sw.label = PanelText(root, $"Label {i + 1} text", OnPanel(u, LabelV, 0.0032f), 0.0075f, new Color(0.08f, 0.08f, 0.09f));
                switches.Add(sw);
            }

            int n = 0;
            for (int row = 0; row < CamV.Length; row++)
                for (int k = 0; k < CamU.Length; k++)
                {
                    bool auto = row == 1 && k == 4;
                    string name = auto ? "CamButton_Auto" : $"CamButton_{++n}";
                    if (!parts.TryGetValue(name, out var cap)) continue;
                    var b = new BoardButton { camera = auto ? 0 : n, cap = cap };
                    b.pivot = Pivot(root, cap.transform, OnPanel(CamU[k], CamV[row], 0.004f), name + " pivot");
                    b.material = new Material(cap.sharedMaterial) { name = name };
                    b.material.EnableKeyword("_EMISSION");
                    b.baseColor = b.material.GetColor("_BaseColor");
                    cap.sharedMaterial = b.material;
                    ownedBoardMaterials.Add(b.material);
                    var t = PanelText(b.pivot, name + " text", OnPanel(CamU[k], CamV[row], 0.0155f) - OnPanel(CamU[k], CamV[row], 0.004f), 0.009f,
                        auto ? new Color(0.1f, 0.06f, 0f) : new Color(0.85f, 0.87f, 0.9f));
                    t.text = auto ? "AUTO" : n.ToString();
                    FitText(t, 0.024f);
                    boardButtons.Add(b);
                }
            if (parts.TryGetValue("StartButton", out var start))
            {
                var b = new BoardButton { camera = -1, cap = start };
                b.pivot = Pivot(root, start.transform, OnPanel(0.46f, 0.165f, 0.010f), "Start pivot");
                b.material = new Material(start.sharedMaterial) { name = "Start button" };
                b.material.EnableKeyword("_EMISSION");
                b.baseColor = b.material.GetColor("_BaseColor");
                start.sharedMaterial = b.material;
                ownedBoardMaterials.Add(b.material);
                boardButtons.Add(b);
            }

            Caption(root, "EXITS", OnPanel(-0.445f, 0.254f, 0.0032f), 0.010f, 0.17f);
            Caption(root, "WALL CAMERA", OnPanel(0.245f, 0.038f, 0.0032f), 0.010f, 0.2f);
            Caption(root, "START SHOW", OnPanel(0.46f, 0.236f, 0.0032f), 0.010f, 0.085f);
            crewText = PanelText(root, "Crew display text", OnPanel(0.245f, 0.2065f, 0.009f), 0.02f, new Color(0.3f, 1f, 0.45f));
        }

        readonly List<Material> ownedBoardMaterials = new();

        /// <summary>An empty at <paramref name="localPivot"/> (board space) that the part hangs from, so it turns/moves about that point.</summary>
        static Transform Pivot(Transform board, Transform part, Vector3 localPivot, string name)
        {
            var pivot = new GameObject(name).transform;
            pivot.SetParent(board, false);
            pivot.localPosition = localPivot;
            part.SetParent(pivot, true);
            return pivot;
        }

        /// <summary>Text lying on the panel (readable from the operator's seat), centred on <paramref name="localPos"/>.</summary>
        TextMesh PanelText(Transform parent, string name, Vector3 localPos, float height, Color color)
        {
            var text = new GameObject(name).AddComponent<TextMesh>();
            text.transform.SetParent(parent, false);
            text.transform.localPosition = localPos;
            text.transform.localRotation = Quaternion.LookRotation(-PanelNormal, PanelUp);
            text.font = boardFont;
            text.fontSize = 64;
            text.characterSize = height / 6.4f;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.richText = true;
            text.color = color;
            var mr = text.GetComponent<MeshRenderer>();
            mr.sharedMaterial = boardFont.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            text.gameObject.layer = RoomLayer;
            return text;
        }

        void Caption(Transform root, string label, Vector3 pos, float height, float maxWidth)
        {
            var t = PanelText(root, label, pos, height, new Color(0.08f, 0.08f, 0.09f));
            t.text = $"<b>{label}</b>";
            FitText(t, maxWidth);
        }

        /// <summary>Shrinks the text until it's no wider than <paramref name="maxWidth"/> (m).</summary>
        static void FitText(TextMesh text, float maxWidth)
        {
            var r = text.GetComponent<Renderer>();
            for (int i = 0; i < 6; i++)
            {
                float width = r.bounds.size.x;
                if (width <= maxWidth || width <= 0f) break;
                text.characterSize *= maxWidth / width * 0.97f;
            }
        }

        /// <summary>The game's view of the exits, crews and phase; called every frame.</summary>
        public void UpdateBoard(ExitInfo[] exitInfo, int freeCrews, int totalCrews, bool inPlanning)
        {
            exits = exitInfo ?? Array.Empty<ExitInfo>();
            crewsFree = freeCrews;
            crewsTotal = totalCrews;
            planning = inPlanning;
        }

        /// <summary>The game refused a switch (no free gate crew): it springs back and its lamp flashes red.</summary>
        public void RejectSwitch(int door)
        {
            if (door < 0 || door >= switches.Count) return;
            switches[door].rejectUntil = Time.unscaledTime + 1.5f;
            switches[door].rejectKick = 1f;
        }

        static readonly Color LampGreen = new(0.1f, 1f, 0.3f), LampYellow = new(1f, 0.75f, 0.05f), LampRed = new(1f, 0.08f, 0.05f);

        void AnimateBoard()
        {
            if (switches.Count == 0) return;
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            bool blink = Mathf.Repeat(now, 0.5f) < 0.25f;
            for (int i = 0; i < switches.Count; i++)
            {
                var sw = switches[i];
                bool used = i < exits.Length;
                var info = used ? exits[i] : default;
                bool hasSwitch = used && !info.entrance;
                if (sw.lever != null) sw.lever.gameObject.SetActive(hasSwitch);
                if (sw.cover != null) sw.cover.gameObject.SetActive(hasSwitch);
                if (sw.lamp != null) sw.lamp.enabled = used;
                sw.label.gameObject.SetActive(used);
                if (!used) continue;

                // lever: on while the gate is wanted open; a refused flick kicks it half way and it springs back
                bool wantedOpen = info.state == ExitState.Open || info.state == ExitState.Opening;
                sw.rejectKick = Mathf.MoveTowards(sw.rejectKick, 0f, dt * 2.5f);
                float target = (wantedOpen ? LeverOn : LeverOff) + sw.rejectKick * (LeverOn - LeverOff) * 0.6f;
                sw.leverAngle = Mathf.MoveTowards(sw.leverAngle, target, dt * 300f);
                if (sw.lever != null) sw.lever.localRotation = Quaternion.AngleAxis(sw.leverAngle, Vector3.right);
                // cover: lifted while the gate is open (or being flicked)
                float coverTarget = wantedOpen || sw.rejectKick > 0f ? CoverOpen : 0f;
                sw.coverAngle = Mathf.MoveTowards(sw.coverAngle, coverTarget, dt * 500f);
                if (sw.cover != null) sw.cover.localRotation = Quaternion.AngleAxis(sw.coverAngle, Vector3.right);

                Color lamp = now < sw.rejectUntil ? (blink ? LampRed * 4f : Color.black)
                           : info.state switch
                           {
                               ExitState.Open => LampGreen * 3f,
                               ExitState.Opening => LampYellow * 3f,
                               ExitState.Blocked => blink ? LampRed * 4f : Color.black,
                               _ => LampRed * 2f,
                           };
                if (sw.lampMaterial != null) sw.lampMaterial.SetColor("_EmissionColor", lamp);

                string label = info.state == ExitState.Blocked
                    ? $"<b>{info.name.ToUpperInvariant()}</b>\n<color=#b00000>BLOCKED: {(info.blockReason ?? "").ToUpperInvariant()}</color>"
                    : $"<b>{info.name.ToUpperInvariant()}</b>\n{info.perMinute:0}/min";
                if (label != sw.labelText)
                {
                    sw.labelText = label;
                    sw.label.characterSize = 0.0075f / 6.4f;
                    sw.label.text = label;
                    FitText(sw.label, 0.068f);
                }
            }

            foreach (var b in boardButtons)
            {
                float depth = now < b.pressedUntil ? PressDepth : 0f;
                if (!buttonRest.TryGetValue(b, out var r)) buttonRest[b] = r = b.pivot.localPosition;
                b.pivot.localPosition = Vector3.MoveTowards(b.pivot.localPosition, r - PanelNormal * depth, dt * 0.08f);

                bool lit = b.camera == -1 ? planning && blink
                         : b.camera == 0 ? WallAuto
                         : !WallAuto && WallRegion == b.camera - 1;
                Color glow = !lit ? Color.black : b.camera == -1 ? new Color(0.1f, 1f, 0.3f) * 2f : b.camera == 0 ? new Color(1f, 0.6f, 0.1f) * 2f : new Color(0.3f, 0.6f, 1f) * 2.5f;
                b.material.SetColor("_EmissionColor", glow);
                // cameras beyond this map's zones stay dark
                if (b.camera > 0) b.cap.enabled = b.camera <= feeds.Count;
            }

            if (crewText != null)
            {
                string crew = crewsTotal > 0 ? $"CREWS {crewsFree}/{crewsTotal}" : "CREWS -";
                if (crewText.text != crew)
                {
                    crewText.characterSize = 0.02f / 6.4f;
                    crewText.text = crew;
                    FitText(crewText, 0.18f);
                }
                crewText.color = crewsFree == 0 ? new Color(1f, 0.35f, 0.25f) : new Color(0.3f, 1f, 0.45f);
            }
        }

        readonly Dictionary<BoardButton, Vector3> buttonRest = new();

        /// <summary>Free look: the switch / button under the crosshair (by bounds), for clicks and the hover hint.</summary>
        bool BoardPick(Ray ray, out int switchIndex, out BoardButton button)
        {
            switchIndex = -1;
            button = null;
            float best = float.MaxValue;
            for (int i = 0; i < switches.Count && i < exits.Length; i++)
            {
                if (exits[i].entrance) continue;
                foreach (var t in new[] { switches[i].lever, switches[i].cover })
                {
                    if (t == null) continue;
                    var r = t.GetComponentInChildren<Renderer>();
                    if (r != null && r.enabled && r.bounds.IntersectRay(ray, out float d) && d < best && d < 3f)
                    {
                        best = d;
                        switchIndex = i;
                        button = null;
                    }
                }
            }
            foreach (var b in boardButtons)
                if (b.cap.enabled && b.cap.bounds.IntersectRay(ray, out float d) && d < best && d < 3f)
                {
                    best = d;
                    button = b;
                    switchIndex = -1;
                }
            return switchIndex >= 0 || button != null;
        }

        /// <summary>From <see cref="UpdateRoomInput"/> in free look: hover hint and clicks on the board.</summary>
        void BoardInput(Ray ray, bool click)
        {
            boardHover = null;
            if (!BoardPick(ray, out int sw, out var button)) return;
            if (sw >= 0)
            {
                var info = exits[sw];
                string action = info.state == ExitState.Open || info.state == ExitState.Opening ? "close" : "open";
                boardHover = $"{info.name}: click to {action} ({crewsFree}/{crewsTotal} crews free)";
                if (click)
                {
                    ClickTakenByRoom = true;
                    bool ok = onSwitch?.Invoke(sw) ?? false;
                    if (!ok) RejectSwitch(sw);
                }
                return;
            }
            boardHover = button.camera == -1 ? (planning ? "START the show" : "The show is on")
                       : button.camera == 0 ? "Video wall: AUTO (follows the latest incident)"
                       : $"Video wall: CAM {button.camera:00} {feeds[button.camera - 1].zoneName}";
            if (!click) return;
            ClickTakenByRoom = true;
            button.pressedUntil = Time.unscaledTime + 0.15f;
            if (button.camera == -1) { if (planning) onStart?.Invoke(); }
            else if (button.camera == 0) SetWallAuto();
            else SelectWall(button.camera - 1, true);
        }

        void DrawBoardHover()
        {
            if (string.IsNullOrEmpty(boardHover) || Focused || !lookEnabled) return;
            float s = Mathf.Max(1f, Screen.height / 900f);
            var style = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(13 * s), alignment = TextAnchor.MiddleCenter };
            var content = new GUIContent(boardHover);
            var size = style.CalcSize(content);
            GUI.Box(new Rect(Screen.width * 0.5f - size.x * 0.5f, Screen.height * 0.5f + 18f * s, size.x, size.y), content, style);
        }
    }
}
