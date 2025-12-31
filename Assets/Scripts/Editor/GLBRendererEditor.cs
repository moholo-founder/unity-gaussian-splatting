#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GLBRenderer))]
public class GLBRendererEditor : Editor
{
    private string[] _availableGlbFiles;
    private int _selectedIndex = 0;

    private void OnEnable()
    {
        RefreshGlbFiles();
        // Note: glTFast uses DontDestroyOnLoad which doesn't work in Edit mode
        // Models will only load when entering Play mode
    }

    private void RefreshGlbFiles()
    {
        string streamingAssetsPath = Path.Combine(Application.dataPath, "StreamingAssets", "GLB");
        
        if (!Directory.Exists(streamingAssetsPath))
        {
            _availableGlbFiles = new string[0];
            return;
        }

        var glbFiles = Directory.GetFiles(streamingAssetsPath, "*.glb")
            .Concat(Directory.GetFiles(streamingAssetsPath, "*.gltf"))
            .Select(Path.GetFileName)
            .OrderBy(f => f)
            .ToArray();

        _availableGlbFiles = glbFiles;

        if (target != null && target is GLBRenderer renderer)
        {
            _selectedIndex = Array.IndexOf(_availableGlbFiles, renderer.GlbFileName);
            if (_selectedIndex < 0) _selectedIndex = 0;
        }
    }

    public override void OnInspectorGUI()
    {
        if (target == null || !(target is GLBRenderer renderer))
            return;
            
        serializedObject.Update();

        // Header
        EditorGUILayout.LabelField("Input - StreamingAssets or URL", EditorStyles.boldLabel);

        // GLB URL field
        var urlInstructionStyle = new GUIStyle(EditorStyles.helpBox);
        urlInstructionStyle.wordWrap = true;
        EditorGUILayout.LabelField("Provide URL to internet-hosted GLB/glTF file.", urlInstructionStyle);
        
        EditorGUI.BeginChangeCheck();
        string previousUrl = renderer.GlbUrl;
        renderer.GlbUrl = EditorGUILayout.TextField("GLB URL (disables Streaming Assets)", renderer.GlbUrl);
        bool urlChanged = EditorGUI.EndChangeCheck();
        
        EditorGUILayout.Space(2);
        var instructionStyle = new GUIStyle(EditorStyles.helpBox);
        instructionStyle.wordWrap = true;
        EditorGUILayout.LabelField("Place GLB/glTF files in Assets/StreamingAssets/GLB folder.", instructionStyle);
        EditorGUILayout.Space(2);
        
        if (urlChanged && previousUrl != renderer.GlbUrl)
        {
            EditorUtility.SetDirty(renderer);
        }
        
        if (!string.IsNullOrEmpty(renderer.GlbUrl))
        {
            EditorGUILayout.HelpBox($"Loading from URL: {renderer.GlbUrl}\n(URL disables Streaming Assets file dropdown)", MessageType.Info);
        }

        EditorGUILayout.Space();

        // GLB File Selector
        EditorGUI.BeginDisabledGroup(!string.IsNullOrEmpty(renderer.GlbUrl));
        EditorGUI.BeginChangeCheck();
        
        if (_availableGlbFiles.Length == 0)
        {
            EditorGUILayout.HelpBox("No GLB/glTF files found in StreamingAssets/GLB/\n\nPlace your .glb or .gltf files in: Assets/StreamingAssets/GLB/", MessageType.Warning);
        }
        else
        {
            _selectedIndex = EditorGUILayout.Popup("GLB File", _selectedIndex, _availableGlbFiles);
            string newFileName = _availableGlbFiles[_selectedIndex];
            
            renderer.GlbFileName = newFileName;
        }

        if (EditorGUI.EndChangeCheck())
        {
            EditorUtility.SetDirty(renderer);
        }
        
        EditorGUI.EndDisabledGroup();

        // Refresh button
        if (GUILayout.Button("Refresh GLB list from StreamingAssets/GLB"))
        {
            RefreshGlbFiles();
        }

        EditorGUILayout.Space();

        // Show status based on play mode
        if (Application.isPlaying)
        {
            if (renderer.IsLoaded)
            {
                EditorGUILayout.HelpBox($"✓ Model loaded", MessageType.Info);
            }
        }
        else
        {
            string source = !string.IsNullOrEmpty(renderer.GlbUrl) ? renderer.GlbUrl : renderer.GlbFileName;
            if (!string.IsNullOrEmpty(source))
            {
                EditorGUILayout.HelpBox($"Model will load when entering Play mode.\nSource: {source}", MessageType.Info);
            }
        }

        serializedObject.ApplyModifiedProperties();
    }
}
#endif

