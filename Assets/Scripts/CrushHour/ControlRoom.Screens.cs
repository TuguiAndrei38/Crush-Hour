using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CrushHour
{
    /// <summary>
    /// Step 4: reaching the venue through the screens. A ray from the operator hits a screen (desk monitor, video wall
    /// or minimap) → the point on the picture (UV) → the ray of the camera that films it → the ground. The game feeds
    /// that into its usual click logic. E on a screen = focus (the view moves in front of it and the cursor is free);
    /// Esc, right click or E again = back to free look.
    /// </summary>
    public partial class ControlRoom
    {
        public enum ScreenKind { DeskMonitor, VideoWall, Minimap }

        /// <summary>A screen's picture surface in world space (static: the room doesn't move).</summary>
        public class ScreenSurface
        {
            public ScreenKind kind;
            public int region = -1;          // desk monitors: their zone
            public Renderer renderer;
            public Camera source;            // the camera whose picture is on it
            public Vector3[] triangles;      // world positions, 3 per triangle
            public Vector2[] uvs;
            public Vector3 center, normal, up;
            public float width, height;
        }

        const float FocusMoveTime = 0.25f;
        readonly List<ScreenSurface> screens = new();
        float focusBlend;
        Vector3 focusPos;
        Quaternion focusRot;
        bool warnedUnreadable;

        /// <summary>In focus on a screen: cursor free, clicks go to the picture.</summary>
        public bool Focused { get; private set; }
        public ScreenSurface FocusedScreen { get; private set; }
        /// <summary>The operator can point at things (free look or focus).</summary>
        public bool PointerActive => lookEnabled && OperatorCamera != null;
        /// <summary>Where the pointer is, in screen pixels: the crosshair (centre) in free look, the mouse in focus.</summary>
        public Vector2 PointerScreen { get; private set; }
        /// <summary>This frame's left click was used by the room (desk monitor → video wall); the game ignores it.</summary>
        public bool ClickTakenByRoom { get; private set; }
        /// <summary>This frame's Esc was used by the room (leaving focus); the game ignores it.</summary>
        public bool EscapeTakenByRoom { get; private set; }
        /// <summary>The pointer is on the video wall: digits pick cameras there, not tools.</summary>
        public bool PointerOnWall { get; private set; }

        void BuildScreenSurfaces()
        {
            screens.Clear();
            foreach (var feed in feeds)
                if (feed.monitor != null) AddScreen(ScreenKind.DeskMonitor, feed.region, feed.monitor, $"Monitor screen {feed.region + 1}", feed.camera);
            if (wallScreen != null) AddScreen(ScreenKind.VideoWall, -1, wallScreen, "Video wall screen", wallCamera);
            if (minimapScreen != null) AddScreen(ScreenKind.Minimap, -1, minimapScreen, "Minimap screen", minimapCamera);
            BuildScreenOverlays();
        }

        void AddScreen(ScreenKind kind, int region, Renderer renderer, string materialName, Camera source)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) return;
            if (!mesh.isReadable)
            {
                if (!warnedUnreadable)
                    Debug.LogWarning("Crush Hour: the control room models aren't readable, so clicks through the screens can't work. " +
                                     "Reimport Assets/Models/ControlRoom (ControlRoomModelImport turns Read/Write on).");
                warnedUnreadable = true;
                return;
            }
            var mats = renderer.sharedMaterials;
            int sub = -1;
            for (int i = 0; i < mats.Length && i < mesh.subMeshCount; i++)
                if (mats[i] != null && mats[i].name == materialName) sub = i;
            if (sub < 0) return;

            var verts = mesh.vertices;
            var uv = mesh.uv;
            var tris = mesh.GetTriangles(sub);
            if (uv == null || uv.Length != verts.Length || tris.Length < 3) return;
            var m = renderer.transform.localToWorldMatrix;
            var surface = new ScreenSurface
            {
                kind = kind, region = region, renderer = renderer, source = source,
                triangles = new Vector3[tris.Length], uvs = new Vector2[tris.Length],
            };
            for (int i = 0; i < tris.Length; i++)
            {
                surface.triangles[i] = m.MultiplyPoint3x4(verts[tris[i]]);
                surface.uvs[i] = uv[tris[i]];
            }

            // Frame of the picture from the first triangle: dP/du (right) and dP/dv (up).
            Vector3 p0 = surface.triangles[0], p1 = surface.triangles[1], p2 = surface.triangles[2];
            Vector2 t0 = surface.uvs[0], t1 = surface.uvs[1], t2 = surface.uvs[2];
            Vector3 e1 = p1 - p0, e2 = p2 - p0;
            Vector2 d1 = t1 - t0, d2 = t2 - t0;
            float det = d1.x * d2.y - d2.x * d1.y;
            if (Mathf.Abs(det) < 1e-8f) return;
            Vector3 dPdu = (e1 * d2.y - e2 * d1.y) / det, dPdv = (e2 * d1.x - e1 * d2.x) / det;
            surface.width = dPdu.magnitude;
            surface.height = dPdv.magnitude;
            surface.up = dPdv.normalized;
            surface.center = p0 + (0.5f - t0.x) * dPdu + (0.5f - t0.y) * dPdv;
            var normal = Vector3.Cross(dPdu, dPdv).normalized;
            if (Vector3.Dot(normal, Root.TransformPoint(EyePos) - surface.center) < 0f) normal = -normal;   // towards the operator
            surface.normal = normal;
            screens.Add(surface);
        }

        /// <summary>The nearest screen a ray hits, with the picture coordinates (0..1) of the hit.</summary>
        public bool HitScreen(Ray ray, out ScreenSurface hit, out Vector2 uv)
        {
            hit = null;
            uv = default;
            float best = float.MaxValue;
            foreach (var s in screens)
            {
                if (!s.renderer.bounds.IntersectRay(ray)) continue;
                for (int i = 0; i + 2 < s.triangles.Length; i += 3)
                {
                    if (!RayTriangle(ray, s.triangles[i], s.triangles[i + 1], s.triangles[i + 2], out float d, out float b1, out float b2) || d >= best) continue;
                    best = d;
                    hit = s;
                    uv = s.uvs[i] * (1f - b1 - b2) + s.uvs[i + 1] * b1 + s.uvs[i + 2] * b2;
                }
            }
            return hit != null;
        }

        static bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance, out float u, out float v)
        {
            distance = u = v = 0f;
            Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(ray.direction, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-9f) return false;
            float inv = 1f / det;
            Vector3 t = ray.origin - a;
            u = Vector3.Dot(t, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(t, e1);
            v = Vector3.Dot(ray.direction, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            distance = Vector3.Dot(e2, q) * inv;
            return distance > 0f;
        }

        /// <summary>
        /// The venue ray behind a point on the operator's screen (pixels): through whatever screen is there, from the
        /// camera that films it. False when the point isn't on a screen.
        /// </summary>
        public bool TryGroundRay(Vector2 screenPoint, out Ray groundRay)
        {
            groundRay = default;
            if (OperatorCamera == null || (!Focused && boardHover != null)) return false;   // pointing at the board, not a screen
            if (!HitScreen(OperatorCamera.ScreenPointToRay(screenPoint), out var s, out var uv) || s.source == null) return false;
            groundRay = s.source.ViewportPointToRay(new Vector3(uv.x, uv.y, 0f));
            return true;
        }

        /// <summary>
        /// Called by the game at the start of its input handling, every frame: pointer, focus, desk-monitor clicks and
        /// camera keys. The game then reads <see cref="ClickTakenByRoom"/>, <see cref="EscapeTakenByRoom"/> and
        /// <see cref="PointerOnWall"/>.
        /// </summary>
        public void UpdateRoomInput()
        {
            ClickTakenByRoom = EscapeTakenByRoom = PointerOnWall = false;
            boardHover = null;
            if (!PointerActive)
            {
                if (Focused) ExitFocus();
                return;
            }
            if (radioOpen) return;   // the radio wheels have the mouse
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            PointerScreen = Focused && mouse != null ? mouse.position.ReadValue() : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            var ray = OperatorCamera.ScreenPointToRay(PointerScreen);
            bool onScreen = HitScreen(ray, out var screen, out _);
            PointerOnWall = onScreen && screen.kind == ScreenKind.VideoWall;

            if (Focused)
            {
                bool leave = (keyboard != null && (keyboard.escapeKey.wasPressedThisFrame || keyboard.eKey.wasPressedThisFrame))
                             || (mouse != null && mouse.rightButton.wasPressedThisFrame);
                if (leave)
                {
                    EscapeTakenByRoom = keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
                    ExitFocus();
                }
            }
            else
            {
                // The exits board first (it sits in front of the minimap).
                BoardInput(ray, mouse != null && mouse.leftButton.wasPressedThisFrame);
                if (boardHover != null) return;
                if (keyboard != null && keyboard.eKey.wasPressedThisFrame && onScreen) EnterFocus(screen);
                // Free look: clicking a desk monitor puts its zone on the video wall (step 3).
                if (mouse != null && mouse.leftButton.wasPressedThisFrame && onScreen && screen.kind == ScreenKind.DeskMonitor)
                {
                    SelectWall(screen.region, true);
                    ClickTakenByRoom = true;
                }
            }

            if (PointerOnWall && keyboard != null)
            {
                for (int i = 0; i < 9 && i < feeds.Count; i++)
                    if (keyboard[Key.Digit1 + i].wasPressedThisFrame || keyboard[Key.Numpad1 + i].wasPressedThisFrame) SelectWall(i, true);
                if (keyboard.digit0Key.wasPressedThisFrame || keyboard.numpad0Key.wasPressedThisFrame) SetWallAuto();
            }
        }

        /// <summary>Moves the view in front of the screen so the picture fills most of it, and frees the cursor.</summary>
        public void EnterFocus(ScreenSurface screen)
        {
            if (screen == null || OperatorCamera == null) return;
            float tan = Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            float distance = Mathf.Max(screen.height * 0.5f / tan, screen.width * 0.5f / (tan * OperatorCamera.aspect)) * 1.12f;
            focusPos = screen.center + screen.normal * distance;
            focusRot = Quaternion.LookRotation(-screen.normal, screen.up);
            FocusedScreen = screen;
            Focused = true;
        }

        public void ExitFocus()
        {
            Focused = false;
            FocusedScreen = null;
        }
    }
}
