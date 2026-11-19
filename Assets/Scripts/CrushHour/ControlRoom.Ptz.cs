using UnityEngine;
using UnityEngine.InputSystem;

namespace CrushHour
{
    /// <summary>
    /// Week 8: PTZ CCTV cameras. In focus (E) on a desk monitor or on the video wall, the camera of that zone can be
    /// driven: right mouse held + moved = pan/tilt, mouse wheel = zoom, R = back to the resting pose (0.4 s). After
    /// <see cref="ptzReturnTime"/> seconds untouched a moved camera goes back by itself. The pole's camera housing turns
    /// with it, the wall follows the camera of the zone it shows, and clicks through the picture use the camera as it
    /// is pointed now. Task markers (layer <see cref="TaskMarkerLayer"/>) show only on the focused camera, zoomed in
    /// more than ×<see cref="taskMarkerZoom"/>.
    /// </summary>
    public partial class ControlRoom
    {
        /// <summary>Camera-task hints (e.g. the fight marker): drawn only by the focused camera while zoomed in.</summary>
        public const int TaskMarkerLayer = 24;

        [Header("PTZ cameras")]
        [Tooltip("Degrees per pixel of mouse movement while turning a camera (slower when zoomed in).")]
        public float ptzSensitivity = 0.15f;
        public float ptzMaxPan = 60f, ptzMinPitch = 10f, ptzMaxPitch = 70f, ptzMinFov = 15f;
        [Tooltip("A moved camera goes back to its resting pose after this many seconds untouched.")]
        public float ptzReturnTime = 20f;
        [Tooltip("Task markers show on the focused camera only when it is zoomed in more than this.")]
        public float taskMarkerZoom = 2f;

        const float PtzResetTime = 0.4f, PtzDragThreshold = 6f;
        bool rightHeld;
        float rightTravel;
        float wallFovShown = -1f;

        void InitPtz(Feed feed)
        {
            var forward = feed.camera.transform.forward;
            feed.baseYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            feed.basePitch = Mathf.Clamp(Mathf.Asin(-forward.y) * Mathf.Rad2Deg, ptzMinPitch, ptzMaxPitch);
            feed.baseFov = feed.camera.fieldOfView;
            feed.pan = 0f;
            feed.pitch = feed.basePitch;
            feed.fov = feed.fovTarget = feed.baseFov;
            feed.resetting = false;
            feed.lastTouch = -999f;
            ApplyPtz(feed);
        }

        /// <summary>The feed whose camera is on the focused screen (desk monitor, or the wall's current zone), or null.</summary>
        public Feed FocusedFeed()
        {
            if (!Focused || FocusedScreen == null) return null;
            if (FocusedScreen.kind == ScreenKind.DeskMonitor && FocusedScreen.region >= 0 && FocusedScreen.region < feeds.Count) return feeds[FocusedScreen.region];
            if (FocusedScreen.kind == ScreenKind.VideoWall && WallRegion >= 0 && WallRegion < feeds.Count) return feeds[WallRegion];
            return null;
        }

        /// <summary>
        /// From <see cref="ProcessPointer"/> while focused: drives the camera. True when the right button was released
        /// without moving (a plain right click), which leaves focus as before.
        /// </summary>
        bool PtzInput(Mouse mouse, Keyboard keyboard)
        {
            var feed = FocusedFeed();
            bool plainRightClick = false;
            if (mouse != null)
            {
                if (mouse.rightButton.wasPressedThisFrame)
                {
                    rightHeld = true;
                    rightTravel = 0f;
                }
                if (rightHeld && mouse.rightButton.isPressed)
                {
                    var delta = mouse.delta.ReadValue();
                    rightTravel += delta.magnitude;
                    if (rightTravel > PtzDragThreshold && feed != null)
                    {
                        float k = ptzSensitivity * (feed.fov / Mathf.Max(1f, feed.baseFov));
                        feed.pan = Mathf.Clamp(feed.pan + delta.x * k, -ptzMaxPan, ptzMaxPan);
                        feed.pitch = Mathf.Clamp(feed.pitch - delta.y * k, ptzMinPitch, ptzMaxPitch);
                        Touch(feed);
                    }
                }
                if (rightHeld && !mouse.rightButton.isPressed)
                {
                    rightHeld = false;
                    plainRightClick = rightTravel <= PtzDragThreshold;
                }
                float scroll = mouse.scroll.ReadValue().y;
                if (feed != null && Mathf.Abs(scroll) > 0.01f)
                {
                    feed.fovTarget = Mathf.Clamp(feed.fovTarget * (scroll > 0f ? 0.86f : 1f / 0.86f), Mathf.Min(ptzMinFov, feed.baseFov), feed.baseFov);
                    Touch(feed);
                }
            }
            if (feed != null && keyboard != null && keyboard.rKey.wasPressedThisFrame) StartPtzReset(feed);
            return plainRightClick;
        }

        void Touch(Feed feed)
        {
            feed.resetting = false;
            feed.lastTouch = Time.unscaledTime;
            feed.captionTick = -1;
        }

        void StartPtzReset(Feed feed)
        {
            if (!feed.Moved && feed.fovTarget >= feed.baseFov - 0.3f) return;
            feed.resetting = true;
            feed.resetT = 0f;
            feed.resetFromPan = feed.pan;
            feed.resetFromPitch = feed.pitch;
            feed.resetFromFov = feed.fov;
            feed.captionTick = -1;
        }

        /// <summary>Every frame (LateUpdate): reset animations, auto-return, zoom easing, cameras, housings, wall copy, task markers.</summary>
        void UpdatePtz()
        {
            if (feeds.Count == 0) return;
            float dt = Time.unscaledDeltaTime, now = Time.unscaledTime;
            var focusedFeed = FocusedFeed();
            foreach (var feed in feeds)
            {
                feed.live = feed == focusedFeed;
                if (!feed.resetting && feed.Moved && feed != focusedFeed && now - feed.lastTouch > ptzReturnTime) StartPtzReset(feed);
                if (feed.resetting)
                {
                    feed.resetT += dt / PtzResetTime;
                    float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(feed.resetT));
                    feed.pan = Mathf.Lerp(feed.resetFromPan, 0f, t);
                    feed.pitch = Mathf.Lerp(feed.resetFromPitch, feed.basePitch, t);
                    feed.fov = feed.fovTarget = Mathf.Lerp(feed.resetFromFov, feed.baseFov, t);
                    if (feed.resetT >= 1f)
                    {
                        feed.resetting = false;
                        feed.captionTick = -1;
                    }
                }
                else if (Mathf.Abs(feed.fov - feed.fovTarget) > 0.01f)
                {
                    feed.fov = Mathf.Lerp(feed.fov, feed.fovTarget, 1f - Mathf.Exp(-10f * dt));
                    if (Mathf.Abs(feed.fov - feed.fovTarget) < 0.02f) feed.fov = feed.fovTarget;
                }
                ApplyPtz(feed);

                // Task markers: only on the camera being looked at closely.
                bool markers = feed == focusedFeed && FocusedScreen != null && FocusedScreen.kind == ScreenKind.DeskMonitor && feed.Zoom > taskMarkerZoom;
                SetLayerBit(feed.camera, TaskMarkerLayer, markers);
            }

            // The video wall shows its zone's camera as it is pointed now.
            if (wallCamera != null && WallRegion >= 0 && WallRegion < feeds.Count)
            {
                var source = feeds[WallRegion];
                wallCamera.transform.SetPositionAndRotation(source.camera.transform.position, source.camera.transform.rotation);
                if (!Mathf.Approximately(wallFovShown, source.camera.fieldOfView))
                {
                    wallFovShown = wallCamera.fieldOfView = source.camera.fieldOfView;
                    LayoutCaption(wallCaption, wallCamera, new Vector2(-0.95f, 0.93f), 0.045f);
                    LayoutCaption(wallTitle, wallCamera, Vector2.zero, 0.09f);
                    LayoutWallEvening();
                }
                bool wallMarkers = source == focusedFeed && FocusedScreen != null && FocusedScreen.kind == ScreenKind.VideoWall && source.Zoom > taskMarkerZoom;
                SetLayerBit(wallCamera, TaskMarkerLayer, wallMarkers);
                if (source.live || source.resetting) wallCaptionTick = -1;
            }
        }

        void ApplyPtz(Feed feed)
        {
            var rotation = Quaternion.Euler(feed.pitch, feed.baseYaw + feed.pan, 0f);
            var t = feed.camera.transform;
            if (t.rotation != rotation) t.rotation = rotation;
            if (feed.housing != null && feed.housing.rotation != rotation) feed.housing.rotation = rotation;
            if (!Mathf.Approximately(feed.camera.fieldOfView, feed.fov))
            {
                feed.camera.fieldOfView = feed.fov;
                LayoutCaption(feed.caption, feed.camera, new Vector2(-0.95f, 0.93f), 0.055f);
            }
        }

        static void SetLayerBit(Camera cam, int layer, bool on)
        {
            int mask = on ? cam.cullingMask | (1 << layer) : cam.cullingMask & ~(1 << layer);
            if (mask != cam.cullingMask) cam.cullingMask = mask;
        }
    }
}
