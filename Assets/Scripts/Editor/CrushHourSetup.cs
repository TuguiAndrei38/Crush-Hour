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

    static CrushHourSetup()
    {
        EditorApplication.delayCall += AutoSetupOnce;
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
