using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace CrushHour
{
    /// <summary>
    /// The first-person control room (week 6). Builds the room from the OBJ models in Assets/Models/ControlRoom far
    /// away from the venue, lights it with its own lamps, and turns the main camera into the seated operator's view
    /// (mouse look, crosshair, right mouse = zoom). <see cref="CrushHourGame"/> finds it in Start and then runs in
    /// control-room mode (no top-down camera, no map clicks, reduced HUD).
    /// </summary>
    public partial class ControlRoom : MonoBehaviour
    {
        /// <summary>Everything in the room is on this layer: the venue's sun ignores it, the room lamps light only it.</summary>
        public const int RoomLayer = 29;

        [Tooltip("Where the room is built: far from the venue (0..104 × 0..76) so venue cameras never see it.")]
        public Vector3 origin = new(-400f, 0f, -400f);

        [Header("Models (Assets/Models/ControlRoom, assigned by Tools > Crush Hour > Create Control Room Scene)")]
        public GameObject roomModel;
        public GameObject doorModel;
        public GameObject videoWallModel;
        public GameObject deskModel;
        public GameObject boardModel;
        public GameObject chairModel;
        public GameObject cctvModel;
        public GameObject walkieModel;
        [Tooltip("URP Lit material to clone for the room (the game's CrushHour_Lit).")]
        public Material litMaterial;

        [Header("Lights")]
        public Color lampColor = new(1f, 0.96f, 0.9f);
        public float lampIntensity = 20f;
        public float lampRange = 10f;

        [Header("Operator view")]
        [Tooltip("Mouse look speed (degrees per pixel of mouse movement).")]
        public float lookSensitivity = 0.12f;
        [Tooltip("How far the operator can turn left/right from facing the video wall.")]
        public float maxYaw = 120f;
        public float minPitch = -55f, maxPitch = 65f;
        public float fieldOfView = 60f, zoomFieldOfView = 22f;
        [Tooltip("How fast WASD rolls the chair (m/s).")]
        public float chairSpeed = 1.6f;
        [Tooltip("How quickly the zoom (right mouse) blends in and out.")]
        public float zoomSpeed = 10f;

        // Room layout, in Unity coordinates relative to the room origin (floor, centre of the video-wall wall's inner
        // face). Unity mirrors X when it imports OBJ files, so these are ControlRoom_Layout.json with x (and yaw) negated.
        static readonly Vector3 VideoWallPos = new(0f, 1.6f, 0f);
        static readonly Vector3 DeskPos = new(0f, 0f, 3.05f);
        static readonly Vector3 BoardPos = new(0f, 0.75f, 3.038f);
        static readonly Vector3 ChairPos = new(0f, 0f, 3.65f);
        static readonly Vector3 DoorPos = new(-3.62f, 0f, 9.1f);
        static readonly (Vector3 position, float yaw)[] CctvPlaces = { (new Vector3(6f, 5.4f, 8.4f), -90f), (new Vector3(-6f, 5.4f, 8.4f), 90f) };
        static readonly Vector3 EyePos = new(0f, 1.2f, 3.6f);
        /// <summary>The seat (eye) position in the room now: WASD rolls the chair around the desk.</summary>
        Vector3 seat = EyePos;
        Transform chair;
        static readonly Vector3[] LampPositions =
        {
            new(-2.6f, 5.82f, 2.2f), new(2.6f, 5.82f, 2.2f), new(-2.6f, 5.82f, 4.8f),
            new(2.6f, 5.82f, 4.8f), new(-2.6f, 5.82f, 7.4f), new(2.6f, 5.82f, 7.4f),
        };

        /// <summary>Set by the game every frame: look around (cursor locked) only while no card or menu needs the mouse.</summary>
        [System.NonSerialized] public bool lookEnabled;
        /// <summary>Set by the game while the radio wheels are open: the mouse moves their pointer, not the view.</summary>
        [System.NonSerialized] public bool radioOpen;
        Transform walkie;
        Material walkieLed;
        float walkieRaise;

        public Transform Root { get; private set; }
        public Camera OperatorCamera { get; private set; }
        public GameObject VideoWall { get; private set; }
        public GameObject Desk { get; private set; }
        public GameObject Board { get; private set; }

        float yaw, pitch;
        readonly Dictionary<string, Material> materials = new();
        Texture2D crosshairTexture;

        // Colours of the materials in ControlRoom.mtl / ControlDesk.mtl / CCTV_Monitor.mtl (Kd), so the room looks
        // the same whatever Unity's OBJ importer makes of the .mtl files.
        static readonly Dictionary<string, Color> MaterialColors = new()
        {
            { "M_VW_Frame", new(0.035f, 0.036f, 0.040f) }, { "M_VW_Back", new(0.020f, 0.020f, 0.022f) },
            { "M_VW_LED", new(0.10f, 0.55f, 1.00f) },
            { "M_Board_Body", new(0.060f, 0.062f, 0.068f) }, { "M_Board_Plate", new(0.12f, 0.125f, 0.13f) },
            { "M_Board_Trim", new(0.35f, 0.36f, 0.38f) }, { "M_Board_Metal", new(0.55f, 0.56f, 0.58f) },
            { "M_Board_Cover", new(0.75f, 0.08f, 0.06f, 0.55f) }, { "M_Board_Lamp", new(0.16f, 0.15f, 0.13f) },
            { "M_Board_Label", new(0.92f, 0.90f, 0.84f) }, { "M_Board_Button", new(0.10f, 0.105f, 0.11f) },
            { "M_Board_ButtonAuto", new(0.95f, 0.62f, 0.12f) }, { "M_Board_Start", new(0.10f, 0.65f, 0.25f) },
            { "M_Board_Collar", new(0.95f, 0.80f, 0.10f) },
            { "M_Walkie_Body", new(0.040f, 0.042f, 0.045f) }, { "M_Walkie_Rubber", new(0.022f, 0.022f, 0.024f) },
            { "M_Walkie_Grille", new(0.010f, 0.010f, 0.011f) }, { "M_Walkie_Keys", new(0.15f, 0.155f, 0.16f) },
            { "M_Walkie_Accent", new(0.95f, 0.45f, 0.08f) }, { "M_Walkie_LED", new(0.9f, 0.1f, 0.08f) },
            { "M_Chair_Fabric", new(0.050f, 0.055f, 0.065f) }, { "M_Chair_Frame", new(0.020f, 0.020f, 0.022f) },
            { "M_Chair_Metal", new(0.50f, 0.51f, 0.53f) },
            { "M_Cctv_Body", new(0.85f, 0.86f, 0.88f) }, { "M_Cctv_Lens", new(0.02f, 0.02f, 0.025f) },
            { "M_Cctv_LED", new(0.9f, 0.1f, 0.08f) },
            { "M_Room_Floor", new(0.16f, 0.17f, 0.19f) }, { "M_Room_Ceiling", new(0.30f, 0.30f, 0.31f) },
            { "M_Room_Wall", new(0.42f, 0.43f, 0.45f) }, { "M_Room_WallDark", new(0.05f, 0.05f, 0.06f) },
            { "M_Room_Panel", new(0.10f, 0.12f, 0.15f) }, { "M_Room_Skirting", new(0.03f, 0.03f, 0.035f) },
            { "M_Room_LampHousing", new(0.03f, 0.03f, 0.035f) }, { "M_Room_LampLight", new(0.95f, 0.95f, 0.90f) },
            { "M_Room_Alarm", new(0.85f, 0.10f, 0.08f) }, { "M_Room_ExitSign", new(0.10f, 0.75f, 0.30f) },
            { "M_Door", new(0.32f, 0.35f, 0.40f) }, { "M_Door_Metal", new(0.55f, 0.56f, 0.58f) },
            { "M_Desk_Top", new(0.20f, 0.20f, 0.21f) }, { "M_Desk_Body", new(0.055f, 0.057f, 0.062f) },
            { "M_Desk_Edge", new(0.018f, 0.018f, 0.020f) }, { "M_Desk_Trim", new(0.33f, 0.34f, 0.36f) },
            { "M_Monitor_Plastic", new(0.045f, 0.047f, 0.050f) }, { "M_Monitor_Back", new(0.090f, 0.092f, 0.096f) },
            { "M_Monitor_Metal", new(0.30f, 0.31f, 0.33f) }, { "M_Monitor_LED", new(0.10f, 0.90f, 0.35f) },
        };
        static readonly HashSet<string> Emissive = new()
            { "M_VW_LED", "M_Room_LampLight", "M_Room_ExitSign", "M_Cctv_LED", "M_Monitor_LED" };
        static readonly Dictionary<string, float> Smoothness = new()
        {
            { "M_Board_Metal", 0.6f }, { "M_Board_Trim", 0.5f }, { "M_Chair_Metal", 0.6f }, { "M_Door_Metal", 0.6f },
            { "M_Desk_Trim", 0.5f }, { "M_Monitor_Metal", 0.5f }, { "M_Cctv_Lens", 0.9f }, { "M_Board_Cover", 0.7f },
            { "M_Room_Floor", 0.25f }, { "M_Desk_Top", 0.3f },
        };

        /// <summary>Builds the room and takes over the camera. Called once by the game.</summary>
        public void Build(Camera camera)
        {
            if (Root != null) return;
            Root = new GameObject("Control Room (generated)").transform;
            Root.SetPositionAndRotation(origin, Quaternion.identity);

            roomShell = Spawn(roomModel, "Room", Vector3.zero, 0f);
            Spawn(doorModel, "Door", DoorPos, 0f);
            VideoWall = Spawn(videoWallModel, "Video Wall", VideoWallPos, 0f);
            Desk = Spawn(deskModel, "Desk", DeskPos, 0f);
            Board = Spawn(boardModel, "Control Board", BoardPos, 0f);
            var chairObject = Spawn(chairModel, "Operator Chair", ChairPos, 0f);
            chair = chairObject != null ? chairObject.transform : null;
            for (int i = 0; i < CctvPlaces.Length; i++) Spawn(cctvModel, $"CCTV Camera {i + 1}", CctvPlaces[i].position, CctvPlaces[i].yaw);

            for (int i = 0; i < LampPositions.Length; i++)
            {
                var light = new GameObject($"Lamp Light {i + 1}").AddComponent<Light>();
                light.transform.SetParent(Root, false);
                light.transform.localPosition = LampPositions[i];
                light.type = LightType.Point;
                light.color = lampColor;
                light.intensity = lampIntensity;
                light.range = lampRange;
                light.shadows = LightShadows.None;
                light.cullingMask = 1 << RoomLayer;
                light.gameObject.layer = RoomLayer;
            }
            // The venue's sun (and any other light already in the scene) must not light the room through its ceiling.
            foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (light.transform.parent != Root) light.cullingMask &= ~(1 << RoomLayer);

            SetLayer(Root.gameObject, RoomLayer);
            SetupBoard();
            SetupInfo();
            SetupAtmosphere();
            TakeCamera(camera);
        }

        GameObject Spawn(GameObject model, string name, Vector3 position, float yawDegrees)
        {
            if (model == null)
            {
                Debug.LogWarning($"Crush Hour: the control room has no model for '{name}' (run Tools > Crush Hour > Create Control Room Scene).");
                return null;
            }
            var go = Instantiate(model, Root);
            go.name = name;
            go.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(0f, yawDegrees, 0f));
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                var shared = renderer.sharedMaterials;
                for (int i = 0; i < shared.Length; i++) shared[i] = RoomMaterial(shared[i] != null ? shared[i].name : "");
                renderer.sharedMaterials = shared;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            return go;
        }

        static void SetLayer(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayer(child.gameObject, layer);
        }

        /// <summary>A URP Lit material for an imported material name (colour table above; screens are dark glass for now).</summary>
        public Material RoomMaterial(string importedName)
        {
            string key = importedName.Replace(" (Instance)", "").Trim();
            if (materials.TryGetValue(key, out var cached)) return cached;

            var shader = litMaterial != null ? litMaterial.shader : Shader.Find("Universal Render Pipeline/Lit");
            var material = litMaterial != null ? new Material(litMaterial) : new Material(shader);
            material.name = key + " (control room)";
            bool screen = key.Contains("Screen");
            Color color = screen ? new Color(0.015f, 0.02f, 0.025f) : MaterialColors.TryGetValue(key, out var c) ? c : new Color(0.4f, 0.4f, 0.42f);
            material.color = color;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", screen ? 0.85f : Smoothness.TryGetValue(key, out float s) ? s : 0.15f);
            if (Emissive.Contains(key) || screen)
            {
                // Screens without a feed glow faintly ("no signal"); desk monitors get their own feed material (ControlRoom.Cctv.cs).
                var emission = screen ? new Color(0.02f, 0.035f, 0.05f) : color * 2f;
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", emission);
            }
            if (color.a < 0.99f) MakeTransparent(material);
            materials[key] = material;
            return material;
        }

        internal static void MakeTransparent(Material material)
        {
            if (!material.HasProperty("_Surface")) return;
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        void TakeCamera(Camera camera)
        {
            OperatorCamera = camera;
            camera.transform.SetParent(null);
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 60f;   // the room only: the venue is 400 m away
            // Only the room layer: otherwise the whole crowd (instanced, no per-instance culling) and the venue are
            // still submitted to this camera every frame, plus a shadow pass for them.
            camera.cullingMask = 1 << RoomLayer;
            NoShadows(camera);
            camera.fieldOfView = fieldOfView;
            yaw = pitch = 0f;
            seat = EyePos;
            SetupWalkie(camera);
            ApplyView();
        }

        void LateUpdate()
        {
            if (OperatorCamera == null) return;
            // Free look with a locked cursor; in focus (or with a card/menu open) the cursor is free.
            bool look = lookEnabled && !Focused && Application.isFocused;
            Cursor.lockState = look ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !look;

            var mouse = Mouse.current;
            bool zoom = false;
            if (look && mouse != null && !radioOpen)
            {
                zoom = mouse.rightButton.isPressed;
                // Slower turning while zoomed in, so details on the wall are easy to follow.
                float scale = lookSensitivity * (OperatorCamera.fieldOfView / fieldOfView);
                Vector2 delta = mouse.delta.ReadValue() * scale;
                yaw = Mathf.Clamp(yaw + delta.x, -maxYaw, maxYaw);
                pitch = Mathf.Clamp(pitch - delta.y, minPitch, maxPitch);
            }
            if (look) MoveChair();
            float targetFov = zoom ? zoomFieldOfView : fieldOfView;
            AnimateBoard();
            AnimateWalkie();
            AnimateAtmosphere();
            focusBlend = Mathf.MoveTowards(focusBlend, Focused ? 1f : 0f, Time.unscaledDeltaTime / FocusMoveTime);
            OperatorCamera.fieldOfView = Mathf.Lerp(OperatorCamera.fieldOfView, targetFov, 1f - Mathf.Exp(-zoomSpeed * Time.unscaledDeltaTime));
            ApplyView();
            ProcessPointer();   // after the view is placed: the pointer ray is exactly what's on screen
        }

        /// <summary>Seated: the eye stays put, yaw 0 faces the video wall (-Z in the room).</summary>
        void ApplyView()
        {
            var seatPos = Root.TransformPoint(seat);
            var seatRot = Root.rotation * Quaternion.Euler(pitch, 180f + yaw, 0f);
            if (focusBlend <= 0f)
            {
                OperatorCamera.transform.SetPositionAndRotation(seatPos, seatRot);
                return;
            }
            float t = Mathf.SmoothStep(0f, 1f, focusBlend);
            OperatorCamera.transform.SetPositionAndRotation(Vector3.Lerp(seatPos, focusPos, t), Quaternion.Slerp(seatRot, focusRot, t));
        }

        /// <summary>
        /// WASD rolls the chair (with the operator) relative to where they look, in front of the desk: the desk's front
        /// edge plus 0.46 m, following the angled wings, out to x ±2.6 and back to z 8.
        /// </summary>
        void MoveChair()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            var input = new Vector2((keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f),
                                    (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f));
            if (input.sqrMagnitude > 0f)
            {
                // yaw 0 faces -Z; turning right (positive yaw) faces -X ... in room space.
                float a = (180f + yaw) * Mathf.Deg2Rad;
                var forward = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                var right = new Vector3(forward.z, 0f, -forward.x);
                var move = (forward * input.y + right * input.x).normalized * chairSpeed * Time.unscaledDeltaTime;
                seat += move;
            }
            seat.x = Mathf.Clamp(seat.x, -2.6f, 2.6f);
            float zMin = 3.05f + 0.46f + Mathf.Max(0f, Mathf.Abs(seat.x) - 0.86f) * Mathf.Tan(30f * Mathf.Deg2Rad);
            seat.z = Mathf.Clamp(seat.z, zMin, 8f);
            if (chair != null)
            {
                chair.localPosition = new Vector3(seat.x, 0f, seat.z + 0.05f);
                chair.localRotation = Quaternion.Euler(0f, yaw, 0f);
            }
        }

        /// <summary>The walkie-talkie in the operator's left hand, low in the view (step 6).</summary>
        void SetupWalkie(Camera camera)
        {
            if (walkieModel == null || walkie != null) return;
            var go = Instantiate(walkieModel, camera.transform);
            go.name = "Walkie-talkie (in hand)";
            walkie = go.transform;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                var shared = renderer.sharedMaterials;
                for (int i = 0; i < shared.Length; i++)
                {
                    string name = shared[i] != null ? shared[i].name : "";
                    shared[i] = RoomMaterial(name);
                    if (name.StartsWith("M_Walkie_LED"))
                    {
                        walkieLed = new Material(shared[i]) { name = "Walkie LED" };
                        walkieLed.EnableKeyword("_EMISSION");
                        shared[i] = walkieLed;
                    }
                }
                renderer.sharedMaterials = shared;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            SetLayer(go, RoomLayer);
            AnimateWalkie();
        }

        void AnimateWalkie()
        {
            if (walkie == null) return;
            walkie.gameObject.SetActive(lookEnabled && !Focused);
            walkieRaise = Mathf.MoveTowards(walkieRaise, radioOpen ? 1f : 0f, Time.unscaledDeltaTime * 5f);
            float t = Mathf.SmoothStep(0f, 1f, walkieRaise);
            // Low left, screen towards the eye; raised and closer while talking.
            walkie.localPosition = Vector3.Lerp(new Vector3(-0.2f, -0.27f, 0.42f), new Vector3(-0.15f, -0.17f, 0.34f), t);
            walkie.localRotation = Quaternion.Euler(Mathf.Lerp(-12f, -4f, t), 180f + Mathf.Lerp(18f, 8f, t), Mathf.Lerp(10f, 4f, t));
            if (walkieLed != null)
                walkieLed.SetColor("_EmissionColor", radioOpen ? new Color(1f, 0.1f, 0.05f) * 3f : Color.black);
        }

        void OnGUI()
        {
            if (Event.current.type == EventType.Repaint && lookEnabled)
            {
                GUI.matrix = Matrix4x4.identity;
                DrawDebug();
            }
            if (!lookEnabled || Focused || radioOpen || OperatorCamera == null || Event.current.type != EventType.Repaint) return;
            if (crosshairTexture == null) crosshairTexture = Texture2D.whiteTexture;
            GUI.matrix = Matrix4x4.identity;
            float s = Mathf.Max(1f, Screen.height / 900f);
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            float arm = 7f * s, gap = 3f * s, w = Mathf.Round(2f * s);
            var old = GUI.color;
            // dark outline, then the white cross
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            DrawCross(cx, cy, arm + s, gap, w + 2f * s);
            GUI.color = new Color(1f, 1f, 1f, 0.9f);
            DrawCross(cx, cy, arm, gap, w);
            GUI.color = old;
            DrawBoardHover();
        }

        void DrawCross(float cx, float cy, float arm, float gap, float w)
        {
            GUI.DrawTexture(new Rect(cx - gap - arm, cy - w / 2f, arm, w), crosshairTexture);
            GUI.DrawTexture(new Rect(cx + gap, cy - w / 2f, arm, w), crosshairTexture);
            GUI.DrawTexture(new Rect(cx - w / 2f, cy - gap - arm, w, arm), crosshairTexture);
            GUI.DrawTexture(new Rect(cx - w / 2f, cy + gap, w, arm), crosshairTexture);
        }

        void OnDisable()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void OnDestroy()
        {
            UnhookCctv();
            foreach (var m in materials.Values) if (m != null) Destroy(m);
            foreach (var m in ownedBoardMaterials) if (m != null) Destroy(m);
            materials.Clear();
        }
    }
}
