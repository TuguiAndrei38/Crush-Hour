using System.IO;
using System.Linq;
using CrushHour;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Crush Hour > Create Control Room Scene: copies the Summer Festival scene (same game settings, person
/// models and materials) to Assets/Scenes/CrushHourControlRoom.unity, adds the <see cref="ControlRoom"/> with the
/// models from Assets/Models/ControlRoom, takes the top-down rig off the camera, drops the old SampleScene leftovers
/// (the "Player" first-person controller and the "Cube Terrain") and adds the scene to Build Settings.
/// The start menu's PLAY loads this scene for every map.
/// </summary>
static class CrushHourControlRoomSetup
{
    const string SourceScene = "Assets/Scenes/CrushHourFestival.unity";
    const string ScenePath = "Assets/Scenes/" + CrushHourGame.ControlRoomScene + ".unity";
    const string ModelFolder = "Assets/Models/ControlRoom";
    /// <summary>Scripts from the old SampleScene experiment that ended up in the festival scene; not part of the game.</summary>
    static readonly string[] SampleSceneScripts = { "FirstPersonController", "CubeTerrain" };

    [MenuItem("Tools/Crush Hour/Create Control Room Scene")]
    public static void CreateControlRoomScene()
    {
        if (!File.Exists(SourceScene))
        {
            EditorUtility.DisplayDialog("Crush Hour", $"{SourceScene} is missing: create the festival scene first.", "OK");
            return;
        }
        if (!AssetDatabase.IsValidFolder(ModelFolder) || Load("Room") == null)
        {
            EditorUtility.DisplayDialog("Crush Hour", $"The control room models aren't in {ModelFolder} (Room.obj, VideoWall_Single.obj, ...).", "OK");
            return;
        }
        if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Crush Hour", $"{ScenePath} already exists. Replace it?", "Replace", "Cancel")) return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        if (File.Exists(ScenePath)) AssetDatabase.DeleteAsset(ScenePath);
        if (!AssetDatabase.CopyAsset(SourceScene, ScenePath))
        {
            Debug.LogError($"Crush Hour: couldn't copy {SourceScene} to {ScenePath}.");
            return;
        }
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        foreach (var root in scene.GetRootGameObjects())
            if (root.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c != null && SampleSceneScripts.Contains(c.GetType().Name)))
            {
                Debug.Log($"Crush Hour: removed '{root.name}' (SampleScene leftover) from the control room scene.");
                Object.DestroyImmediate(root);
            }

        CrushHourGame game = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (game == null) game = root.GetComponentInChildren<CrushHourGame>(true);
            // The operator's camera is driven by the control room, not by the top-down rig.
            foreach (var rig in root.GetComponentsInChildren<TopDownCamera>(true)) Object.DestroyImmediate(rig);
        }
        if (game == null)
        {
            Debug.LogError($"Crush Hour: {ScenePath} has no CrushHourGame.");
            return;
        }

        var room = new GameObject("Control Room").AddComponent<ControlRoom>();
        room.roomModel = Load("Room");
        room.doorModel = Load("Room_DoorLeaf");
        room.videoWallModel = Load("VideoWall_Single");
        room.deskModel = Load("ControlDesk_WithMonitors");
        room.boardModel = Load("ControlBoard");
        room.chairModel = Load("OperatorChair");
        room.cctvModel = Load("Cctv_Camera");
        room.walkieModel = Load("WalkieTalkie");
        room.litMaterial = game.litMaterial;
        // The menu's PLAY uses the difficulty's real crowd size; pressing Play in this scene directly does too.
        game.crowdSizeOverride = 0;
        EditorUtility.SetDirty(game);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        // After the scene the game starts in (the start menu lives there), so it isn't the first scene.
        // (Scenes whose file is gone, like SampleScene, are dropped from the list.)
        var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath && File.Exists(s.path)).ToList();
        scenes.Insert(Mathf.Min(1, scenes.Count), new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log($"Crush Hour: created {ScenePath} from {SourceScene} and added it to Build Settings. " +
                  "Press Play here, or start from the menu scene and press PLAY.");
    }

    static GameObject Load(string name)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelFolder}/{name}.obj");
        if (model == null && name != "Room") Debug.LogWarning($"Crush Hour: {ModelFolder}/{name}.obj not found.");
        return model;
    }
}
