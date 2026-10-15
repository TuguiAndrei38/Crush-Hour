using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Generates a blocky, voxel-style terrain made of 1x1x1 cubes from fractal Perlin noise.
/// Only the visible cube faces are meshed, split into chunks that each get a MeshCollider.
/// The meshes are rebuilt on load (play mode and edit mode) and are never saved into the scene.
/// </summary>
[ExecuteAlways, SelectionBase, DefaultExecutionOrder(-100)]
public class CubeTerrain : MonoBehaviour
{
    public enum Block : byte { Air, Grass, Dirt, Stone, Sand, Snow }

    const int BlockTypeCount = 5;
    const string ChunkRootName = "Chunks (generated)";

    public static readonly Color[] DefaultColors =
    {
        new Color(0.38f, 0.64f, 0.26f), // Grass
        new Color(0.49f, 0.35f, 0.22f), // Dirt
        new Color(0.52f, 0.52f, 0.54f), // Stone
        new Color(0.87f, 0.81f, 0.58f), // Sand
        new Color(0.95f, 0.96f, 0.98f), // Snow
    };

    [Header("Size")]
    [Min(1)] public int sizeX = 128;
    [Min(1)] public int sizeZ = 128;
    [Min(4)] public int chunkSize = 16;

    [Header("Shape")]
    public int seed = 1234;
    [Range(0.001f, 0.2f)] public float noiseScale = 0.02f;
    [Range(1, 8)] public int octaves = 4;
    [Range(0f, 1f)] public float persistence = 0.5f;
    [Min(1f)] public float lacunarity = 2f;
    [Min(0)] public int baseHeight = 4;
    [Min(1)] public int heightAmplitude = 32;
    [Tooltip("Above 1 flattens lowlands and sharpens peaks.")]
    [Range(0.5f, 4f)] public float heightExponent = 1.8f;

    [Header("Layers (by column height)")]
    public int sandLevel = 7;
    public int rockLevel = 22;
    public int snowLevel = 28;
    [Min(0)] public int dirtDepth = 3;

    [Header("Materials (fallbacks are created when empty)")]
    public Material grassMaterial;
    public Material dirtMaterial;
    public Material stoneMaterial;
    public Material sandMaterial;
    public Material snowMaterial;

    static readonly Vector3[][] FaceCorners =
    {
        // Clockwise when seen from outside: bottom-left, top-left, top-right, bottom-right.
        new[] { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) }, // +Y
        new[] { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) }, // +X
        new[] { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), new Vector3(0, 0, 0) }, // -X
        new[] { new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1) }, // +Z
        new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) }, // -Z
    };
    static readonly Vector3[] FaceNormals = { Vector3.up, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
    static readonly Vector2Int[] SideOffsets = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) }; // matches faces 1..4
    static readonly Vector2[] FaceUVs = { new(0, 0), new(0, 1), new(1, 1), new(1, 0) };

    int[,] heights;
    readonly List<Mesh> generatedMeshes = new();
    Material[] fallbackMaterials;
#if UNITY_EDITOR
    bool generatePending;
#endif

    void Awake()
    {
        if (Application.isPlaying) Generate();
    }

    void OnEnable()
    {
#if UNITY_EDITOR
        // Generating while the hierarchy is being activated is not allowed, so defer it.
        if (!Application.isPlaying) ScheduleEditorGenerate();
#endif
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!Application.isPlaying) ScheduleEditorGenerate();
    }

    void ScheduleEditorGenerate()
    {
        if (generatePending) return;
        generatePending = true;
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this == null) return;
            generatePending = false;
            if (isActiveAndEnabled && !Application.isPlaying) Generate();
        };
    }
#endif

    void OnDestroy()
    {
        DestroyMeshes();
        if (fallbackMaterials != null)
            foreach (var m in fallbackMaterials) DestroySafe(m);
    }

    /// <summary>Top solid block Y (local) of a column.</summary>
    public int GetColumnHeight(int x, int z) => SampleHeight(x, z, OctaveOffsets());

    /// <summary>World position standing on top of the column under <paramref name="worldPosition"/>.</summary>
    public Vector3 GetSurfacePoint(Vector3 worldPosition)
    {
        Vector3 local = transform.InverseTransformPoint(worldPosition);
        int x = Mathf.Clamp(Mathf.FloorToInt(local.x), 0, sizeX - 1);
        int z = Mathf.Clamp(Mathf.FloorToInt(local.z), 0, sizeZ - 1);
        return transform.TransformPoint(new Vector3(x + 0.5f, GetColumnHeight(x, z) + 1, z + 0.5f));
    }

    [ContextMenu("Regenerate")]
    public void Generate()
    {
        Clear();

        var offsets = OctaveOffsets();
        heights = new int[sizeX, sizeZ];
        for (int x = 0; x < sizeX; x++)
            for (int z = 0; z < sizeZ; z++)
                heights[x, z] = SampleHeight(x, z, offsets);

        var flags = Application.isPlaying ? HideFlags.None : HideFlags.DontSave | HideFlags.NotEditable;
        var root = new GameObject(ChunkRootName) { hideFlags = flags };
        root.transform.SetParent(transform, false);

        var materials = GetMaterials();
        for (int cx = 0; cx < sizeX; cx += chunkSize)
            for (int cz = 0; cz < sizeZ; cz += chunkSize)
                BuildChunk(root.transform, cx, cz, materials, flags);
    }

    Vector2[] OctaveOffsets()
    {
        var rng = new System.Random(seed);
        var offsets = new Vector2[octaves];
        for (int i = 0; i < octaves; i++)
            offsets[i] = new Vector2(rng.Next(-10000, 10000), rng.Next(-10000, 10000));
        return offsets;
    }

    int SampleHeight(int x, int z, Vector2[] offsets)
    {
        float amplitude = 1f, frequency = noiseScale, sum = 0f, norm = 0f;
        foreach (var offset in offsets)
        {
            sum += Mathf.PerlinNoise(x * frequency + offset.x, z * frequency + offset.y) * amplitude;
            norm += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        float t = Mathf.Clamp01((sum / norm - 0.25f) * 2f);
        return baseHeight + Mathf.RoundToInt(Mathf.Pow(t, heightExponent) * heightAmplitude);
    }

    Block BlockAt(int y, int columnHeight)
    {
        if (y > columnHeight) return Block.Air;
        bool beach = columnHeight <= sandLevel;
        bool rocky = columnHeight >= rockLevel;
        if (y == columnHeight)
        {
            if (beach) return Block.Sand;
            if (columnHeight >= snowLevel) return Block.Snow;
            return rocky ? Block.Stone : Block.Grass;
        }
        if (y >= columnHeight - dirtDepth) return beach ? Block.Sand : rocky ? Block.Stone : Block.Dirt;
        return Block.Stone;
    }

    void BuildChunk(Transform parent, int startX, int startZ, Material[] materials, HideFlags flags)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>[BlockTypeCount];
        for (int i = 0; i < BlockTypeCount; i++) triangles[i] = new List<int>();

        void AddFace(int face, int x, int y, int z, Block block)
        {
            int start = vertices.Count;
            var origin = new Vector3(x - startX, y, z - startZ);
            for (int i = 0; i < 4; i++)
            {
                vertices.Add(origin + FaceCorners[face][i]);
                normals.Add(FaceNormals[face]);
                uvs.Add(FaceUVs[i]);
            }
            var tris = triangles[(int)block - 1];
            tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
            tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
        }

        int endX = Mathf.Min(startX + chunkSize, sizeX);
        int endZ = Mathf.Min(startZ + chunkSize, sizeZ);
        for (int x = startX; x < endX; x++)
        {
            for (int z = startZ; z < endZ; z++)
            {
                int h = heights[x, z];
                AddFace(0, x, h, z, BlockAt(h, h));
                for (int s = 0; s < SideOffsets.Length; s++)
                {
                    int nx = x + SideOffsets[s].x, nz = z + SideOffsets[s].y;
                    int neighbourHeight = nx >= 0 && nx < sizeX && nz >= 0 && nz < sizeZ ? heights[nx, nz] : -1;
                    for (int y = neighbourHeight + 1; y <= h; y++)
                        AddFace(s + 1, x, y, z, BlockAt(y, h));
                }
            }
        }

        var mesh = new Mesh { name = $"Cube Chunk {startX / chunkSize},{startZ / chunkSize}", indexFormat = IndexFormat.UInt32, hideFlags = flags };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = BlockTypeCount;
        for (int i = 0; i < BlockTypeCount; i++) mesh.SetTriangles(triangles[i], i, false);
        mesh.RecalculateBounds();
        generatedMeshes.Add(mesh);

        var chunk = new GameObject(mesh.name) { hideFlags = flags };
        chunk.transform.SetParent(parent, false);
        chunk.transform.localPosition = new Vector3(startX, 0, startZ);
        chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
        chunk.AddComponent<MeshRenderer>().sharedMaterials = materials;
        chunk.AddComponent<MeshCollider>().sharedMesh = mesh;
    }

    Material[] GetMaterials()
    {
        var materials = new[] { grassMaterial, dirtMaterial, stoneMaterial, sandMaterial, snowMaterial };
        for (int i = 0; i < materials.Length; i++)
            if (materials[i] == null) materials[i] = FallbackMaterial(i);
        return materials;
    }

    Material FallbackMaterial(int index)
    {
        fallbackMaterials ??= new Material[BlockTypeCount];
        if (fallbackMaterials[index] == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            fallbackMaterials[index] = new Material(shader)
            {
                name = ((Block)(index + 1)) + " (fallback)",
                color = DefaultColors[index],
                hideFlags = HideFlags.DontSave,
            };
        }
        return fallbackMaterials[index];
    }

    void Clear()
    {
        DestroyMeshes();
        // Also sweeps chunk roots whose reference was lost (e.g. after a script reload).
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child.name != ChunkRootName) continue;
            foreach (var filter in child.GetComponentsInChildren<MeshFilter>(true))
                DestroySafe(filter.sharedMesh);
            DestroySafe(child.gameObject);
        }
    }

    void DestroyMeshes()
    {
        foreach (var mesh in generatedMeshes) DestroySafe(mesh);
        generatedMeshes.Clear();
    }

    static void DestroySafe(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }
}
