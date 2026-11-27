using UnityEngine;

namespace CrushHour
{
    public partial class CrushHourGame
    {
        const float ArrivalShotSeconds = 5f;

        readonly struct ArrivalShot
        {
            readonly Vector3 start, end, lookStart, lookEnd;
            readonly float verticalFov;

            public ArrivalShot(Vector3 start, Vector3 end, Vector3 lookStart, Vector3 lookEnd, float verticalFov)
            {
                this.start = start;
                this.end = end;
                this.lookStart = lookStart;
                this.lookEnd = lookEnd;
                this.verticalFov = verticalFov;
            }

            public void Frame(Camera camera, float progress)
            {
                float t = Mathf.SmoothStep(0f, 1f, progress);
                Vector3 position = Vector3.Lerp(start, end, t);
                Vector3 subject = Vector3.Lerp(lookStart, lookEnd, t);
                camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(subject - position, Vector3.up));
                camera.fieldOfView = verticalFov;
            }
        }

        readonly ArrivalShot[] arrivalShots = new ArrivalShot[3];
        Behaviour arrivalViewOwner;
        Pose viewBeforeArrivals;
        float fovBeforeArrivals, farClipBeforeArrivals;
        int maskBeforeArrivals;
        bool orthoBeforeArrivals, viewOwnerWasEnabled, arrivalCameraActive;

        void BeginArrivalCamera()
        {
            if (arrivalCameraActive || cam == null) return;
            arrivalCameraActive = true;
            viewBeforeArrivals = new Pose(cam.transform.position, cam.transform.rotation);
            fovBeforeArrivals = cam.fieldOfView;
            farClipBeforeArrivals = cam.farClipPlane;
            maskBeforeArrivals = cam.cullingMask;
            orthoBeforeArrivals = cam.orthographic;

            // There is one display camera and listener for the entire sequence. The normal controller
            // writes its transform in LateUpdate, even when player input is locked.
            arrivalViewOwner = InControlRoom ? (Behaviour)controlRoom : cameraRig;
            viewOwnerWasEnabled = arrivalViewOwner.enabled;
            arrivalViewOwner.enabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            cam.orthographic = false;
            cam.farClipPlane = 600f; // the landscape's horizon reaches ~540 m (fog hides the rest)
            int venueMask = InControlRoom ? ~0 : maskBeforeArrivals;
            cam.cullingMask = venueMask & ~((1 << ControlRoom.RoomLayer) | (1 << ControlRoom.CaptionLayer)
                | (1 << ControlRoom.MinimapLayer) | (1 << ControlRoom.CrowdSimpleLayer));

            ArrivalShot interior = InteriorArrivalShot();
            Door primary = venue.doors.Find(d => d.gate == null);
            Door secondary = venue.doors.Find(d => d.gate == null && d != primary);
            arrivalShots[0] = primary != null ? GateArrivalShot(primary, false) : interior;
            arrivalShots[1] = secondary != null ? GateArrivalShot(secondary, true) : interior;
            arrivalShots[2] = interior;
            UpdateArrivalCamera(0f);
        }

        void UpdateArrivalCamera(float normalizedTime)
        {
            if (!arrivalCameraActive) return;
            float progress = Mathf.Clamp01(normalizedTime) * arrivalShots.Length;
            int shot = Mathf.Min((int)progress, arrivalShots.Length - 1);
            arrivalShots[shot].Frame(cam, progress - shot);
        }

        // Mask the time jump at each edit. The hold also covers frames that straddle a cut.
        static float ArrivalShotFade(float normalizedTime)
        {
            float seconds = Mathf.Clamp01(normalizedTime) * (3f * ArrivalShotSeconds);
            float within = Mathf.Repeat(seconds, ArrivalShotSeconds);
            float distanceToCut = Mathf.Min(within, ArrivalShotSeconds - within);
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.12f, 0.5f, distanceToCut));
        }

        void EndArrivalCamera()
        {
            if (!arrivalCameraActive) return;
            arrivalCameraActive = false;
            if (cam != null)
            {
                cam.transform.SetPositionAndRotation(viewBeforeArrivals.position, viewBeforeArrivals.rotation);
                cam.fieldOfView = fovBeforeArrivals;
                cam.farClipPlane = farClipBeforeArrivals;
                cam.cullingMask = maskBeforeArrivals;
                cam.orthographic = orthoBeforeArrivals;
            }
            if (arrivalViewOwner != null) arrivalViewOwner.enabled = viewOwnerWasEnabled;
        }

        static ArrivalShot GateArrivalShot(Door door, bool sideView)
        {
            Vector3 center = new(door.Center.x, 0f, door.Center.y);
            bool horizontal = door.rect.width >= door.rect.height;
            Vector3 inward = horizontal
                ? (center.z < FestivalVenue.Height * 0.5f ? Vector3.forward : Vector3.back)
                : (center.x < FestivalVenue.Width * 0.5f ? Vector3.right : Vector3.left);
            Vector3 across = Vector3.Cross(Vector3.up, inward);
            Vector3 Point(float sideways, float height, float depth) =>
                center + across * sideways + Vector3.up * height + inward * depth;

            if (sideView)
                return new ArrivalShot(Point(-6.5f, 5.6f, -2.1f), Point(-8f, 6.8f, 0.2f),
                    Point(0f, 0.9f, 1.6f), Point(-0.4f, 0.9f, 4f), 46f);

            // A few metres above the threshold, looking in the direction of travel. The
            // ground extends only three metres outside the fence, so keep the lens over it.
            float height = 3.8f + door.Width * 0.18f;
            return new ArrivalShot(Point(-2.9f, height, -3.1f), Point(-1.4f, height + 0.8f, -2.8f),
                Point(0f, 0.85f, 0.45f), Point(0.4f, 0.85f, 3.2f), 53f);
        }

        ArrivalShot InteriorArrivalShot()
        {
            string landmark = venue.map switch
            {
                VenueMap.CityStadium => "Playing Field",
                VenueMap.MetroConcourse => "Metro Train",
                _ => "Main Stage",
            };
            Landmark subject = venue.landmarks.Find(l => l.name == landmark);
            Vector2 point = subject != null ? subject.rect.center : new Vector2(W * 0.5f, H * 0.5f);
            Vector3 At(float x, float height, float z) => new(point.x + x, height, point.y + z);

            return venue.map switch
            {
                // The end stands fill the foreground, with the playing field beyond them.
                VenueMap.CityStadium => new ArrivalShot(At(-17f, 13.5f, -28f), At(-13f, 16.5f, -28.5f),
                    At(-6f, 1f, -15f), At(0f, 1f, -12f), 48f),
                // Track along the platform, with the passengers and the side of the train in the same shot.
                VenueMap.MetroConcourse => new ArrivalShot(At(-11f, 8.5f, 8f), At(-6.5f, 10f, 10.5f),
                    At(3f, 1f, 4.6f), At(8f, 1f, 5.8f), 44f),
                // Lift over the audience toward the stage, clear of the sound desk and lighting towers.
                _ => new ArrivalShot(At(-15f, 10.5f, -25f), At(-10f, 13f, -27f),
                    At(-2f, 1.2f, -9f), At(2f, 1.6f, -9f), 45f),
            };
        }
    }
}
