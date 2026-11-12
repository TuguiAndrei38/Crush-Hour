using System.Collections.Generic;
using UnityEngine;

namespace CrushHour
{
    public partial class CrushHourGame
    {
        // Visual replacements only: FestivalVenue still owns every obstacle and walkable cell.
        readonly Dictionary<string, GameObject> festivalPrefabs = new();
        readonly Dictionary<Landmark, float> festivalLandmarkHeights = new();
        static readonly Color FestivalDeck = new(0.083f, 0.087f, 0.098f);
        static readonly Color FestivalWood = new(0.24f, 0.165f, 0.11f);
        static readonly Color FestivalTrim = new(0.93f, 0.49f, 0.11f);

        GameObject FestivalPrefab(string name)
        {
            if (festivalPrefabs.TryGetValue(name, out var prefab) && prefab != null) return prefab;
            prefab = Resources.Load<GameObject>("Festival/" + name);
            if (prefab != null) festivalPrefabs[name] = prefab;
            return prefab;
        }

        bool TryBuildFestivalLandmark(Landmark landmark, out GameObject view)
        {
            view = null;
            if (selectedMap != VenueMap.SummerFestival) return false;
            string primary;
            switch (landmark.name)
            {
                case "Main Stage": primary = "SmallStage"; break;
                case "Sound Desk":
                case "Bar Tent":
                case "Dance Tent": primary = "EventCanopy"; break;
                case "Light Tower": primary = "LightTower"; break;
                case "Food Court": primary = "FoodStall01"; break;
                case "Toilets": primary = "PortableToilet"; break;
                case "Merch Tent": primary = "WristbandBooth"; break;
                case "Generator": primary = "Generator"; break;
                default: return false;
            }
            if (FestivalPrefab(primary) == null) return false;

            view = new GameObject(landmark.name);
            var root = view.transform;
            root.SetParent(world, false);
            root.localPosition = new Vector3(landmark.rect.center.x, 0f, landmark.rect.center.y);
            var size = landmark.rect.size;
            switch (landmark.name)
            {
                case "Main Stage":
                    // A broad apron and speaker wings fill the original plot without stretching the roof.
                    FestivalPlatform(root, size, 0.32f);
                    FestivalProp("SmallStage", root, new Vector3(0f, 0.32f, 0.2f), new Vector3(15f, 8f, size.y - 0.8f), 180f, 1.15f);
                    foreach (float x in new[] { -11f, -8.8f, 8.8f, 11f })
                        FestivalProp("Speakers", root, new Vector3(x, 0.32f, 0.9f), new Vector3(1.7f, 3.1f, 1.5f), 180f, 1.2f);
                    break;
                case "Sound Desk":
                    FestivalPlatform(root, size, 0.1f);
                    FestivalProp("EventCanopy", root, new Vector3(0f, 0.1f, 0f), new Vector3(size.x - 0.4f, 4f, size.y - 0.4f), 180f);
                    FestivalConsole(root, new Vector3(0f, 0.1f, 0.4f), 3f);
                    foreach (float side in new[] { -1f, 1f })
                        FestivalProp("Speakers", root, new Vector3(side * 2.05f, 0.1f, 0.45f), new Vector3(0.7f, 1.5f, 0.75f), 180f);
                    break;
                case "Light Tower":
                    FestivalProp("LightTower", root, Vector3.zero, new Vector3(size.x - 0.2f, 6f, size.y - 0.2f), 180f);
                    break;
                case "Food Court":
                    int index = (landmark.rect.y == 17 ? 3 : 0) + (landmark.rect.x - 10) / 7;
                    string[] stalls = { "FoodStall01", "FoodTruckBurger", "FoodStall02", "FoodStall03", "FoodTruckDonut", "FoodStall01" };
                    string stall = stalls[Mathf.Clamp(index, 0, stalls.Length - 1)];
                    if (FestivalPrefab(stall) == null) stall = "FoodStall0" + (index % 3 + 1);
                    FestivalProp(stall, root, Vector3.zero, new Vector3(size.x - 0.25f, 4f, size.y - 0.2f),
                        index < 3 ? 0f : 180f);
                    break;
                case "Bar Tent":
                    FestivalPlatform(root, size, 0.12f);
                    FestivalProp("EventCanopy", root, new Vector3(0f, 0.12f, 0f), new Vector3(size.x - 0.4f, 6.6f, size.y - 0.4f), 180f);
                    FestivalPart(root, "Bar counter", new Vector3(0f, 0.68f, -2.1f), new Vector3(8.2f, 1.1f, 0.75f), FestivalWood);
                    FestivalPart(root, "Bar top", new Vector3(0f, 1.27f, -2.1f), new Vector3(8.4f, 0.12f, 0.9f), FestivalDeck);
                    break;
                case "Toilets":
                    // Back-to-back rows: doors face out, with no decorative obstacles in crowd routes.
                    for (int row = 0; row < 2; row++)
                        for (int column = 0; column < 6; column++)
                            FestivalProp("PortableToilet", root, new Vector3((column - 2.5f) * 1.25f, 0f, row == 0 ? -0.85f : 0.85f),
                                new Vector3(1.18f, 2.6f, 1.55f), row == 0 ? 180f : 0f);
                    break;
                case "Merch Tent":
                    foreach (float x in new[] { -1.85f, 1.85f })
                        FestivalProp("WristbandBooth", root, new Vector3(x, 0f, 0f), new Vector3(3.3f, 3.8f, size.y - 0.4f), 180f);
                    break;
                case "Dance Tent":
                    FestivalPlatform(root, size, 0.16f);
                    FestivalProp("EventCanopy", root, new Vector3(0f, 0.16f, 0f), new Vector3(size.x - 0.4f, 6.4f, size.y - 0.4f), 90f);
                    FestivalConsole(root, new Vector3(0f, 0.16f, 3.3f), 3.6f);
                    foreach (float side in new[] { -1f, 1f })
                        FestivalProp("Speakers", root, new Vector3(side * 3.4f, 0.16f, 2.8f), new Vector3(1.4f, 2.6f, 1.4f), 180f);
                    break;
                case "Generator":
                    FestivalProp("Generator", root, Vector3.zero, new Vector3(size.x - 0.4f, 2f, size.y - 0.4f), 180f);
                    break;
            }
            festivalLandmarkHeights[landmark] = FestivalBounds(root, root).max.y;
            return true;
        }

        void FestivalPlatform(Transform parent, Vector2Int size, float height)
        {
            FestivalPart(parent, "Event platform", new Vector3(0f, height * 0.5f, 0f), new Vector3(size.x - 0.25f, height, size.y - 0.25f), FestivalDeck);
            FestivalPart(parent, "Front edge", new Vector3(0f, height + 0.015f, -size.y * 0.5f + 0.2f), new Vector3(size.x - 0.4f, 0.035f, 0.1f), FestivalTrim);
        }

        void FestivalConsole(Transform parent, Vector3 floor, float width)
        {
            FestivalPart(parent, "Control desk", floor + new Vector3(0f, 0.55f, 0f), new Vector3(width, 1.1f, 0.8f), FestivalDeck);
            FestivalPart(parent, "Mixing surface", floor + new Vector3(0f, 1.14f, 0f), new Vector3(width - 0.12f, 0.08f, 0.75f), new Color(0.23f, 0.24f, 0.25f));
            // Large fader strips read as a console at the gameplay camera's distance.
            for (int strip = 0; strip < 7; strip++)
                FestivalPart(parent, "Fader strip", floor + new Vector3((strip - 3) * width / 9f, 1.19f, -0.05f), new Vector3(0.045f, 0.025f, 0.4f), FestivalTrim);
        }

        void FestivalPart(Transform parent, string name, Vector3 position, Vector3 size, Color color)
        {
            var part = AddPart(parent, position, size, color);
            part.name = name;
        }

        GameObject FestivalProp(string name, Transform parent, Vector3 floor, Vector3 limits, float yaw = 0f, float maxScale = 1f)
        {
            var prefab = FestivalPrefab(name);
            if (prefab == null) return null;
            var prop = Instantiate(prefab, parent, false);
            prop.name = name;
            prop.transform.localPosition = Vector3.zero;
            prop.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            prop.transform.localScale = Vector3.one;
            Bounds bounds = FestivalBounds(prop.transform, parent);
            float scale = Mathf.Min(maxScale, limits.x / Mathf.Max(bounds.size.x, 0.001f), limits.y / Mathf.Max(bounds.size.y, 0.001f), limits.z / Mathf.Max(bounds.size.z, 0.001f));
            prop.transform.localScale = Vector3.one * scale;
            prop.transform.localPosition = floor - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z) * scale;
            return prop;
        }

        static Bounds FestivalBounds(Transform contents, Transform frame)
        {
            bool found = false;
            var result = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var filter in contents.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;
                var meshBounds = filter.sharedMesh.bounds;
                var matrix = frame.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = matrix.MultiplyPoint3x4(meshBounds.center + Vector3.Scale(meshBounds.extents,
                        new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1)));
                    if (found) result.Encapsulate(point);
                    else { result = new Bounds(point, Vector3.zero); found = true; }
                }
            }
            return result;
        }

        bool TryBuildFestivalTruckBlocker(Transform parent, float length)
        {
            if (selectedMap != VenueMap.SummerFestival || FestivalPrefab("FoodTruckBurger") == null) return false;
            return FestivalProp("FoodTruckBurger", parent, new Vector3(0f, 0f, 1.6f), new Vector3(length, 4f, 2.6f)) != null;
        }
    }
}
