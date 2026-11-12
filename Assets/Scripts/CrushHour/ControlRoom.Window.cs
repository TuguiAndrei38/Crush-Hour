using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    /// <summary>
    /// The control room as a VIP box at the stadium: it stands just outside the venue's low-z edge, raised, and its back
    /// wall (behind the operator) has a real floor-to-ceiling window onto the venue. The CCTV cameras never draw the
    /// room (they cull its layer). The operator camera draws the venue only while the window is in view.
    /// </summary>
    public partial class ControlRoom
    {
        [Header("VIP box")]
        [Tooltip("Put the room at the stadium (a real window onto the venue). Off = the old place far away, no window.")]
        public bool vipBox = true;
        [Tooltip("Height of the room's floor above the ground (m).")]
        public float boxHeight = 9f;
        [Tooltip("Distance from the window to the venue's edge (m).")]
        public float boxSetback = 4f;

        // The opening in the back wall (room-local, wall from z 9 to 9.2): right of the door, nearly floor to ceiling.
        const float WindowX0 = -2.6f, WindowX1 = 4.4f, WindowY0 = 0.2f, WindowY1 = 4.6f, BackWallZ = 9f, WallThickness = 0.2f;
        bool hasWindow, venueInView, viewMaskSet;
        Bounds windowBounds;
        readonly Plane[] viewPlanes = new Plane[6];

        Vector3 RoomPlace() => vipBox
            ? new Vector3(FestivalVenue.Width * 0.5f, boxHeight, -(boxSetback + BackWallZ + WallThickness))
            : origin;

        /// <summary>Cuts the opening out of the back wall mesh and rebuilds the wall around it. Once, in Build.</summary>
        void SetupWindow()
        {
            if (!vipBox || roomShell == null) return;
            bool cut = false;
            foreach (var filter in roomShell.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                var renderer = filter.GetComponent<Renderer>();
                if (mesh == null || renderer == null) continue;
                if (!mesh.isReadable)
                {
                    Debug.LogWarning("Crush Hour: the room model isn't readable, so the window can't be cut (reimport Assets/Models/ControlRoom).");
                    return;
                }
                var verts = mesh.vertices;
                var local = new Vector3[verts.Length];
                for (int i = 0; i < verts.Length; i++) local[i] = Root.InverseTransformPoint(filter.transform.TransformPoint(verts[i]));
                var mats = renderer.sharedMaterials;
                Mesh copy = null;
                for (int sub = 0; sub < mesh.subMeshCount && sub < mats.Length; sub++)
                {
                    string name = mats[sub] != null ? mats[sub].name : "";
                    if (!name.StartsWith("M_Room_Wall") || name.StartsWith("M_Room_WallDark")) continue;
                    var tris = mesh.GetTriangles(sub);
                    var kept = new List<int>(tris.Length);
                    for (int k = 0; k + 2 < tris.Length; k += 3)
                    {
                        // The back wall's big piece (right of the door): every vertex on z 9..9.2 and x -3.6..6.
                        if (InBackWall(local[tris[k]]) && InBackWall(local[tris[k + 1]]) && InBackWall(local[tris[k + 2]])) continue;
                        kept.Add(tris[k]); kept.Add(tris[k + 1]); kept.Add(tris[k + 2]);
                    }
                    if (kept.Count == tris.Length) continue;
                    copy ??= Instantiate(mesh);
                    copy.SetTriangles(kept, sub);
                    cut = true;
                }
                if (copy != null) filter.sharedMesh = copy;
            }
            if (!cut)
            {
                Debug.LogWarning("Crush Hour: couldn't find the back wall in the room model; no window.");
                return;
            }

            var holder = new GameObject("Back Wall With Window").transform;
            holder.SetParent(Root, false);
            var wall = RoomMaterial("M_Room_Wall");
            float zc = BackWallZ + WallThickness * 0.5f;
            const float roomTop = 6.5f, xLeft = -3.6f, xRight = 6f;
            WallPiece(holder, wall, xLeft, WindowX0, 0f, roomTop, zc);            // between the door and the window
            WallPiece(holder, wall, WindowX1, xRight, 0f, roomTop, zc);           // right of the window
            WallPiece(holder, wall, WindowX0, WindowX1, 0f, WindowY0, zc);        // sill
            WallPiece(holder, wall, WindowX0, WindowX1, WindowY1, roomTop, zc);   // above

            var frame = RoomMaterial("M_Room_LampHousing");
            const float bar = 0.07f, d = WallThickness + 0.06f;
            float cx = (WindowX0 + WindowX1) * 0.5f, cy = (WindowY0 + WindowY1) * 0.5f, w = WindowX1 - WindowX0, h = WindowY1 - WindowY0;
            Prim(holder, PrimitiveType.Cube, frame, new Vector3(cx, WindowY0 + bar * 0.5f, zc), new Vector3(w, bar, d), Quaternion.identity);
            Prim(holder, PrimitiveType.Cube, frame, new Vector3(cx, WindowY1 - bar * 0.5f, zc), new Vector3(w, bar, d), Quaternion.identity);
            for (int i = 0; i <= 4; i++)   // jambs and three mullions
            {
                float x = Mathf.Lerp(WindowX0 + bar * 0.5f, WindowX1 - bar * 0.5f, i / 4f);
                Prim(holder, PrimitiveType.Cube, frame, new Vector3(x, cy, zc), new Vector3(i == 0 || i == 4 ? bar : 0.04f, h, d), Quaternion.identity);
            }
            // a handrail in front of the glass
            Prim(holder, PrimitiveType.Cube, RoomMaterial("M_Door_Metal"), new Vector3(cx, 1.05f, BackWallZ - 0.12f), new Vector3(w, 0.04f, 0.04f), Quaternion.identity);

            windowBounds = new Bounds(Root.TransformPoint(new Vector3(cx, cy, zc)), new Vector3(w, h, 0.4f));
            hasWindow = true;
        }

        static bool InBackWall(Vector3 p) => p.z >= BackWallZ - 0.01f && p.x >= -3.61f && p.x <= 6.01f;

        void WallPiece(Transform parent, Material material, float x0, float x1, float y0, float y1, float zc) =>
            Prim(parent, PrimitiveType.Cube, material, new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, zc),
                new Vector3(x1 - x0, y1 - y0, WallThickness), Quaternion.identity);

        /// <summary>
        /// Every frame after the view is placed: the operator camera draws the venue (and the crowd) only while the
        /// window is in view; facing the desk it draws just the room, which is much cheaper.
        /// </summary>
        void UpdateWindowView()
        {
            if (OperatorCamera == null) return;
            bool see = false;
            if (hasWindow)
            {
                GeometryUtility.CalculateFrustumPlanes(OperatorCamera, viewPlanes);
                see = GeometryUtility.TestPlanesAABB(viewPlanes, windowBounds);
            }
            if (see == venueInView && viewMaskSet) return;
            viewMaskSet = true;
            venueInView = see;
            OperatorCamera.cullingMask = see
                ? ~((1 << CaptionLayer) | (1 << MinimapLayer) | (1 << CrowdSimpleLayer))
                : 1 << RoomLayer;
            OperatorCamera.farClipPlane = see ? 400f : 60f;
        }

        /// <summary>The pointer ray goes out through the window opening (so it can be used directly on the venue).</summary>
        bool ThroughWindow(Ray ray)
        {
            if (!hasWindow) return false;
            var o = Root.InverseTransformPoint(ray.origin);
            var dir = Root.InverseTransformDirection(ray.direction);
            if (dir.z <= 1e-4f || o.z >= BackWallZ) return false;
            float k = (BackWallZ - o.z) / dir.z;
            var p = o + dir * k;
            return p.x > WindowX0 && p.x < WindowX1 && p.y > WindowY0 && p.y < WindowY1;
        }
    }
}
