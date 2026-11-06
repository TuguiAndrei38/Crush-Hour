using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CrushHour
{
    /// <summary>
    /// Step 3: the video wall (one zone at a time, high resolution; AUTO follows the latest incident) and the minimap
    /// (an orthographic camera over the whole venue; its markers are made by the game on <see cref="MinimapLayer"/>).
    /// Clicking a desk monitor puts its zone on the wall; looking at the wall, 1–9 pick a zone and 0 goes back to AUTO.
    /// </summary>
    public partial class ControlRoom
    {
        /// <summary>Minimap markers (zones, units, exits, incidents): only the minimap camera draws this layer.</summary>
        public const int MinimapLayer = 27;

        [Header("Video wall")]
        public int wallWidth = 1920, wallHeight = 1080;
        public float wallFps = 30f;
        [Tooltip("How long the big 'CAM 04 – FOOD COURT' title stays after a switch (s).")]
        public float switchTitleTime = 2.5f;
        [Tooltip("How long a warning (smoke, creaking tower) stays on the wall caption (s).")]
        public float warningTime = 12f;

        [Header("Minimap")]
        public int minimapWidth = 1280, minimapHeight = 720;
        public float minimapFps = 10f;

        Camera wallCamera, minimapCamera;
        RenderTexture wallTexture, minimapTexture;
        TextMesh wallCaption, wallTitle;
        readonly List<Renderer> wallCaptionRenderers = new();
        Material wallScreenMaterial, minimapScreenMaterial;
        Renderer wallScreen, minimapScreen;
        float nextWallRender, nextMinimapRender, switchTime = -99f, warningUntil = -99f;
        string warningText;

        /// <summary>Zone shown on the wall (index into the feeds), and whether AUTO picks it.</summary>
        public int WallRegion { get; private set; }
        public bool WallAuto { get; private set; } = true;

        void SetupWallAndMinimap(FestivalVenue venue, Font font)
        {
            if (VideoWall != null)
                foreach (var r in VideoWall.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                        if (m != null && m.name.StartsWith("M_VW_Screen")) wallScreen = r;
            if (Desk != null)
                foreach (var r in Desk.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                        if (m != null && m.name.StartsWith("M_Minimap_Screen")) minimapScreen = r;

            // --- wall camera: takes the selected CCTV camera's place and lens, renders big
            wallTexture = new RenderTexture(wallWidth, wallHeight, 24) { name = "Video wall", antiAliasing = 1 };
            wallCamera = new GameObject("Video Wall Camera").AddComponent<Camera>();
            wallCamera.transform.SetParent(cctvRoot, false);
            wallCamera.targetTexture = wallTexture;
            wallCamera.cullingMask = ~((1 << RoomLayer) | (1 << MinimapLayer));
            wallCamera.nearClipPlane = 0.5f;
            wallCamera.farClipPlane = 250f;
            wallCamera.allowHDR = false;
            wallCamera.allowMSAA = false;
            wallCamera.aspect = wallWidth / (float)wallHeight;
            wallCamera.enabled = false;
            wallCaption = MakeCaption(wallCamera, font, "Caption", TextAnchor.UpperLeft, new Vector2(-0.95f, 0.93f), 0.045f);
            wallTitle = MakeCaption(wallCamera, font, "Switch Title", TextAnchor.MiddleCenter, new Vector2(0f, 0f), 0.09f);
            if (wallScreen != null) wallScreenMaterial = ScreenMaterial(wallScreen, "M_VW_Screen", wallTexture, "Video wall screen");

            // --- minimap: straight down over the whole venue
            minimapTexture = new RenderTexture(minimapWidth, minimapHeight, 16) { name = "Minimap", antiAliasing = 1 };
            minimapCamera = new GameObject("Minimap Camera").AddComponent<Camera>();
            minimapCamera.transform.SetParent(cctvRoot, false);
            minimapCamera.orthographic = true;
            minimapCamera.aspect = minimapWidth / (float)minimapHeight;
            minimapCamera.orthographicSize = Mathf.Max(FestivalVenue.Height * 0.5f, FestivalVenue.Width * 0.5f / minimapCamera.aspect) * 1.04f;
            minimapCamera.transform.SetPositionAndRotation(new Vector3(FestivalVenue.Width * 0.5f, 120f, FestivalVenue.Height * 0.5f), Quaternion.Euler(90f, 0f, 0f));
            minimapCamera.nearClipPlane = 1f;
            minimapCamera.farClipPlane = 200f;
            minimapCamera.cullingMask = ~((1 << RoomLayer) | (1 << CaptionLayer));
            minimapCamera.allowHDR = false;
            minimapCamera.allowMSAA = false;
            minimapCamera.targetTexture = minimapTexture;
            minimapCamera.enabled = false;
            CheapCamera(minimapCamera);
            if (minimapScreen != null) minimapScreenMaterial = ScreenMaterial(minimapScreen, "M_Minimap_Screen", minimapTexture, "Minimap screen");
            BuildScreenSurfaces();

            WallRegion = Mathf.Clamp(WallRegion, 0, Mathf.Max(0, feeds.Count - 1));
            WallAuto = true;
            warningUntil = -99f;
            SelectWall(WallRegion, false, announce: false);
        }

        TextMesh MakeCaption(Camera cam, Font font, string name, TextAnchor anchor, Vector2 at, float heightFraction)
        {
            var text = new GameObject(name).AddComponent<TextMesh>();
            text.transform.SetParent(cam.transform, false);
            text.font = font;
            text.fontSize = 48;
            LayoutCaption(text, cam, at, heightFraction);
            text.anchor = anchor;
            text.alignment = anchor == TextAnchor.MiddleCenter ? TextAlignment.Center : TextAlignment.Left;
            text.richText = true;
            text.color = Color.white;
            var mr = text.GetComponent<MeshRenderer>();
            mr.sharedMaterial = font.material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.enabled = false;
            text.gameObject.layer = CaptionLayer;
            wallCaptionRenderers.Add(mr);
            return text;
        }

        /// <summary>Puts a caption 1 m in front of the lens at <paramref name="at"/> (-1..1 of the picture), sized to the field of view.</summary>
        static void LayoutCaption(TextMesh text, Camera cam, Vector2 at, float heightFraction)
        {
            const float d = 1f;
            float halfH = d * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), halfW = halfH * cam.aspect;
            text.transform.localPosition = new Vector3(at.x * halfW, at.y * halfH, d);
            text.characterSize = 2f * halfH * heightFraction / 4.8f;
        }

        /// <summary>A screen material of its own showing <paramref name="texture"/> (glowing), on the submesh named <paramref name="slot"/>.</summary>
        Material ScreenMaterial(Renderer renderer, string slot, Texture texture, string name)
        {
            Material result = null;
            var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null || !mats[i].name.StartsWith(slot)) continue;
                result = new Material(mats[i]) { name = name };
                result.SetTexture("_BaseMap", texture);
                result.SetColor("_BaseColor", new Color(0.25f, 0.25f, 0.25f));
                result.EnableKeyword("_EMISSION");
                result.SetTexture("_EmissionMap", texture);
                result.SetColor("_EmissionColor", Color.white);
                result.SetFloat("_Smoothness", 0.8f);
                mats[i] = result;
                ownedMaterials.Add(result);
            }
            renderer.sharedMaterials = mats;
            return result;
        }

        /// <summary>Puts a zone on the wall. Manual picks (desk monitor click, keys, board buttons) turn AUTO off.</summary>
        public void SelectWall(int region, bool manual, bool announce = true)
        {
            if (region < 0 || region >= feeds.Count || wallCamera == null) return;
            if (manual) WallAuto = false;
            WallRegion = region;
            var source = feeds[region].camera;
            wallCamera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            // Same framing as the desk monitor (both are 16:9).
            wallCamera.fieldOfView = source.fieldOfView;
            LayoutCaption(wallCaption, wallCamera, new Vector2(-0.95f, 0.93f), 0.045f);
            LayoutCaption(wallTitle, wallCamera, Vector2.zero, 0.09f);
            if (announce) switchTime = Time.unscaledTime;
            wallTitle.text = $"CAM {region + 1:00} – {feeds[region].zoneName.ToUpperInvariant()}";
            nextWallRender = 0f;
        }

        /// <summary>Back to AUTO: the wall follows the latest incident again.</summary>
        public void SetWallAuto()
        {
            if (feeds.Count == 0 || wallTitle == null) return;
            WallAuto = true;
            switchTime = Time.unscaledTime;
            wallTitle.text = $"AUTO – CAM {WallRegion + 1:00} {feeds[WallRegion].zoneName.ToUpperInvariant()}";
        }

        /// <summary>The game reports a new (known) incident in this zone: AUTO switches the wall to it.</summary>
        public void OnIncident(int region)
        {
            if (WallAuto && region >= 0 && region < feeds.Count) SelectWall(region, false);
        }

        /// <summary>A warning sign (smoke, creaking tower): shown on the wall caption, the wall doesn't switch.</summary>
        public void SignalWarning(int region, string text)
        {
            string where = region >= 0 && region < feeds.Count ? $"  (CAM {region + 1:00})" : "";
            warningText = $"<color=#ffc400>WARNING: {text}{where}</color>";
            warningUntil = Time.unscaledTime + warningTime;
        }

        void UpdateWallAndMinimap(float clock)
        {
            float now = Time.unscaledTime;
            if (wallCamera != null)
            {
                bool due = now >= nextWallRender;
                wallCamera.enabled = due;
                if (due)
                {
                    nextWallRender = Mathf.Max(nextWallRender + 1f / Mathf.Max(1f, wallFps), now);
                    int minutes = (int)clock / 60, seconds = (int)clock % 60;
                    string dot = Mathf.Repeat(now, 1f) < 0.5f ? "<color=#ff3030>●</color>" : "<color=#00000000>●</color>";
                    string mode = WallAuto ? "<color=#ffb020>AUTO</color>" : "MANUAL";
                    var feed = feeds[WallRegion];
                    string caption = $"CAM {feed.region + 1:00} | {feed.zoneName.ToUpperInvariant()}  {dot} REC {minutes:00}:{seconds:00}   {mode}";
                    if (now < warningUntil && Mathf.Repeat(now, 0.8f) < 0.55f) caption += "\n" + warningText;
                    wallCaption.text = caption;
                    float age = now - switchTime;
                    wallTitle.color = new Color(1f, 1f, 1f, age < switchTitleTime ? Mathf.Clamp01((switchTitleTime - age) / 0.5f) : 0f);
                }
            }
            if (wallScreenMaterial != null)
            {
                // Switch effect: a short blank cut, then the picture comes in over-bright and settles.
                float age = now - switchTime;
                Color emission = age < 0.08f ? Color.black : Color.white * (1f + 2.5f * Mathf.Clamp01(1f - (age - 0.08f) / 0.35f));
                wallScreenMaterial.SetColor("_EmissionColor", emission);
            }
            if (minimapCamera != null)
            {
                bool due = now >= nextMinimapRender;
                minimapCamera.enabled = due;
                if (due) nextMinimapRender = Mathf.Max(nextMinimapRender + 1f / Mathf.Max(1f, minimapFps), now);
            }
        }

        void ClearWallAndMinimap()
        {
            if (wallTexture != null) { wallTexture.Release(); Destroy(wallTexture); }
            if (minimapTexture != null) { minimapTexture.Release(); Destroy(minimapTexture); }
            wallTexture = minimapTexture = null;
            wallCamera = minimapCamera = null; // destroyed with cctvRoot
            wallCaptionRenderers.Clear();
            if (wallScreen != null) RestoreScreen(wallScreen, "Video wall screen", "M_VW_Screen");
            if (minimapScreen != null) RestoreScreen(minimapScreen, "Minimap screen", "M_Minimap_Screen");
            wallScreenMaterial = minimapScreenMaterial = null;
            screens.Clear();
            Focused = false;
        }

        void RestoreScreen(Renderer renderer, string ownedName, string shared)
        {
            var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] != null && mats[i].name == ownedName) mats[i] = RoomMaterial(shared);
            renderer.sharedMaterials = mats;
        }
    }
}
