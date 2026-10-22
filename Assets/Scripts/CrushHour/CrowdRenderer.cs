using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;

namespace CrushHour
{
    /// <summary>
    /// A character prefab prepared for drawing thousands of copies: all parts merged into one mesh, feet at the
    /// origin, scaled to a standard height. When animation clips are available they are baked into vertex animation
    /// textures (one row per frame, one column per vertex) that the CrushHour/CrowdVAT shader plays back on the GPU.
    /// </summary>
    public class PersonModel
    {
        public enum Clip { Idle, Walk, Run, Wave }
        static readonly string[] ClipKeys = { "idle", "walk", "run", "wave" };
        const int BakeFps = 30;

        public string name;
        public Mesh mesh;
        public Material[] staticMaterials;   // URP Lit with instancing, for the unanimated fallback
        public Material[] animatedMaterials; // CrowdVAT, null when not animated
        public Texture2D positions, normals;
        public readonly int[] clipStart = new int[4], clipFrames = new int[4];
        public readonly float[] clipLength = new float[4];
        public bool Animated => animatedMaterials != null;

        static readonly Dictionary<Material, Material> converted = new();

        class Part
        {
            public Renderer renderer;
            public SkinnedMeshRenderer skinned;
            public Mesh mesh;
        }

        public static PersonModel Build(GameObject prefab, Material litTemplate, Shader animationShader,
            IReadOnlyList<AnimationClip> clipLibrary, float height, float yawOffset)
        {
            var instance = Object.Instantiate(prefab, new Vector3(0f, -1000f, 0f), Quaternion.identity);
            try
            {
                return BuildFrom(instance, prefab.name, litTemplate, animationShader, clipLibrary, height, yawOffset);
            }
            finally
            {
                Object.Destroy(instance);
            }
        }

        static PersonModel BuildFrom(GameObject instance, string name, Material litTemplate, Shader animationShader,
            IReadOnlyList<AnimationClip> clipLibrary, float height, float yawOffset)
        {
            var parts = new List<Part>();
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>())
            {
                if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                    parts.Add(new Part { renderer = renderer, skinned = skinned, mesh = skinned.sharedMesh });
                else if (renderer is MeshRenderer && renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
                    parts.Add(new Part { renderer = renderer, mesh = filter.sharedMesh });
            }
            if (parts.Count == 0) return null;
            foreach (var part in parts)
            {
                if (part.mesh.isReadable) continue;
                Debug.LogWarning($"Crush Hour: '{name}' mesh '{part.mesh.name}' needs Read/Write enabled in its import settings " +
                                 "(Tools > Crush Hour > Use Person Models does this).");
                return null;
            }

            var model = new PersonModel { name = name };
            var toRoot = instance.transform.worldToLocalMatrix;
            var baked = new Mesh();
            var tmpVertices = new List<Vector3>();
            var tmpNormals = new List<Vector3>();
            Matrix4x4 normalise = Matrix4x4.identity;

            // Current pose of every part, in the root's space, as one vertex list.
            void Snapshot(List<Vector3> vertices, List<Vector3> normals)
            {
                vertices.Clear();
                normals.Clear();
                foreach (var part in parts)
                {
                    Mesh source;
                    Matrix4x4 m;
                    if (part.skinned != null)
                    {
                        part.skinned.BakeMesh(baked, true);
                        source = baked;
                        m = toRoot * Matrix4x4.TRS(part.skinned.transform.position, part.skinned.transform.rotation, Vector3.one);
                    }
                    else
                    {
                        source = part.mesh;
                        m = toRoot * part.renderer.transform.localToWorldMatrix;
                    }
                    source.GetVertices(tmpVertices);
                    source.GetNormals(tmpNormals);
                    for (int v = 0; v < tmpVertices.Count; v++)
                    {
                        vertices.Add(normalise.MultiplyPoint3x4(m.MultiplyPoint3x4(tmpVertices[v])));
                        var n = v < tmpNormals.Count ? tmpNormals[v] : Vector3.up;
                        normals.Add(normalise.MultiplyVector(m.MultiplyVector(n)).normalized);
                    }
                }
            }

            // Pose the character (first idle frame if we can animate), then fix feet at origin and the height.
            var clips = PickClips(name, clipLibrary);
            var animator = instance.GetComponentInChildren<Animator>();
            bool animate = animationShader != null && clips[(int)Clip.Walk] != null && animator != null && parts.Exists(p => p.skinned != null);
            PlayableGraph graph = default;
            AnimationPlayableOutput output = default;
            if (animate)
            {
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = false;
                graph = PlayableGraph.Create("Crowd bake");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                output = AnimationPlayableOutput.Create(graph, "Bake", animator);
                SamplePose(graph, output, clips[(int)Clip.Idle] ?? clips[(int)Clip.Walk], 0f);
            }

            var restVertices = new List<Vector3>();
            var restNormals = new List<Vector3>();
            Snapshot(restVertices, restNormals);
            var bounds = GeometryUtility.CalculateBounds(restVertices.ToArray(), Matrix4x4.identity);
            if (bounds.size.y < 0.01f)
            {
                if (graph.IsValid()) graph.Destroy();
                return null;
            }
            normalise = Matrix4x4.Rotate(Quaternion.Euler(0f, yawOffset, 0f))
                        * Matrix4x4.Scale(Vector3.one * (height / bounds.size.y))
                        * Matrix4x4.Translate(new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z));
            Snapshot(restVertices, restNormals);

            // One mesh for all parts; one submesh (and material) per part submesh.
            model.mesh = new Mesh { name = name + " (crowd)", indexFormat = IndexFormat.UInt32 };
            model.mesh.SetVertices(restVertices);
            model.mesh.SetNormals(restNormals);
            var uvs = new List<Vector2>();
            var partUvs = new List<Vector2>();
            var triangles = new List<int[]>();
            var sourceMaterials = new List<Material>();
            int offset = 0;
            foreach (var part in parts)
            {
                part.mesh.GetUVs(0, partUvs);
                for (int v = 0; v < part.mesh.vertexCount; v++) uvs.Add(v < partUvs.Count ? partUvs[v] : Vector2.zero);
                var materials = part.renderer.sharedMaterials;
                for (int s = 0; s < part.mesh.subMeshCount; s++)
                {
                    var tris = part.mesh.GetTriangles(s);
                    for (int t = 0; t < tris.Length; t++) tris[t] += offset;
                    triangles.Add(tris);
                    sourceMaterials.Add(s < materials.Length ? materials[s] : null);
                }
                offset += part.mesh.vertexCount;
            }
            model.mesh.SetUVs(0, uvs);
            model.mesh.subMeshCount = triangles.Count;
            for (int s = 0; s < triangles.Count; s++) model.mesh.SetTriangles(triangles[s], s, false);
            model.mesh.RecalculateBounds();

            model.staticMaterials = new Material[sourceMaterials.Count];
            for (int s = 0; s < sourceMaterials.Count; s++) model.staticMaterials[s] = ToInstancedUrp(sourceMaterials[s], litTemplate);

            if (animate && restVertices.Count <= SystemInfo.maxTextureSize)
                BakeAnimations(model, graph, output, clips, Snapshot, sourceMaterials, animationShader);
            if (graph.IsValid()) graph.Destroy();
            Object.Destroy(baked);
            return model;
        }

        static void SamplePose(PlayableGraph graph, AnimationPlayableOutput output, AnimationClip clip, float time)
        {
            var playable = AnimationClipPlayable.Create(graph, clip);
            playable.SetApplyFootIK(false);
            output.SetSourcePlayable(playable);
            playable.SetTime(time);
            graph.Evaluate();
            playable.Destroy();
        }

        delegate void SnapshotFn(List<Vector3> vertices, List<Vector3> normals);

        static void BakeAnimations(PersonModel model, PlayableGraph graph, AnimationPlayableOutput output, AnimationClip[] clips,
            SnapshotFn snapshot, List<Material> sourceMaterials, Shader animationShader)
        {
            var rows = new List<Vector3[]>();
            var rowNormals = new List<Vector3[]>();
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            for (int k = 0; k < clips.Length; k++)
            {
                var clip = clips[k] ?? clips[(int)Clip.Walk];
                int frames = Mathf.Clamp(Mathf.RoundToInt(clip.length * BakeFps), 8, 60);
                model.clipStart[k] = rows.Count;
                model.clipFrames[k] = frames;
                model.clipLength[k] = Mathf.Max(clip.length, 0.1f);

                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(false);
                output.SetSourcePlayable(playable);
                for (int f = 0; f < frames; f++)
                {
                    playable.SetTime(f * clip.length / frames);
                    graph.Evaluate();
                    snapshot(vertices, normals);
                    rows.Add(vertices.ToArray());
                    rowNormals.Add(normals.ToArray());
                }
                playable.Destroy();
            }

            int width = rows[0].Length, heightRows = rows.Count;
            model.positions = MakeTexture(model.name + " positions", rows, width, heightRows);
            model.normals = MakeTexture(model.name + " normals", rowNormals, width, heightRows);
            model.animatedMaterials = new Material[sourceMaterials.Count];
            for (int s = 0; s < sourceMaterials.Count; s++)
            {
                var material = new Material(animationShader) { name = model.name + " (animated)" };
                CopyLook(sourceMaterials[s], material);
                material.SetTexture("_PositionTex", model.positions);
                material.SetTexture("_NormalTex", model.normals);
                model.animatedMaterials[s] = material;
            }
        }

        static Texture2D MakeTexture(string name, List<Vector3[]> rows, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = new Color(rows[y][x].x, rows[y][x].y, rows[y][x].z, 1f);
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>Clips for this character: "normal man a" prefers "normal idle", "normal walk", ...</summary>
        static AnimationClip[] PickClips(string prefabName, IReadOnlyList<AnimationClip> library)
        {
            var result = new AnimationClip[ClipKeys.Length];
            if (library == null) return result;
            string body = prefabName.Trim().Split(' ', '-', '_')[0].ToLowerInvariant();
            for (int k = 0; k < ClipKeys.Length; k++)
            {
                foreach (var clip in library)
                {
                    if (clip == null) continue;
                    string clipName = clip.name.ToLowerInvariant();
                    if (!clipName.Contains(ClipKeys[k])) continue;
                    if (clipName.StartsWith(body)) { result[k] = clip; break; }
                    if (result[k] == null) result[k] = clip;
                }
            }
            return result;
        }

        /// <summary>Copies texture and tint from any pipeline's material (Built-in _MainTex/_Color, URP/HDRP _Base*) onto a URP-style one.</summary>
        static void CopyLook(Material source, Material target)
        {
            if (source == null) return;
            foreach (string texture in new[] { "_BaseMap", "_BaseColorMap", "_MainTex" })
            {
                if (!source.HasProperty(texture) || source.GetTexture(texture) == null) continue;
                var map = source.GetTexture(texture);
                if (target.HasProperty("_BaseMap")) target.SetTexture("_BaseMap", map);
                else target.mainTexture = map;
                break;
            }
            var tint = source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor")
                : source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
            if (target.HasProperty("_BaseColor")) target.SetColor("_BaseColor", tint);
            else target.color = tint;
        }

        static Material ToInstancedUrp(Material source, Material litTemplate)
        {
            if (source == null) return litTemplate;
            if (converted.TryGetValue(source, out var material) && material != null) return material;
            if (source.shader != null && source.shader.name.StartsWith("Universal Render Pipeline"))
            {
                material = new Material(source);
            }
            else
            {
                // Built-in/HDRP material: rebuild it on URP Lit with the same texture and colour.
                material = new Material(litTemplate);
                CopyLook(source, material);
            }
            material.enableInstancing = true;
            return converted[source] = material;
        }
    }

    /// <summary>
    /// Draws the crowd on the GPU: coloured cubes, static person models, or animated person models (VAT),
    /// plus a state-coloured tile under each person when models are used.
    /// </summary>
    public class CrowdRenderer : System.IDisposable
    {
        const int Batch = 1023;
        const int StateCount = 6;
        const float WalkStride = 1.4f, RunStride = 2.6f; // metres per animation cycle

        [StructLayout(LayoutKind.Sequential)]
        struct CrowdInstance
        {
            public Matrix4x4 objectToWorld;
            public Vector4 anim; // frame in clip, first row, frames in clip, unused
        }

        class Group
        {
            public PersonModel model;
            public RenderParams[] staticParams;
            public RenderParams[] animatedParams;
            public Matrix4x4[] matrices;
            public CrowdInstance[] instances;
            public GraphicsBuffer buffer;
            public int count;
        }

        readonly CrowdSimulation sim;
        readonly Mesh cube;
        readonly RenderParams[] stateParams;
        readonly Group[] groups;
        readonly int[] modelOf;
        readonly float[] facing, animClock;
        readonly bool[] cheers;
        readonly Matrix4x4[][] cubes, markers;
        readonly int[] cubeCounts, markerCounts;

        public bool UsesModels => groups.Length > 0;
        public bool Animated { get; }
        public bool ShowMarkers { get; set; } = true;

        /// <param name="stateParams">Calm, following directions, alarmed, panicking, injured, panic attack.</param>
        public CrowdRenderer(CrowdSimulation sim, Mesh cube, RenderParams[] stateParams, IReadOnlyList<PersonModel> models, Bounds bounds, System.Random rng)
        {
            this.sim = sim;
            this.cube = cube;
            this.stateParams = stateParams;
            modelOf = new int[sim.count];
            facing = new float[sim.count]; // 0 = facing +Z, towards the stage
            animClock = new float[sim.count];
            cheers = new bool[sim.count];
            for (int i = 0; i < sim.count; i++)
            {
                modelOf[i] = models.Count > 0 ? rng.Next(models.Count) : 0;
                animClock[i] = (float)rng.NextDouble();
                cheers[i] = rng.NextDouble() < 0.35; // some fans are dancing/waving at the show
            }

            var perModel = new int[Mathf.Max(1, models.Count)];
            for (int i = 0; i < sim.count; i++) perModel[modelOf[i]]++;
            groups = new Group[models.Count];
            Animated = models.Count > 0;
            for (int m = 0; m < models.Count; m++)
            {
                var model = models[m];
                var group = groups[m] = new Group { model = model, matrices = new Matrix4x4[perModel[m]] };
                group.staticParams = new RenderParams[model.staticMaterials.Length];
                for (int s = 0; s < group.staticParams.Length; s++)
                    group.staticParams[s] = new RenderParams(model.staticMaterials[s]) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
                if (!model.Animated)
                {
                    Animated = false;
                    continue;
                }
                group.instances = new CrowdInstance[Mathf.Max(1, perModel[m])];
                group.buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, group.instances.Length, Marshal.SizeOf<CrowdInstance>());
                var properties = new MaterialPropertyBlock();
                properties.SetBuffer("_Instances", group.buffer);
                group.animatedParams = new RenderParams[model.animatedMaterials.Length];
                for (int s = 0; s < group.animatedParams.Length; s++)
                    group.animatedParams[s] = new RenderParams(model.animatedMaterials[s]) { worldBounds = bounds, matProps = properties, shadowCastingMode = ShadowCastingMode.Off };
            }

            cubes = Allocate(StateCount, sim.count);
            cubeCounts = new int[StateCount];
            markers = Allocate(StateCount, sim.count);
            markerCounts = new int[StateCount];
        }

        static Matrix4x4[][] Allocate(int groups, int size)
        {
            var arrays = new Matrix4x4[groups][];
            for (int g = 0; g < groups; g++) arrays[g] = new Matrix4x4[size];
            return arrays;
        }

        public void Render(float dt)
        {
            System.Array.Clear(cubeCounts, 0, cubeCounts.Length);
            System.Array.Clear(markerCounts, 0, markerCounts.Length);
            foreach (var g in groups) g.count = 0;
            var cubeUpright = new Vector3(0.42f, 1f, 0.42f);
            var cubeLying = new Vector3(0.42f, 0.18f, 0.9f);
            var marker = new Vector3(0.7f, 0.04f, 0.7f);
            float turn = Mathf.Min(1f, 8f * dt);

            for (int i = 0; i < sim.count; i++)
            {
                int s = sim.DisplayState(i);
                if (s < 0) continue;
                Vector2 p = sim.position[i], v = sim.velocity[i];
                float speed = v.magnitude;
                if (speed > 0.15f) facing[i] = Mathf.LerpAngle(facing[i], Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg, turn);
                bool down = s == 4, attack = s == 5;

                if (!UsesModels)
                {
                    var scale = down ? cubeLying : attack ? new Vector3(0.5f, 0.55f, 0.5f) : cubeUpright;
                    cubes[s][cubeCounts[s]++] = Matrix4x4.TRS(new Vector3(p.x, scale.y * 0.5f, p.y), Quaternion.Euler(0f, facing[i], 0f), scale);
                    continue;
                }

                var rotation = Quaternion.Euler(0f, facing[i], 0f);
                var bodyScale = Vector3.one;
                float lift = 0f;
                if (down)
                {
                    rotation *= Quaternion.Euler(-90f, 0f, 0f); // lying on their back
                    lift = 0.15f;
                }
                else if (attack)
                {
                    bodyScale = new Vector3(1.1f, 0.6f, 1.1f); // doubled over
                }

                var group = groups[modelOf[i]];
                var matrix = Matrix4x4.TRS(new Vector3(p.x, lift, p.y), rotation, bodyScale);
                if (Animated)
                {
                    var clip = PersonModel.Clip.Idle;
                    float cyclesPerSecond = 0f;
                    if (!down && !attack)
                    {
                        bool running = speed > 2f || (sim.panic[i] > 0.7f && speed > 0.5f);
                        if (speed > 0.2f)
                        {
                            clip = running ? PersonModel.Clip.Run : PersonModel.Clip.Walk;
                            cyclesPerSecond = speed / (running ? RunStride : WalkStride);
                        }
                        else
                        {
                            // Fans enjoying the show cheer until they realise what's happening.
                            clip = cheers[i] && !sim.evacuating[i] ? PersonModel.Clip.Wave : PersonModel.Clip.Idle;
                            cyclesPerSecond = 1f / group.model.clipLength[(int)clip];
                        }
                    }
                    else if (attack)
                    {
                        cyclesPerSecond = 2f / group.model.clipLength[(int)clip]; // trembling
                    }
                    animClock[i] = Mathf.Repeat(animClock[i] + dt * cyclesPerSecond, 1f);
                    int k = (int)clip;
                    group.instances[group.count++] = new CrowdInstance
                    {
                        objectToWorld = matrix,
                        anim = new Vector4(animClock[i] * group.model.clipFrames[k], group.model.clipStart[k], group.model.clipFrames[k], 0f),
                    };
                }
                else
                {
                    group.matrices[group.count++] = matrix;
                }
                if (ShowMarkers) markers[s][markerCounts[s]++] = Matrix4x4.TRS(new Vector3(p.x, 0.03f, p.y), Quaternion.identity, marker);
            }

            if (!UsesModels)
            {
                for (int s = 0; s < StateCount; s++) Draw(stateParams[s], cube, 0, cubes[s], cubeCounts[s]);
                return;
            }
            foreach (var group in groups)
            {
                if (group.count == 0) continue;
                if (Animated)
                {
                    group.buffer.SetData(group.instances, 0, 0, group.count);
                    for (int sub = 0; sub < group.animatedParams.Length; sub++)
                        Graphics.RenderMeshPrimitives(group.animatedParams[sub], group.model.mesh, sub, group.count);
                }
                else
                {
                    for (int sub = 0; sub < group.staticParams.Length; sub++)
                        Draw(group.staticParams[sub], group.model.mesh, sub, group.matrices, group.count);
                }
            }
            for (int s = 0; s < StateCount; s++) Draw(stateParams[s], cube, 0, markers[s], markerCounts[s]);
        }

        static void Draw(in RenderParams rp, Mesh mesh, int submesh, Matrix4x4[] matrices, int count)
        {
            for (int start = 0; start < count; start += Batch)
                Graphics.RenderMeshInstanced(rp, mesh, submesh, matrices, Mathf.Min(Batch, count - start), start);
        }

        public void Dispose()
        {
            foreach (var group in groups) group.buffer?.Release();
        }
    }
}
