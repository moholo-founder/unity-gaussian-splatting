#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderer))]
    public class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        private RuntimePlyLoader _loader;

        private void OnEnable()
        {
            if (Application.isPlaying && _pendingRequest != null)
            {
                EditorApplication.update -= UpdateUrlLoad;
                CleanupRequest();
                return;
            }
            
            if (target != null && target is GaussianSplatRenderer renderer)
            {
                _loader = renderer.GetComponent<RuntimePlyLoader>();
            }
        }

        private void OnDisable()
        {
            if (_pendingRequest != null)
            {
                EditorApplication.update -= UpdateUrlLoad;
                CleanupRequest();
            }
        }

        public override void OnInspectorGUI()
        {
            if (target == null || !(target is GaussianSplatRenderer renderer))
                return;
                
            serializedObject.Update();

            EditorGUILayout.LabelField("Gaussian Splat Renderer", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            if (_loader != null)
            {
                EditorGUILayout.HelpBox("This renderer is controlled by a RuntimePlyLoader component. Configure loading there.", MessageType.Info);
                
                if (_loader.IsLoaded)
                {
                    var data = renderer.GetSplatData();
                    if (data != null)
                    {
                        EditorGUILayout.LabelField($"Loaded: {data.Count} splats");
                    }
                }
                else if (_loader.IsLoading)
                {
                    EditorGUILayout.LabelField("Loading...");
                }
                
                EditorGUILayout.Space();
            }
            else
            {
                EditorGUILayout.HelpBox("Add a RuntimePlyLoader component to load PLY files, or call SetSplatData() from code.", MessageType.Info);
                
                if (GUILayout.Button("Add RuntimePlyLoader"))
                {
                    renderer.gameObject.AddComponent<RuntimePlyLoader>();
                    _loader = renderer.GetComponent<RuntimePlyLoader>();
                }
                
                EditorGUILayout.Space();
            }

            var data2 = renderer.GetSplatData();
            if (data2 != null)
            {
                EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Splat Count: {data2.Count}");
                EditorGUILayout.LabelField($"Is Loaded: {renderer.IsLoaded}");
                EditorGUILayout.Space();
            }

            DrawPropertiesExcluding(serializedObject, "m_Script");

            serializedObject.ApplyModifiedProperties();
        }

        private UnityWebRequest _pendingRequest;
        private GaussianSplatRenderer _pendingRenderer;
        private string _pendingUrl;

        private void UpdateUrlLoad()
        {
            if (_pendingRequest == null)
            {
                EditorApplication.update -= UpdateUrlLoad;
                return;
            }

            if (_pendingRenderer == null)
            {
                EditorApplication.update -= UpdateUrlLoad;
                EditorUtility.ClearProgressBar();
                CleanupRequest();
                return;
            }

            if (!_pendingRequest.isDone)
            {
                EditorUtility.DisplayProgressBar("Loading PLY from URL", 
                    $"Downloading {_pendingUrl}... {_pendingRequest.downloadProgress * 100:F1}%", 
                    0.1f + _pendingRequest.downloadProgress * 0.5f);
                return;
            }

            EditorApplication.update -= UpdateUrlLoad;
            ProcessUrlLoadResult();
        }

        private void ProcessUrlLoadResult()
        {
            EditorUtility.DisplayProgressBar("Loading PLY from URL", "Processing...", 0.7f);

            try
            {
                if (_pendingRenderer == null)
                {
                    EditorUtility.ClearProgressBar();
                    CleanupRequest();
                    return;
                }

                if (_pendingRequest.result == UnityWebRequest.Result.ConnectionError || 
                    _pendingRequest.result == UnityWebRequest.Result.ProtocolError)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error Loading PLY", 
                        $"Failed to download PLY from URL:\n{_pendingUrl}\n\nError: {_pendingRequest.error}", "OK");
                    CleanupRequest();
                    return;
                }

                byte[] fileBytes = _pendingRequest.downloadHandler.data;
                
                if (fileBytes == null || fileBytes.Length == 0)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error Loading PLY", "Downloaded file is empty", "OK");
                    CleanupRequest();
                    return;
                }

                if (fileBytes.Length < 3)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error Loading PLY", "Downloaded file is too small to be a valid PLY file", "OK");
                    CleanupRequest();
                    return;
                }

                string headerStart = System.Text.Encoding.ASCII.GetString(fileBytes, 0, Math.Min(3, fileBytes.Length));
                if (!headerStart.Equals("ply", StringComparison.OrdinalIgnoreCase))
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error Loading PLY", 
                        $"File is not a PLY file. PLY files must start with 'ply' header.", "OK");
                    CleanupRequest();
                    return;
                }

                EditorUtility.DisplayProgressBar("Loading PLY from URL", "Parsing PLY data...", 0.8f);

                var plyData = PlyGaussianSplatLoader.Load(fileBytes, CoordinateConversion.RightHandedToUnity);
                
                if (_pendingRenderer == null)
                {
                    EditorUtility.ClearProgressBar();
                    CleanupRequest();
                    return;
                }

                _pendingRenderer.SetSplatData(plyData);
                
                Debug.Log($"[Editor] Loaded PLY from URL with {plyData.Count} splats");
                
                EditorUtility.SetDirty(_pendingRenderer);
                SceneView.RepaintAll();
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Error Parsing PLY", 
                    $"Failed to parse PLY file:\n\nError: {ex.Message}", "OK");
            }
            finally
            {
                CleanupRequest();
            }
        }

        private void CleanupRequest()
        {
            if (_pendingRequest != null)
            {
                _pendingRequest.Dispose();
                _pendingRequest = null;
            }
            _pendingRenderer = null;
            _pendingUrl = null;
            EditorUtility.ClearProgressBar();
        }
    }
}
#endif
