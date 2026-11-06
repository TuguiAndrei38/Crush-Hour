using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CrushHour
{
    /// <summary>
    /// Step 2: one CCTV camera per venue region, rendered round-robin into small RenderTextures shown on the desk
    /// monitors (zone 1 on the operator's far left). Each picture has a "CAM 03 | FOOD COURT ● REC 00:42" caption that
    /// only its own camera sees, and the monitor's frame flashes red for a new incident in its zone, yellow for high
    /// pressure or detected gas.
    /// </summary>
    public partial class ControlRoom
    {
        /// <summary>Captions live on this layer; the operator camera never draws it.</summary>
        public const int CaptionLayer = 28;

        [Header("CCTV")]
        public int feedWidth = 480, feedHeight = 270;
        [Tooltip("Frames per second of each desk monitor feed (cameras take turns).")]
        public float feedFps = 12f;
        [Tooltip("At most this many CCTV cameras render in one frame.")]
        public int maxFeedsPerFrame = 3;
        public float cameraMinHeight = 6f, cameraMaxHeight = 9f;
        [Tooltip("How far from the zone's outer corner towards its centre the camera pole stands (0 = on the corner).")]
        [Range(0f, 0.6f)] public float cameraInset = 0.3f;
        [Tooltip("Part of the zone the picture fits (1 = all four corners; smaller = closer).")]
        [Range(0.3f, 1.1f)] public float framing = 0.6f;
        public Color poleColor = new(0.32f, 0.33f, 0.35f);

        public class Feed
        {
            public int region;
            public string zoneName;
            public Camera camera;
            public RenderTexture texture;
            public TextMesh caption;
            public MeshRenderer captionRenderer;
            public float nextRender;
            public Renderer monitor;
            public Material frameMaterial;
        }

        readonly List<Feed> feeds = new();
        public IReadOnlyList<Feed> Feeds => feeds;
        Transform cctvRoot;
        readonly List<Renderer> deskMonitors = new();
        bool hookedCameraEvents;

        /// <summary>(Re)creates the CCTV cameras for this venue's regions and puts their pictures on the desk monitors.</summary>
        public void SetupCctv(FestivalVenue venue)
        {
            ClearCctv();
            if (Root == null) return;
            if (deskMonitors.Count == 0) FindDeskMonitors();
            cctvRoot = new GameObject("CCTV Cameras (generated)").transform;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var venueCenter = new Vector3(FestivalVenue.Width * 0.5f, 0f, FestivalVenue.Height * 0.5f);

            for (int r = 0; r < venue.regions.Count; r++)
            {
                var region = venue.regions[r];
                var feed = new Feed { region = r, zoneName = region.name };
                feed.texture = new RenderTexture(feedWidth, feedHeight, 16) { name = $"CCTV {r + 1}", antiAliasing = 1 };

                var cam = new GameObject($"CCTV {r + 1:00} - {region.name}").AddComponent<Camera>();
                cam.transform.SetParent(cctvRoot, false);
                PlaceCamera(cam, region.area, venueCenter);
                cam.targetTexture = feed.texture;
                cam.cullingMask = ~((1 << RoomLayer) | (1 << MinimapLayer));
                cam.nearClipPlane = 0.5f;
                cam.farClipPlane = 250f;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.enabled = false;   // rendered in turns by UpdateCctv
                CheapCamera(cam);
                feed.camera = cam;
                feed.nextRender = Time.unscaledTime + r * 0.02f;

                // Caption in front of the lens, top-left of its picture, on the caption layer.
                var text = new GameObject("Caption").AddComponent<TextMesh>();
                text.transform.SetParent(cam.transform, false);
                const float d = 1f;
                float halfH = d * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), halfW = halfH * cam.aspect;
                text.transform.localPosition = new Vector3(-halfW * 0.95f, halfH * 0.93f, d);
                text.font = font;
                text.fontSize = 48;
                text.characterSize = 2f * halfH * 0.055f / 4.8f;   // ~5.5% of the picture height
                text.anchor = TextAnchor.UpperLeft;
                text.richText = true;
                text.color = Color.white;
                var mr = text.GetComponent<MeshRenderer>();
                mr.sharedMaterial = font.material;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.enabled = false;
                text.gameObject.layer = CaptionLayer;
                feed.caption = text;
                feed.captionRenderer = mr;

                if (r < deskMonitors.Count) AttachToMonitor(feed, deskMonitors[r]);
                feeds.Add(feed);
            }
            if (feeds.Count > deskMonitors.Count)
                Debug.LogWarning($"Crush Hour: {feeds.Count} zones but only {deskMonitors.Count} desk monitors.");

            if (OperatorCamera != null) OperatorCamera.cullingMask &= ~((1 << CaptionLayer) | (1 << MinimapLayer));
            SetupWallAndMinimap(venue, font);
            if (!hookedCameraEvents)
            {
                RenderPipelineManager.beginCameraRendering += OnBeginCamera;
                RenderPipelineManager.endCameraRendering += OnEndCamera;
                hookedCameraEvents = true;
            }
        }

        /// <summary>From the region's corner farthest from the venue centre, pulled outwards, high up, looking at the middle.</summary>
        void PlaceCamera(Camera cam, RectInt area, Vector3 venueCenter)
        {
            var center = new Vector3(area.center.x, 0f, area.center.y);
            var corners = new[]
            {
                new Vector3(area.xMin, 0f, area.yMin), new Vector3(area.xMax, 0f, area.yMin),
                new Vector3(area.xMin, 0f, area.yMax), new Vector3(area.xMax, 0f, area.yMax),
            };
            Vector3 best = corners[0];
            foreach (var c in corners) if ((c - venueCenter).sqrMagnitude > (best - venueCenter).sqrMagnitude) best = c;
            var outward = best - center;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.01f) outward = Vector3.back;
            float diagonal = new Vector2(area.width, area.height).magnitude;
            float height = Mathf.Clamp(diagonal * 0.25f, cameraMinHeight, cameraMaxHeight);
            // On a pole inside the zone, part way from its outer corner to its centre, so the picture is close on the zone.
            var foot = Vector3.Lerp(best, center, cameraInset);
            var inward = (center - foot);
            inward.y = 0f;
            inward = inward.sqrMagnitude > 0.01f ? inward.normalized : -outward.normalized;
            // The camera hangs on a short arm off the pole top, towards the zone (keeps the pole out of the picture).
            var position = foot + inward * 0.9f + Vector3.up * (height - 0.35f);
            cam.transform.SetPositionAndRotation(position, Quaternion.LookRotation(center - position, Vector3.up));
            BuildPole(cam.name, foot, inward, height);

            // Field of view that fits every corner of the zone (with a small margin).
            cam.aspect = feedWidth / (float)feedHeight;
            float need = 0f;
            foreach (var corner in corners)
            {
                var c = Vector3.Lerp(center, corner, framing);   // frame the inner part of the zone
                var local = cam.transform.InverseTransformPoint(c);
                if (local.z < 0.5f) continue;
                need = Mathf.Max(need, Mathf.Abs(local.y) / local.z, Mathf.Abs(local.x) / (local.z * cam.aspect));
            }
            cam.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(need) * Mathf.Rad2Deg, 20f, 90f);
        }

        Material poleMaterial, poleCameraMaterial;

        /// <summary>A CCTV pole in the venue: mast, arm, camera housing (seen by the other cameras, like in real life).</summary>
        void BuildPole(string name, Vector3 foot, Vector3 inward, float height)
        {
            if (poleMaterial == null)
            {
                poleMaterial = new Material(litMaterial != null ? litMaterial : new Material(Shader.Find("Universal Render Pipeline/Lit"))) { name = "CCTV pole" };
                poleMaterial.SetColor("_BaseColor", poleColor);
                poleCameraMaterial = new Material(poleMaterial) { name = "CCTV pole camera" };
                poleCameraMaterial.SetColor("_BaseColor", new Color(0.85f, 0.86f, 0.88f));
            }
            var pole = new GameObject($"Pole {name}").transform;
            pole.SetParent(cctvRoot, false);
            pole.position = foot;
            pole.rotation = Quaternion.LookRotation(inward, Vector3.up);
            Part(pole, PrimitiveType.Cylinder, poleMaterial, new Vector3(0f, height * 0.5f, 0f), new Vector3(0.22f, height * 0.5f, 0.22f), Quaternion.identity);
            Part(pole, PrimitiveType.Cylinder, poleMaterial, new Vector3(0f, 0.08f, 0f), new Vector3(0.5f, 0.08f, 0.5f), Quaternion.identity);   // base plate
            Part(pole, PrimitiveType.Cube, poleMaterial, new Vector3(0f, height - 0.15f, 0.45f), new Vector3(0.1f, 0.1f, 0.95f), Quaternion.identity);   // arm
            var tilt = Quaternion.Euler(35f, 0f, 0f);
            Part(pole, PrimitiveType.Cube, poleCameraMaterial, new Vector3(0f, height - 0.35f, 0.85f), new Vector3(0.18f, 0.16f, 0.42f), tilt);   // housing
        }

        static void Part(Transform parent, PrimitiveType type, Material material, Vector3 localPos, Vector3 scale, Quaternion localRot)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(localPos, localRot);
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.On;
        }

        /// <summary>The Monitor_01..09 objects of the desk model, ordered from the operator's left to right.</summary>
        void FindDeskMonitors()
        {
            deskMonitors.Clear();
            if (Desk == null) return;
            var found = new List<(float x, Renderer renderer)>();
            foreach (var renderer in Desk.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.name.StartsWith("Monitor_")) continue;
                // The operator faces -Z, so their left is +X in the room.
                float x = Root.InverseTransformPoint(renderer.bounds.center).x;
                found.Add((x, renderer));
            }
            found.Sort((a, b) => b.x.CompareTo(a.x));
            foreach (var f in found) deskMonitors.Add(f.renderer);
            if (deskMonitors.Count == 0) Debug.LogWarning("Crush Hour: no Monitor_xx objects found in the desk model.");
        }

        /// <summary>Gives this monitor its own screen material (the feed, glowing) and its own frame material (for the alarm flash).</summary>
        void AttachToMonitor(Feed feed, Renderer monitor)
        {
            feed.monitor = monitor;
            var mats = monitor.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                string name = mats[i] != null ? mats[i].name : "";
                if (name.StartsWith("M_Monitor_Screen"))
                {
                    var screen = new Material(mats[i]) { name = $"Monitor screen {feed.region + 1}" };
                    screen.SetTexture("_BaseMap", feed.texture);
                    screen.SetColor("_BaseColor", new Color(0.25f, 0.25f, 0.25f));
                    screen.EnableKeyword("_EMISSION");
                    screen.SetTexture("_EmissionMap", feed.texture);
                    screen.SetColor("_EmissionColor", Color.white);
                    screen.SetFloat("_Smoothness", 0.8f);
                    mats[i] = screen;
                    ownedMaterials.Add(screen);
                }
                else if (name.StartsWith("M_Monitor_Plastic"))
                {
                    var frame = new Material(mats[i]) { name = $"Monitor frame {feed.region + 1}" };
                    frame.EnableKeyword("_EMISSION");
                    frame.SetColor("_EmissionColor", Color.black);
                    mats[i] = frame;
                    feed.frameMaterial = frame;
                    ownedMaterials.Add(frame);
                }
            }
            monitor.sharedMaterials = mats;
        }

        readonly List<Material> ownedMaterials = new();
        static readonly Color AlarmRed = new(1f, 0.08f, 0.05f), WarnYellow = new(1f, 0.75f, 0.05f);

        /// <summary>
        /// Called by the game every frame. <paramref name="clock"/> is the show clock in seconds; per region:
        /// alarm = 0 none, 1 steady red (incident still active), 2 flashing red (new incident); warning = yellow.
        /// </summary>
        public void UpdateCctv(float clock, int[] alarm, bool[] warning)
        {
            if (feeds.Count == 0) return;
            UpdateWallAndMinimap(clock);
            int minutes = (int)clock / 60, seconds = (int)clock % 60;
            bool blink = Mathf.Repeat(Time.unscaledTime, 0.5f) < 0.25f;
            bool slowBlink = Mathf.Repeat(Time.unscaledTime, 1f) < 0.5f;

            // Round-robin: render the feeds whose turn it is (at most maxFeedsPerFrame this frame).
            float now = Time.unscaledTime, interval = 1f / Mathf.Max(1f, feedFps);
            int rendered = 0;
            foreach (var feed in feeds)
            {
                bool due = now >= feed.nextRender && rendered < maxFeedsPerFrame;
                feed.camera.enabled = due;
                if (due)
                {
                    rendered++;
                    feed.nextRender = Mathf.Max(feed.nextRender + interval, now);
                    string dot = slowBlink ? "<color=#ff3030>●</color>" : "<color=#00000000>●</color>";
                    feed.caption.text = $"CAM {feed.region + 1:00} | {feed.zoneName.ToUpperInvariant()}  {dot} REC {minutes:00}:{seconds:00}";
                }

                if (feed.frameMaterial != null)
                {
                    int a = alarm != null && feed.region < alarm.Length ? alarm[feed.region] : 0;
                    bool w = warning != null && feed.region < warning.Length && warning[feed.region];
                    Color glow = a == 2 ? (blink ? AlarmRed * 2.5f : Color.black)
                               : a == 1 ? AlarmRed * 0.9f
                               : w ? (slowBlink ? WarnYellow * 1.6f : WarnYellow * 0.3f)
                               : Color.black;
                    feed.frameMaterial.SetColor("_EmissionColor", glow);
                }
            }
        }

        void OnBeginCamera(ScriptableRenderContext context, Camera cam)
        {
            foreach (var feed in feeds) feed.captionRenderer.enabled = feed.camera == cam;
            foreach (var r in wallCaptionRenderers) if (r != null) r.enabled = cam == wallCamera;
        }

        void OnEndCamera(ScriptableRenderContext context, Camera cam)
        {
            foreach (var feed in feeds) feed.captionRenderer.enabled = false;
            foreach (var r in wallCaptionRenderers) if (r != null) r.enabled = false;
        }

        void ClearCctv()
        {
            foreach (var feed in feeds)
            {
                if (feed.texture != null) { feed.texture.Release(); Destroy(feed.texture); }
            }
            feeds.Clear();
            foreach (var m in ownedMaterials) if (m != null) Destroy(m);
            ownedMaterials.Clear();
            ClearWallAndMinimap();
            if (cctvRoot != null) Destroy(cctvRoot.gameObject);
            cctvRoot = null;
            // Monitors get fresh materials on the next AttachToMonitor; put the shared ones back meanwhile.
            foreach (var monitor in deskMonitors)
            {
                if (monitor == null) continue;
                var mats = monitor.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    if (mats[i].name.StartsWith("Monitor screen")) mats[i] = RoomMaterial("M_Monitor_Screen");
                    else if (mats[i].name.StartsWith("Monitor frame")) mats[i] = RoomMaterial("M_Monitor_Plastic");
                }
                monitor.sharedMaterials = mats;
            }
        }

        void UnhookCctv()
        {
            if (hookedCameraEvents)
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
                RenderPipelineManager.endCameraRendering -= OnEndCamera;
                hookedCameraEvents = false;
            }
            ClearCctv();
        }
    }
}
