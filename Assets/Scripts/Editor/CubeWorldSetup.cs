using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Adds the cube terrain and a first person player to the active scene.
/// Runs once automatically after the scripts first compile, and on demand via Tools > Cube World > Build Scene.
/// </summary>
[InitializeOnLoad]
static class CubeWorldSetup
{
    const string Root = "Assets/CubeWorld";
    const string TexturePath = Root + "/BlockTexture.png";

    static string AutoSetupDoneKey => "CubeWorldSetup.Done:" + Application.dataPath;

    static CubeWorldSetup()
    {
        EditorApplication.delayCall += AutoSetupOnce;
    }

    static void AutoSetupOnce()
    {
        if (EditorPrefs.GetBool(AutoSetupDoneKey, false)) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path)) return;

        EditorPrefs.SetBool(AutoSetupDoneKey, true);
        if (FindInScene<CubeTerrain>(scene) != null) return;
        BuildScene();
        Debug.Log($"Cube World: added a cube terrain and first person player to '{scene.path}'. Press Play to walk around.");
    }

    [MenuItem("Tools/Cube World/Build Scene")]
    public static void BuildScene()
    {
        var scene = SceneManager.GetActiveScene();
        var materials = CreateMaterials();

        var terrain = FindInScene<CubeTerrain>(scene);
        if (terrain == null)
        {
            var go = new GameObject("Cube Terrain");
            Undo.RegisterCreatedObjectUndo(go, "Create Cube Terrain");
            terrain = go.AddComponent<CubeTerrain>();
            go.transform.position = new Vector3(-(terrain.sizeX / 2), 0f, -(terrain.sizeZ / 2));
        }
        Undo.RecordObject(terrain, "Assign Cube Terrain Materials");
        terrain.grassMaterial = materials[0];
        terrain.dirtMaterial = materials[1];
        terrain.stoneMaterial = materials[2];
        terrain.sandMaterial = materials[3];
        terrain.snowMaterial = materials[4];
        EditorUtility.SetDirty(terrain);
        terrain.Generate();

        var player = FindInScene<FirstPersonController>(scene);
        if (player == null)
        {
            var go = new GameObject("Player");
            Undo.RegisterCreatedObjectUndo(go, "Create Player");
            var controller = go.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.4f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            controller.stepOffset = 0.4f; // less than a cube, so climbing needs a jump
            controller.skinWidth = 0.05f;
            controller.slopeLimit = 45f;
            player = go.AddComponent<FirstPersonController>();
            go.transform.SetPositionAndRotation(terrain.GetSurfacePoint(Vector3.zero) + Vector3.up * 0.1f, Quaternion.identity);
        }

        var camera = FindMainCamera(scene);
        if (camera == null)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            Undo.RegisterCreatedObjectUndo(go, "Create Camera");
            camera = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
        }
        Undo.SetTransformParent(camera.transform, player.transform, "Parent Camera To Player");
        Undo.RecordObject(camera.transform, "Position Camera");
        Undo.RecordObject(camera, "Configure Camera");
        camera.transform.localPosition = new Vector3(0f, 1.62f, 0f);
        camera.transform.localRotation = Quaternion.identity;
        camera.nearClipPlane = 0.05f;
        camera.fieldOfView = 70f;

        Undo.RecordObject(player, "Assign Player Camera");
        player.cameraRoot = camera.transform;
        EditorUtility.SetDirty(player);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!string.IsNullOrEmpty(scene.path)) EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject = player.gameObject;
    }

    static Material[] CreateMaterials()
    {
        if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets", "CubeWorld");
        var texture = CreateBlockTexture();
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        string[] names = { "Grass", "Dirt", "Stone", "Sand", "Snow" };
        var materials = new Material[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            string path = $"{Root}/{names[i]}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                material.SetColor("_BaseColor", CubeTerrain.DefaultColors[i]);
                material.SetTexture("_BaseMap", texture);
                material.SetFloat("_Smoothness", i == 4 ? 0.35f : 0.1f);
                AssetDatabase.CreateAsset(material, path);
            }
            materials[i] = material;
        }
        AssetDatabase.SaveAssets();
        return materials;
    }

    /// <summary>Small greyscale noise tile with darker edges so individual cubes read clearly; tinted per material.</summary>
    static Texture2D CreateBlockTexture()
    {
        if (!File.Exists(TexturePath))
        {
            const int size = 16;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var rng = new System.Random(42);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float v = 0.84f + (float)rng.NextDouble() * 0.16f;
                    if (x == 0 || y == 0 || x == size - 1 || y == size - 1) v *= 0.75f;
                    texture.SetPixel(x, y, new Color(v, v, v, 1f));
                }
            }
            File.WriteAllBytes(TexturePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(TexturePath);

            var importer = (TextureImporter)AssetImporter.GetAtPath(TexturePath);
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
    }

    static T FindInScene<T>(Scene scene) where T : Component
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            var found = root.GetComponentInChildren<T>(true);
            if (found != null) return found;
        }
        return null;
    }

    static Camera FindMainCamera(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
            foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                if (camera.CompareTag("MainCamera")) return camera;
        return null;
    }
}
