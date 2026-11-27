using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CrushHour.EditorTools
{
    /// <summary>
    /// The night sky (CrushHourGame.Sky.cs) uses the built-in Skybox/Panoramic shader, created from code, so nothing in a
    /// scene references it and a build would strip it. On every editor load this makes sure it (and Skybox/Procedural,
    /// the day sky) is in Project Settings > Graphics > Always Included Shaders. Without it the night falls back to a dark
    /// procedural sky.
    /// </summary>
    [InitializeOnLoad]
    static class SkyboxShaderInclude
    {
        static readonly string[] Shaders = { "Skybox/Panoramic", "Skybox/Procedural" };

        static SkyboxShaderInclude() => EditorApplication.delayCall += Ensure;

        static void Ensure()
        {
            var settings = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            if (settings == null) return;
            var serialized = new SerializedObject(settings);
            var list = serialized.FindProperty("m_AlwaysIncludedShaders");
            if (list == null) return;
            bool changed = false;
            foreach (string name in Shaders)
            {
                var shader = Shader.Find(name);
                if (shader == null) continue;
                bool present = false;
                for (int i = 0; i < list.arraySize; i++)
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) { present = true; break; }
                if (present) continue;
                list.arraySize++;
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                changed = true;
            }
            if (!changed) return;
            serialized.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            Debug.Log("Crush Hour: added the skybox shaders to Always Included Shaders (night sky in builds).");
        }
    }
}
