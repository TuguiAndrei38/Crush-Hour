using System.IO;
using System.Linq;
using CrushHour;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Creates the Crush Hour festival prototype scene (camera, light, game object, materials) and puts it first in the build.
/// Runs once automatically after the scripts first compile, and on demand via Tools > Crush Hour > Create Festival Scene.
/// </summary>
[InitializeOnLoad]
static class CrushHourSetup
{
    const string ScenePath = "Assets/Scenes/CrushHourFestival.unity";
    const string Folder = "Assets/CrushHour";

    static string AutoSetupDoneKey => "CrushHourSetup.Done:" + Application.dataPath;

    static string AnimatedCrowdUpgradeKey => "CrushHourSetup.AnimatedCrowd:" + Application.dataPath;

    static CrushHourSetup()
    {
        EditorApplication.delayCall += AutoSetupOnce;
        EditorApplication.delayCall += UpgradeToAnimatedCrowdOnce;
    }

    /// <summary>
    /// Scenes set up before animated crowds existed have person prefabs but no clips/shader, and meshes without
    /// Read/Write: finish that setup once, for the festival scene if it's open.
    /// </summary>
    static void UpgradeToAnimatedCrowdOnce()
    {
        if (EditorPrefs.GetBool(AnimatedCrowdUpgradeKey, false) || EditorApplication.isPlayingOrWillChangePlaymode) return;
        var game = FindGameInOpenScene();
        if (game == null || game.personPrefabs == null || game.personPrefabs.Length == 0) return;
        EditorPrefs.SetBool(AnimatedCrowdUpgradeKey, true);
        if (game.crowdClips != null && game.crowdClips.Length > 0 && game.crowdAnimationShader != null) return;
        ApplyPersonModels(PreferPrefabs(game.personPrefabs.Where(p => p != null).ToList()), game);
    }

    static void AutoSetupOnce()
    {
        if (EditorPrefs.GetBool(AutoSetupDoneKey, false) || EditorApplication.isPlayingOrWillChangePlaymode) return;
        EditorPrefs.SetBool(AutoSetupDoneKey, true);
        if (!File.Exists(ScenePath)) CreateFestivalScene();
    }

    [MenuItem("Tools/Crush Hour/Create Festival Scene")]
    public static void CreateFestivalScene()
    {
        if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Crush Hour", $"{ScenePath} already exists. Replace it?", "Replace", "Cancel")) return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "CrushHour");
        var lit = CreateMaterial("Universal Render Pipeline/Lit", $"{Folder}/CrushHour_Lit.mat", true);
        var unlit = CreateMaterial("Universal Render Pipeline/Unlit", $"{Folder}/CrushHour_Unlit.mat", false);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var game = new GameObject("Crush Hour").AddComponent<CrushHourGame>();
        game.litMaterial = lit;
        game.unlitMaterial = unlit;
        game.crowdAnimationShader = Shader.Find("CrushHour/CrowdVAT");
        foreach (var root in scene.GetRootGameObjects())
        {
            if (!root.TryGetComponent(out Camera camera)) continue;
            camera.farClipPlane = 600f;
            root.AddComponent<TopDownCamera>().Apply();
        }

        EditorSceneManager.SaveScene(scene, ScenePath);
        var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
        scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log($"Crush Hour: created and opened {ScenePath}. Press Play.");
    }

    /// <summary>
    /// Points the festival crowd at character prefabs: the prefabs/folders selected in the Project window,
    /// or else anything under a folder named like the "Low Poly People" (David Jalbert) pack.
    /// Also picks up idle/walk/run/wave clips next to them (baked into vertex animation textures at runtime)
    /// and enables Read/Write on their meshes, which baking needs.
    /// </summary>
    [MenuItem("Tools/Crush Hour/Use Person Models (selected, or Low Poly People)")]
    public static void UsePersonModels()
    {
        var prefabs = PrefabsIn(Selection.objects.Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)));
        if (prefabs.Count == 0)
        {
            var packFolders = AssetDatabase.GetAllAssetPaths()
                .Where(AssetDatabase.IsValidFolder)
                .Where(p => Normalise(p).Contains("lowpolypeople") || Normalise(p).Contains("jalbert"));
            prefabs = PrefabsIn(packFolders);
        }
        prefabs = PreferPrefabs(prefabs);
        if (prefabs.Count == 0)
        {
            EditorUtility.DisplayDialog("Crush Hour",
                "No character prefabs found.\n\nImport 'Low Poly People' via Window > Package Manager > My Assets, " +
                "then run this again (or select the character prefabs / their folder in the Project window first).", "OK");
            return;
        }

        var game = FindGameInOpenScene();
        if (game == null)
        {
            if (!File.Exists(ScenePath) || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(ScenePath);
            game = FindGameInOpenScene();
            if (game == null) return;
        }
        ApplyPersonModels(prefabs, game);
    }

    /// <summary>Real prefabs are fully assembled; raw FBX models are only a fallback.</summary>
    static System.Collections.Generic.List<GameObject> PreferPrefabs(System.Collections.Generic.List<GameObject> prefabs)
    {
        bool hasPrefabs = prefabs.Any(p => PrefabUtility.GetPrefabAssetType(p) != PrefabAssetType.Model);
        return hasPrefabs ? prefabs.Where(p => PrefabUtility.GetPrefabAssetType(p) != PrefabAssetType.Model).ToList() : prefabs;
    }

    static void ApplyPersonModels(System.Collections.Generic.List<GameObject> prefabs, CrushHourGame game)
    {
        EnableReadWrite(prefabs);
        var clips = ClipsNear(prefabs);

        Undo.RecordObject(game, "Use Person Models");
        game.personPrefabs = prefabs.ToArray();
        game.crowdClips = clips.ToArray();
        game.crowdAnimationShader = Shader.Find("CrushHour/CrowdVAT");
        EditorUtility.SetDirty(game);
        EditorSceneManager.SaveScene(game.gameObject.scene);
        Debug.Log($"Crush Hour: the crowd now uses {prefabs.Count} person models and {clips.Count} animation clips " +
                  $"({string.Join(", ", clips.Select(c => c.name))}).\n" + string.Join("\n", prefabs.Select(AssetDatabase.GetAssetPath)));
    }

    /// <summary>Idle/walk/run/wave clips in the prefabs' folder or the folder above it (e.g. Prefabs/Animations).</summary>
    static System.Collections.Generic.List<AnimationClip> ClipsNear(System.Collections.Generic.List<GameObject> prefabs)
    {
        var folders = prefabs
            .Select(p => Path.GetDirectoryName(AssetDatabase.GetAssetPath(p))?.Replace('\\', '/'))
            .Where(f => !string.IsNullOrEmpty(f))
            .SelectMany(f => new[] { f, Path.GetDirectoryName(f)?.Replace('\\', '/') })
            .Where(f => !string.IsNullOrEmpty(f) && f != "Assets" && AssetDatabase.IsValidFolder(f))
            .Distinct()
            .ToArray();
        if (folders.Length == 0) return new System.Collections.Generic.List<AnimationClip>();
        string[] keys = { "idle", "walk", "run", "wave" };
        return AssetDatabase.FindAssets("t:AnimationClip", folders)
            .Select(AssetDatabase.GUIDToAssetPath)
            .Distinct()
            .SelectMany(path => AssetDatabase.LoadAllAssetsAtPath(path))
            .OfType<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview__") && keys.Any(k => c.name.ToLowerInvariant().Contains(k)))
            .Distinct()
            .ToList();
    }

    /// <summary>Baking the crowd's meshes and animations at runtime needs CPU-readable meshes.</summary>
    static void EnableReadWrite(System.Collections.Generic.List<GameObject> prefabs)
    {
        var meshes = prefabs.SelectMany(p => p.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => r.sharedMesh)
                .Concat(p.GetComponentsInChildren<MeshFilter>(true).Select(f => f.sharedMesh)))
            .Where(m => m != null && !m.isReadable);
        foreach (string path in meshes.Select(AssetDatabase.GetAssetPath).Distinct().ToList())
        {
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer || importer.isReadable) continue;
            importer.isReadable = true;
            importer.SaveAndReimport();
        }
    }

    static System.Collections.Generic.List<GameObject> PrefabsIn(System.Collections.Generic.IEnumerable<string> paths)
    {
        var result = new System.Collections.Generic.List<GameObject>();
        foreach (string path in paths)
        {
            var candidates = new[] { path };
            if (AssetDatabase.IsValidFolder(path))
            {
                // Prefer prefabs; packs that only ship FBX models get those instead.
                var guids = AssetDatabase.FindAssets("t:Prefab", new[] { path });
                if (guids.Length == 0) guids = AssetDatabase.FindAssets("t:Model", new[] { path });
                candidates = guids.Select(AssetDatabase.GUIDToAssetPath).ToArray();
            }
            foreach (string candidate in candidates)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(candidate);
                // Characters only: something with a mesh, and no cameras/lights from demo scenes.
                if (go == null || result.Contains(go) || go.GetComponentInChildren<Renderer>(true) == null) continue;
                if (go.GetComponentInChildren<Camera>(true) != null || go.GetComponentInChildren<Light>(true) != null) continue;
                result.Add(go);
            }
        }
        return result;
    }

    static string Normalise(string path) => path.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");

    static CrushHourGame FindGameInOpenScene()
    {
        var scene = EditorSceneManager.GetActiveScene();
        foreach (var root in scene.GetRootGameObjects())
        {
            var game = root.GetComponentInChildren<CrushHourGame>(true);
            if (game != null) return game;
        }
        return null;
    }

    static Material CreateMaterial(string shaderName, string path, bool instancing)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;
        material = new Material(Shader.Find(shaderName)) { enableInstancing = instancing };
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.1f);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }
}
