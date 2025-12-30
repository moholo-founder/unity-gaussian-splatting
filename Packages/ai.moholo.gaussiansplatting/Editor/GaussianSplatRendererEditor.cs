#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderer))]
    public class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (target == null || !(target is GaussianSplatRenderer renderer))
                return;

            serializedObject.Update();

            var splatData = renderer.GetSplatData();
            if (splatData != null)
            {
                EditorGUILayout.HelpBox($"Loaded: {splatData.Count:N0} splats", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("No splat data loaded. Use SetSplatData() to load splats programmatically.", MessageType.Warning);
            }

            EditorGUILayout.Space();

            DrawDefaultInspector();

            EditorGUILayout.Space();

            if (splatData != null && GUILayout.Button("Force Reload"))
            {
                renderer.ForceReload();
                EditorUtility.SetDirty(renderer);
                SceneView.RepaintAll();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
