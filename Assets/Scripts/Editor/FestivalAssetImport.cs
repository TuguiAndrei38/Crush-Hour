using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Builds shared URP materials and runtime prefabs from the cleaned festival FBXs.</summary>
[InitializeOnLoad]
public sealed class FestivalAssetImport : AssetPostprocessor
{
    const string ArtRoot = "Assets/Art/Festival";
    const string ModelRoot = ArtRoot + "/Models/";
    const string MaterialRoot = ArtRoot + "/Materials";
    const string ResourceRoot = "Assets/Resources/Festival";
    const string ManifestPath = ArtRoot + "/import-manifest.json";
    const string BuildKey = "CrushHour.FestivalAssetImport.v1";
    static bool queued, building;

    [Serializable] sealed class Manifest
    {
        public int version;
        public ModelInfo[] models;
        public PaletteInfo[] palette;
    }

    [Serializable] sealed class ModelInfo
    {
        public string name;
        public string source;
        public float width, height, depth;
        public int meshes, triangles;
    }

    [Serializable] sealed class PaletteInfo
    {
        public string name;
        public float r, g, b, a;
        public string texture;
    }

    static FestivalAssetImport()
    {
        QueueBuild();
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredEditMode) QueueBuild();
        };
    }

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(ModelRoot, StringComparison.Ordinal)) return;
        var importer = (ModelImporter)assetImporter;
        importer.globalScale = 1f;
        importer.useFileScale = true;
        importer.bakeAxisConversion = true;
        importer.importAnimation = false;
        importer.animationType = ModelImporterAnimationType.None;
        importer.importCameras = false;
        importer.importLights = false;
        importer.addCollider = false;
        importer.isReadable = false;
        importer.meshCompression = ModelImporterMeshCompression.Off;
        importer.importNormals = ModelImporterNormals.Import;
        importer.importTangents = ModelImporterTangents.None;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        importer.materialName = ModelImporterMaterialName.BasedOnMaterialName;
        importer.userData = "CrushHour.FestivalImport.v1";
    }

    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        if (!building && imported.Any(path => path.StartsWith(ModelRoot, StringComparison.Ordinal) || path == ManifestPath))
            QueueBuild();
    }

    static void QueueBuild()
    {
        if (queued || building) return;
        queued = true;
        EditorApplication.delayCall += () =>
        {
            queued = false;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { QueueBuild(); return; }
            if (!EditorApplication.isPlayingOrWillChangePlaymode) Build(false);
        };
    }

    [MenuItem("Tools/Crush Hour/Rebuild Festival Art Prefabs")]
    public static void Rebuild() => Build(true);

    static void Build(bool force)
    {
        if (building || !File.Exists(ManifestPath)) return;
        var contents = File.ReadAllText(ManifestPath);
        var manifest = JsonUtility.FromJson<Manifest>(contents);
        if (manifest?.models == null || manifest.palette == null) return;
        if (manifest.models.Any(model => AssetDatabase.LoadAssetAtPath<GameObject>(ModelRoot + model.name + ".fbx") == null)) return;
        string stamp = Hash128.Compute(contents + "importer-v1").ToString();
        if (!force && SessionState.GetString(BuildKey, "") == stamp &&
            manifest.models.All(model => File.Exists(ResourceRoot + "/" + model.name + ".prefab"))) return;
        building = true;
        try
        {
            Directory.CreateDirectory(MaterialRoot);
            Directory.CreateDirectory(ResourceRoot);
            AssetDatabase.Refresh();
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("Festival art requires URP/Lit.");
            var materials = manifest.palette.ToDictionary(item => item.name, item => CreateMaterial(item, shader));
            foreach (var info in manifest.models)
            {
                string modelPath = ModelRoot + info.name + ".fbx";
                var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
                if (importer.userData != "CrushHour.FestivalImport.v1") importer.SaveAndReimport();
                var sourceMaterials = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Material>().ToArray();
                bool changed = false;
                var remaps = importer.GetExternalObjectMap();
                foreach (var original in sourceMaterials)
                {
                    var key = materials.Keys.FirstOrDefault(name => original.name.StartsWith(name, StringComparison.Ordinal));
                    if (key == null) continue;
                    var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), original.name);
                    if (remaps.TryGetValue(id, out var mapped) && mapped == materials[key]) continue;
                    importer.AddRemap(id, materials[key]);
                    changed = true;
                }
                if (changed) importer.SaveAndReimport();
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                var root = new GameObject(info.name);
                try
                {
                    var visual = UnityEngine.Object.Instantiate(model, root.transform, false);
                    visual.name = "Visual";
                    foreach (var renderer in visual.GetComponentsInChildren<Renderer>())
                    {
                        renderer.sharedMaterials = renderer.sharedMaterials.Select(material =>
                        {
                            var key = material == null ? null : materials.Keys.FirstOrDefault(name => material.name.StartsWith(name, StringComparison.Ordinal));
                            return key == null ? material : materials[key];
                        }).ToArray();
                    }
                    var renderers = visual.GetComponentsInChildren<Renderer>();
                    if (renderers.Length > 0)
                    {
                        var bounds = renderers[0].bounds;
                        foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                        visual.transform.localPosition -= new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                    }
                    PrefabUtility.SaveAsPrefabAsset(root, ResourceRoot + "/" + info.name + ".prefab");
                }
                finally { UnityEngine.Object.DestroyImmediate(root); }
            }
            AssetDatabase.SaveAssets();
            SessionState.SetString(BuildKey, stamp);
            Debug.Log($"Festival art: built {manifest.models.Length} runtime prefabs with {materials.Count} shared URP materials.");
        }
        catch (Exception exception) { Debug.LogException(exception); }
        finally { building = false; }
    }

    static Material CreateMaterial(PaletteInfo info, Shader shader)
    {
        string path = MaterialRoot + "/" + info.name + ".mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = info.name };
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = shader;
        material.SetColor("_BaseColor", new Color(info.r, info.g, info.b, info.a));
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Smoothness", 0.15f);
        material.enableInstancing = true;
        if (!string.IsNullOrEmpty(info.texture))
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(ArtRoot + "/Textures/" + info.texture));
        EditorUtility.SetDirty(material);
        return material;
    }
}
