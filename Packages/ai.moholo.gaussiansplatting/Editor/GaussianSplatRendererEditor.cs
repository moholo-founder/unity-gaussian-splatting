#if UNITY_EDITOR
using System;
using System.Collections;
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
        private string[] _availablePlyFiles;
        private int _selectedIndex = 0;

        private void OnEnable()
        {
            RefreshPlyFiles();
            
            // Cancel any pending requests when entering Play mode
            if (Application.isPlaying && _pendingRequest != null)
            {
                EditorApplication.update -= UpdateUrlLoad;
                CleanupRequest();
                return;
            }
            
            // Auto-load PLY in editor if not already loaded (only in Edit mode)
            if (target != null && target is GaussianSplatRenderer renderer)
            {
                if (!Application.isPlaying && renderer.PlyAsset == null)
                {
                    // Reload from URL if set
                    if (!string.IsNullOrEmpty(renderer.PlyUrl))
                    {
                        LoadPlyFromUrl(renderer);
                    }
                    // Reload from local file if URL is empty but filename is set
                    else if (!string.IsNullOrEmpty(renderer.PlyFileName))
                    {
                        LoadPlyInEditor(renderer);
                    }
                }
            }
        }

        private void OnDisable()
        {
            // Cancel any pending URL loads when inspector is closed or entering Play mode
            if (_pendingRequest != null)
            {
                EditorApplication.update -= UpdateUrlLoad;
                CleanupRequest();
            }
        }

        private void RefreshPlyFiles()
        {
            string streamingAssetsPath = Path.Combine(Application.dataPath, "StreamingAssets", "GaussianSplatting");
            
            if (!Directory.Exists(streamingAssetsPath))
            {
                _availablePlyFiles = new string[0];
                return;
            }

            var plyFiles = Directory.GetFiles(streamingAssetsPath, "*.ply")
                .Select(Path.GetFileName)
                .OrderBy(f => f)
                .ToArray();

            _availablePlyFiles = plyFiles;

            // Find current selection - use serializedObject to avoid cast issues
            if (target != null && target is GaussianSplatRenderer renderer)
            {
                _selectedIndex = System.Array.IndexOf(_availablePlyFiles, renderer.PlyFileName);
                if (_selectedIndex < 0) _selectedIndex = 0;
            }
        }

        public override void OnInspectorGUI()
        {
            if (target == null || !(target is GaussianSplatRenderer renderer))
                return;
                
            serializedObject.Update();

            // Header
            EditorGUILayout.LabelField("Input - StreamingAssets or URL", EditorStyles.boldLabel);

            // PLY URL field (takes precedence)
            EditorGUI.BeginChangeCheck();
            string previousUrl = renderer.PlyUrl;
            renderer.PlyUrl = EditorGUILayout.TextField("PLY URL (prioritized)", renderer.PlyUrl);
            bool urlChanged = EditorGUI.EndChangeCheck();
            
            if (urlChanged && previousUrl != renderer.PlyUrl)
            {
                EditorUtility.SetDirty(renderer);
                
                // If URL is set and changed in play mode, trigger reload
                if (Application.isPlaying && !string.IsNullOrEmpty(renderer.PlyUrl))
                {
                    Debug.Log($"[Editor] URL changed to '{renderer.PlyUrl}', will reload in play mode");
                }
                // If URL is set in editor mode, load it
                else if (!Application.isPlaying && !string.IsNullOrEmpty(renderer.PlyUrl))
                {
                    LoadPlyFromUrl(renderer);
                }
            }
            
            // Show info if URL is being used
            if (!string.IsNullOrEmpty(renderer.PlyUrl))
            {
                EditorGUILayout.HelpBox($"Loading from URL: {renderer.PlyUrl}\n(URL takes precedence over file dropdown)", MessageType.Info);
            }

            EditorGUILayout.Space();

            // PLY File Selector (used only if URL is empty)
            EditorGUI.BeginDisabledGroup(!string.IsNullOrEmpty(renderer.PlyUrl));
            EditorGUI.BeginChangeCheck();
            
            if (_availablePlyFiles.Length == 0)
            {
                EditorGUILayout.HelpBox("No PLY files found in StreamingAssets/GaussianSplatting/\n\nPlace your .ply files in: Assets/StreamingAssets/GaussianSplatting/", MessageType.Warning);
                renderer.PlyFileName = "testsplat.ply";
            }
            else
            {
                _selectedIndex = EditorGUILayout.Popup("PLY File", _selectedIndex, _availablePlyFiles);
                string newFileName = _availablePlyFiles[_selectedIndex];
                
                // If file changed, trigger reload (only if URL is empty)
                if (renderer.PlyFileName != newFileName && string.IsNullOrEmpty(renderer.PlyUrl))
                {
                    renderer.PlyFileName = newFileName;
                    
                    // Auto-load in editor mode
                    if (!Application.isPlaying)
                    {
                        LoadPlyInEditor(renderer);
                    }
                }
                else
                {
                    renderer.PlyFileName = newFileName;
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(renderer);
            }
            
            EditorGUI.EndDisabledGroup();

            // Refresh button
            if (GUILayout.Button("Refresh PLY Files List"))
            {
                RefreshPlyFiles();
            }

            // Show loaded status
            if (renderer.PlyAsset != null)
            {
                EditorGUILayout.HelpBox($"✓ Loaded: {renderer.PlyAsset.name} ({renderer.PlyAsset.Count} splats)", MessageType.Info);
            }

            EditorGUILayout.Space();

            // Rendering settings (excluding header since DrawPropertiesExcluding will show it)
            DrawPropertiesExcluding(serializedObject, "m_Script", "PlyUrl", "PlyFileName", "PlyAsset");

            serializedObject.ApplyModifiedProperties();
        }

        private void LoadPlyInEditor(GaussianSplatRenderer renderer)
        {
            if (string.IsNullOrEmpty(renderer.PlyFileName))
            {
                EditorUtility.DisplayDialog("Error", "PLY File Name is empty", "OK");
                return;
            }

            string filePath = Path.Combine(Application.streamingAssetsPath, "GaussianSplatting", renderer.PlyFileName);
            
            if (!File.Exists(filePath))
            {
                EditorUtility.DisplayDialog("Error", $"PLY file not found:\n{filePath}", "OK");
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Loading PLY", $"Reading {renderer.PlyFileName}...", 0.3f);

                byte[] fileBytes = File.ReadAllBytes(filePath);
                
                EditorUtility.DisplayProgressBar("Loading PLY", "Parsing PLY data...", 0.6f);
                
                var plyData = PlyGaussianSplatLoader.Load(fileBytes, CoordinateConversion.RightHandedToUnity);
                
                EditorUtility.DisplayProgressBar("Loading PLY", "Creating asset...", 0.9f);
                
                // Create runtime asset
                var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
                asset.name = Path.GetFileNameWithoutExtension(renderer.PlyFileName);
                asset.Count = plyData.Count;
                asset.Centers = plyData.Centers;
                asset.Rotations = plyData.Rotations;
                asset.Scales = plyData.Scales;
                asset.Colors = plyData.Colors;
                asset.ShBands = plyData.ShBands;
                asset.ShCoeffsPerSplat = plyData.ShCoeffsPerSplat;
                asset.ShCoeffs = plyData.ShCoeffs;

                renderer.PlyAsset = asset;
                
                Debug.Log($"[GaussianSplatRenderer] Loaded '{renderer.PlyFileName}' in Editor with {plyData.Count} splats");
                
                // Force immediate GPU buffer creation
                if (renderer.isActiveAndEnabled)
                {
                    renderer.LoadAssetToGPU();
                }
                
                EditorUtility.SetDirty(renderer);
                SceneView.RepaintAll();
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Error Loading PLY", $"Failed to load {renderer.PlyFileName}:\n\n{ex.Message}", "OK");
                Debug.LogError($"Error loading PLY in Editor: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private UnityWebRequest _pendingRequest;
        private GaussianSplatRenderer _pendingRenderer;
        private string _pendingUrl;

        private void LoadPlyFromUrl(GaussianSplatRenderer renderer)
        {
            if (string.IsNullOrEmpty(renderer.PlyUrl))
            {
                EditorUtility.DisplayDialog("Error", "PLY URL is empty", "OK");
                return;
            }

            if (_pendingRequest != null)
            {
                EditorUtility.DisplayDialog("Loading in Progress", "A PLY file is already being loaded. Please wait.", "OK");
                return;
            }

            // Start async download using EditorApplication.update
            _pendingUrl = renderer.PlyUrl;
            _pendingRenderer = renderer;
            _pendingRequest = UnityWebRequest.Get(_pendingUrl);
            _pendingRequest.SendWebRequest();
            
            EditorApplication.update += UpdateUrlLoad;
            Debug.Log($"[Editor] Started loading PLY from URL: {_pendingUrl}");
        }

        private void UpdateUrlLoad()
        {
            if (_pendingRequest == null)
            {
                EditorApplication.update -= UpdateUrlLoad;
                return;
            }

            // Check if renderer was destroyed (e.g., when entering Play mode)
            if (_pendingRenderer == null)
            {
                EditorApplication.update -= UpdateUrlLoad;
                EditorUtility.ClearProgressBar();
                Debug.LogWarning("[Editor] Renderer was destroyed, cancelling PLY load");
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

            // Request is done, process it
            EditorApplication.update -= UpdateUrlLoad;
            ProcessUrlLoadResult();
        }

        private void ProcessUrlLoadResult()
        {
            EditorUtility.DisplayProgressBar("Loading PLY from URL", "Processing...", 0.7f);

            try
            {
                // Check if renderer still exists (might be destroyed when entering Play mode)
                if (_pendingRenderer == null)
                {
                    EditorUtility.ClearProgressBar();
                    Debug.LogWarning("[Editor] Renderer was destroyed, cancelling PLY load");
                    CleanupRequest();
                    return;
                }

                if (_pendingRequest.result == UnityWebRequest.Result.ConnectionError || 
                    _pendingRequest.result == UnityWebRequest.Result.ProtocolError)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error Loading PLY", 
                        $"Failed to download PLY from URL:\n{_pendingUrl}\n\nError: {_pendingRequest.error}", "OK");
                    Debug.LogError($"[Editor] Error loading PLY from URL: {_pendingRequest.error}");
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

                // Validate PLY header
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
                        $"File is not a PLY file. File extension: '{Path.GetExtension(_pendingUrl)}'. PLY files must start with 'ply' header.", "OK");
                    CleanupRequest();
                    return;
                }

                EditorUtility.DisplayProgressBar("Loading PLY from URL", "Parsing PLY data...", 0.8f);

                var plyData = PlyGaussianSplatLoader.Load(fileBytes, CoordinateConversion.RightHandedToUnity);
                
                EditorUtility.DisplayProgressBar("Loading PLY from URL", "Creating asset...", 0.9f);

                // Check again if renderer still exists before assigning asset
                if (_pendingRenderer == null)
                {
                    EditorUtility.ClearProgressBar();
                    Debug.LogWarning("[Editor] Renderer was destroyed during PLY parsing, cancelling asset assignment");
                    CleanupRequest();
                    return;
                }

                // Create runtime asset
                var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
                string assetName = Path.GetFileNameWithoutExtension(_pendingUrl);
                if (string.IsNullOrEmpty(assetName) || assetName == _pendingUrl)
                    assetName = "url_splat";
                asset.name = assetName;
                asset.Count = plyData.Count;
                asset.Centers = plyData.Centers;
                asset.Rotations = plyData.Rotations;
                asset.Scales = plyData.Scales;
                asset.Colors = plyData.Colors;
                asset.ShBands = plyData.ShBands;
                asset.ShCoeffsPerSplat = plyData.ShCoeffsPerSplat;
                asset.ShCoeffs = plyData.ShCoeffs;

                _pendingRenderer.PlyAsset = asset;
                
                Debug.Log($"[Editor] Loaded PLY from URL '{_pendingUrl}' in Editor with {plyData.Count} splats");
                
                EditorUtility.SetDirty(_pendingRenderer);
                
                // Force immediate GPU buffer creation
                if (_pendingRenderer.isActiveAndEnabled)
                {
                    _pendingRenderer.LoadAssetToGPU();
                }
                
                SceneView.RepaintAll();
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Error Parsing PLY", 
                    $"Failed to parse PLY file from URL:\n{_pendingUrl}\n\nError: {ex.Message}", "OK");
                Debug.LogError($"Error parsing PLY in Editor: {ex.Message}\n{ex.StackTrace}");
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

