using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace GaussianSplatting
{
    /// <summary>
    /// Loads PLY files at runtime from StreamingAssets or URL using UnityWebRequest.
    /// This is necessary for Android builds where direct file access is not available.
    /// </summary>
    [ExecuteAlways]
    public class RuntimePlyLoader : MonoBehaviour
    {
#if UNITY_EDITOR
        [Tooltip("Drag a PLY file here to automatically copy it to StreamingAssets/GaussianSplatting and set it as the source")]
        [SerializeField] private UnityEngine.Object _plyAsset;
#endif
        
        [Tooltip("URL or filename to load. URLs start with http:// or https://. Filenames are loaded from StreamingAssets/GaussianSplatting/ folder.")]
        [SerializeField] private string _plySource = "testsplat.ply";

        [Tooltip("Automatically load the PLY file on Start")]
        [SerializeField] private bool _loadOnStart = true;

        [Tooltip("GaussianSplatRenderer to assign the loaded data to")]
        [SerializeField] private GaussianSplatRenderer _targetRenderer;

        [SerializeField, HideInInspector] private bool _hadLoadedData = false;

        private bool _isLoading = false;
        private bool _isLoaded = false;
        private string _loadError = "";

        private GaussianSplatData _loadedData;
        private string _lastLoadedSource = "";

        public bool IsLoading => _isLoading;
        public bool IsLoaded => _isLoaded;
        public string LoadError => _loadError;
        public GaussianSplatData LoadedData => _loadedData;

        void Start()
        {
            if (_loadOnStart && Application.isPlaying)
            {
                LoadPly();
            }
        }
        
        private bool IsUrl(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            return source.StartsWith("http://") || source.StartsWith("https://");
        }

        /// <summary>
        /// Load the PLY from the current PlySource
        /// </summary>
        public void LoadPly()
        {
            if (string.IsNullOrEmpty(_plySource))
            {
                Debug.LogError("[RuntimePlyLoader] PlySource is empty.");
                _loadError = "PlySource is empty";
                return;
            }

            StartCoroutine(LoadPlyCoroutine());
        }

        /// <summary>
        /// Load a PLY from a specific URL or filename
        /// </summary>
        public void LoadPly(string urlOrFileName)
        {
            _plySource = urlOrFileName;
            LoadPly();
        }

        [ContextMenu("Force Reload")]
        public void ForceReload()
        {
            _lastLoadedSource = "";
            _loadedData = null;
            _isLoaded = false;
            LoadPly();
        }

        private IEnumerator LoadPlyCoroutine()
        {
            _isLoading = true;
            _isLoaded = false;
            _loadError = "";

            bool isUrl = IsUrl(_plySource);
            string loadPath;

            if (isUrl)
            {
                // Load directly from URL
                loadPath = _plySource;
                string urlExtension = Path.GetExtension(loadPath).ToLower();
                if (!string.IsNullOrEmpty(urlExtension) && urlExtension != ".ply")
                {
                    Debug.LogWarning($"[RuntimePlyLoader] URL has extension '{urlExtension}' but expected '.ply'.");
                }
            }
            else
            {
                // Load from StreamingAssets/GaussianSplatting/ folder
                loadPath = Path.Combine(Application.streamingAssetsPath, "GaussianSplatting", _plySource);
            }

            byte[] fileBytes = null;

            // Determine if we need to use UnityWebRequest
            bool useWebRequest = isUrl || loadPath.Contains("://") || Application.platform == RuntimePlatform.Android;
            
            // For local file paths on Android, prepend file:// for UnityWebRequest
            string requestUrl = loadPath;
            if (useWebRequest && !isUrl && !loadPath.Contains("://"))
            {
                requestUrl = "file://" + loadPath;
            }

            if (useWebRequest)
            {
                using (UnityWebRequest uwr = UnityWebRequest.Get(requestUrl))
                {
                    yield return uwr.SendWebRequest();

                    if (uwr.result == UnityWebRequest.Result.ConnectionError ||
                        uwr.result == UnityWebRequest.Result.ProtocolError)
                    {
                        _loadError = $"Error loading PLY: {uwr.error}";
                        Debug.LogError($"[RuntimePlyLoader] {_loadError} URL: {requestUrl}");
                        _isLoading = false;
                        yield break;
                    }

                    fileBytes = uwr.downloadHandler.data;
                }
            }
            else
            {
                // For Editor and Standalone builds on desktop platforms
                if (!File.Exists(loadPath))
                {
                    _loadError = $"PLY file not found: {loadPath}";
                    Debug.LogError($"[RuntimePlyLoader] {_loadError}");
                    _isLoading = false;
                    yield break;
                }

                try
                {
                    fileBytes = File.ReadAllBytes(loadPath);
                }
                catch (System.Exception ex)
                {
                    _loadError = $"Error reading PLY file: {ex.Message}";
                    Debug.LogError($"[RuntimePlyLoader] {_loadError}");
                    _isLoading = false;
                    yield break;
                }
            }

            // Validate file content
            if (fileBytes == null || fileBytes.Length == 0)
            {
                _loadError = "PLY file is empty";
                Debug.LogError($"[RuntimePlyLoader] {_loadError}");
                _isLoading = false;
                yield break;
            }

            if (fileBytes.Length < 3)
            {
                _loadError = "File is too small to be a valid PLY file";
                Debug.LogError($"[RuntimePlyLoader] {_loadError}");
                _isLoading = false;
                yield break;
            }

            // Check PLY header magic bytes (PLY files start with "ply")
            string headerStart = Encoding.ASCII.GetString(fileBytes, 0, System.Math.Min(3, fileBytes.Length));
            if (!headerStart.Equals("ply", System.StringComparison.OrdinalIgnoreCase))
            {
                _loadError = "File is not a PLY file (missing 'ply' header)";
                Debug.LogError($"[RuntimePlyLoader] {_loadError}");
                _isLoading = false;
                yield break;
            }

            // Parse PLY data on a separate frame to avoid blocking
            yield return null;

            GaussianSplatData plyData = null;
            try
            {
                // Always use RightHandedToUnity conversion
                plyData = PlyGaussianSplatLoader.Load(fileBytes, CoordinateConversion.RightHandedToUnity);
            }
            catch (System.Exception ex)
            {
                _loadError = $"Error parsing PLY file: {ex.Message}";
                Debug.LogError($"[RuntimePlyLoader] {_loadError}\n{ex.StackTrace}");
                _isLoading = false;
                yield break;
            }

            if (plyData == null || plyData.Count == 0)
            {
                _loadError = "PLY file contains no splats";
                Debug.LogError($"[RuntimePlyLoader] {_loadError}");
                _isLoading = false;
                yield break;
            }

            Debug.Log($"[RuntimePlyLoader] Parsed {plyData.Count} splats from '{_plySource}'");

            _loadedData = plyData;
            _lastLoadedSource = _plySource;
            _isLoaded = true;
            _isLoading = false;
            _hadLoadedData = true;

            Debug.Log($"[RuntimePlyLoader] Successfully loaded '{_plySource}' with {plyData.Count} splats");

            // Auto-assign to target renderer if specified
            if (_targetRenderer != null)
            {
                _targetRenderer.SetSplatData(_loadedData);
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_targetRenderer == null)
            {
                _targetRenderer = GetComponent<GaussianSplatRenderer>();
            }

            if (!Application.isPlaying && _hadLoadedData && _loadedData == null && !_isLoading)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (this != null && !_isLoading)
                        LoadPly();
                };
            }
        }
#endif
    }

#if UNITY_EDITOR
    [UnityEditor.CustomEditor(typeof(RuntimePlyLoader))]
    public class RuntimePlyLoaderEditor : UnityEditor.Editor
    {
        private bool _wasLoading = false;
        private UnityEditor.SerializedProperty _plyAssetProp;
        private UnityEditor.SerializedProperty _plySourceProp;

        private void OnEnable()
        {
            _wasLoading = false;
            _plyAssetProp = serializedObject.FindProperty("_plyAsset");
            _plySourceProp = serializedObject.FindProperty("_plySource");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            RuntimePlyLoader loader = (RuntimePlyLoader)target;

            UnityEditor.EditorGUI.BeginChangeCheck();
            UnityEditor.EditorGUILayout.PropertyField(_plyAssetProp, new GUIContent("PLY Asset", "Drag a PLY file here to copy to StreamingAssets"));
            if (UnityEditor.EditorGUI.EndChangeCheck() && _plyAssetProp.objectReferenceValue != null)
            {
                HandlePlyAssetAssigned(_plyAssetProp.objectReferenceValue, loader);
                _plyAssetProp.objectReferenceValue = null;
            }

            UnityEditor.EditorGUILayout.PropertyField(_plySourceProp);
            
            var iterator = serializedObject.GetIterator();
            iterator.NextVisible(true);
            while (iterator.NextVisible(false))
            {
                if (iterator.name == "_plyAsset" || iterator.name == "_plySource") continue;
                UnityEditor.EditorGUILayout.PropertyField(iterator, true);
            }
            
            serializedObject.ApplyModifiedProperties();

            UnityEditor.EditorGUILayout.Space();
            
            if (GUILayout.Button("Load Splat"))
            {
                loader.LoadPly();
            }

            UnityEditor.EditorGUILayout.Space();
            UnityEditor.EditorGUILayout.LabelField("Status", UnityEditor.EditorStyles.boldLabel);

            if (loader.IsLoading)
            {
                UnityEditor.EditorGUILayout.HelpBox("Loading...", UnityEditor.MessageType.Info);
                if (!_wasLoading)
                {
                    UnityEditor.EditorApplication.update += Repaint;
                    _wasLoading = true;
                }
            }
            else
            {
                if (_wasLoading)
                {
                    UnityEditor.EditorApplication.update -= Repaint;
                    _wasLoading = false;
                }

                if (!string.IsNullOrEmpty(loader.LoadError))
                {
                    UnityEditor.EditorGUILayout.HelpBox($"Error: {loader.LoadError}", UnityEditor.MessageType.Error);
                }
                else if (loader.IsLoaded && loader.LoadedData != null)
                {
                    UnityEditor.EditorGUILayout.HelpBox($"Loaded: {loader.LoadedData.Count} splats", UnityEditor.MessageType.Info);
                }
                else
                {
                    UnityEditor.EditorGUILayout.HelpBox("Not loaded", UnityEditor.MessageType.None);
                }
            }
        }

        private void HandlePlyAssetAssigned(UnityEngine.Object asset, RuntimePlyLoader loader)
        {
            string assetPath = UnityEditor.AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogWarning("[RuntimePlyLoader] Could not get asset path");
                return;
            }

            if (!assetPath.ToLower().EndsWith(".ply"))
            {
                Debug.LogWarning("[RuntimePlyLoader] Asset is not a PLY file");
                return;
            }

            string fileName = Path.GetFileName(assetPath);
            string streamingAssetsFolder = Path.Combine(Application.streamingAssetsPath, "GaussianSplatting");
            string targetPath = Path.Combine(streamingAssetsFolder, fileName);

            string normalizedAssetPath = assetPath.Replace("\\", "/");
            string normalizedTargetPath = targetPath.Replace("\\", "/");
            string streamingAssetsRelative = "Assets/StreamingAssets/GaussianSplatting/";

            bool alreadyInStreamingAssets = normalizedAssetPath.StartsWith(streamingAssetsRelative, System.StringComparison.OrdinalIgnoreCase);

            if (!alreadyInStreamingAssets)
            {
                if (!Directory.Exists(streamingAssetsFolder))
                {
                    Directory.CreateDirectory(streamingAssetsFolder);
                    UnityEditor.AssetDatabase.Refresh();
                }

                string sourcePath = Path.GetFullPath(assetPath);
                
                if (File.Exists(targetPath))
                {
                    Debug.Log($"[RuntimePlyLoader] PLY file already exists in StreamingAssets: {fileName}");
                }
                else
                {
                    File.Copy(sourcePath, targetPath);
                    UnityEditor.AssetDatabase.Refresh();
                    Debug.Log($"[RuntimePlyLoader] Copied PLY file to StreamingAssets: {fileName}");
                }
            }

            _plySourceProp.stringValue = fileName;
            serializedObject.ApplyModifiedProperties();

            string gameObjectName = Path.GetFileNameWithoutExtension(fileName);
            UnityEditor.Undo.RecordObject(loader.gameObject, "Rename to PLY filename");
            loader.gameObject.name = gameObjectName;

            Debug.Log($"[RuntimePlyLoader] Set PLY source to '{fileName}' and renamed GameObject to '{gameObjectName}'");

            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (loader != null)
                        loader.LoadPly();
                };
            }
        }

        private void OnDisable()
        {
            UnityEditor.EditorApplication.update -= Repaint;
        }
    }
#endif
}
