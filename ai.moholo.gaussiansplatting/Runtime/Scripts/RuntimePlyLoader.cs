using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace GaussianSplatting
{
    /// <summary>
    /// Loads PLY files at runtime from StreamingAssets using UnityWebRequest.
    /// This is necessary for Android builds where direct file access is not available.
    /// </summary>
    [ExecuteAlways]
    public class RuntimePlyLoader : MonoBehaviour
    {
        [Header("PLY File Settings")]
        [Tooltip("Name of the PLY file in StreamingAssets/GaussianSplatting/ folder (e.g., 'testsplat.ply')")]
        public string plyFileName = "testsplat.ply";

        [Header("Auto-Load Settings")]
        [Tooltip("Automatically load the PLY file on Start")]
        public bool loadOnStart = true;

        [Header("Target Renderer")]
        [Tooltip("Optional: GaussianSplatRenderer to automatically assign the loaded asset to")]
        public GaussianSplatRenderer targetRenderer;

        [Header("Status")]
        [SerializeField]
        private bool _isLoading = false;
        [SerializeField]
        private bool _isLoaded = false;
        [SerializeField]
        private string _loadError = "";

        private GaussianSplatAsset _loadedAsset;

        public bool IsLoading => _isLoading;
        public bool IsLoaded => _isLoaded;
        public string LoadError => _loadError;
        public GaussianSplatAsset LoadedAsset => _loadedAsset;

        void Start()
        {
            if (loadOnStart && Application.isPlaying)
            {
                LoadPlyFile();
            }
        }

        /// <summary>
        /// Load the PLY file specified in plyFileName.
        /// </summary>
        public void LoadPlyFile()
        {
            if (string.IsNullOrEmpty(plyFileName))
            {
                Debug.LogError("[RuntimePlyLoader] PLY file name is empty.");
                _loadError = "PLY file name is empty";
                return;
            }

            StartCoroutine(LoadPlyFileCoroutine());
        }

        /// <summary>
        /// Load a specific PLY file by name.
        /// </summary>
        public void LoadPlyFile(string fileName)
        {
            plyFileName = fileName;
            LoadPlyFile();
        }

        private IEnumerator LoadPlyFileCoroutine()
        {
            _isLoading = true;
            _isLoaded = false;
            _loadError = "";

            // StreamingAssets path for Gaussian Splatting
            string filePath = Path.Combine(Application.streamingAssetsPath, "GaussianSplatting", plyFileName);
            Debug.Log($"[RuntimePlyLoader] Loading PLY from: {filePath}");

            byte[] fileBytes = null;

            // For Android, iOS, and WebGL, StreamingAssets path contains "://" 
            // and requires UnityWebRequest
            if (filePath.Contains("://") || Application.platform == RuntimePlatform.Android)
            {
                using (UnityWebRequest uwr = UnityWebRequest.Get(filePath))
                {
                    yield return uwr.SendWebRequest();

                    if (uwr.result == UnityWebRequest.Result.ConnectionError || 
                        uwr.result == UnityWebRequest.Result.ProtocolError)
                    {
                        _loadError = $"Error loading PLY file: {uwr.error}";
                        Debug.LogError($"[RuntimePlyLoader] {_loadError}");
                        _isLoading = false;
                        yield break;
                    }

                    fileBytes = uwr.downloadHandler.data;
                }
            }
            else
            {
                // For Editor and Standalone builds on desktop platforms
                if (!File.Exists(filePath))
                {
                    _loadError = $"PLY file not found: {filePath}";
                    Debug.LogError($"[RuntimePlyLoader] {_loadError}");
                    _isLoading = false;
                    yield break;
                }

                try
                {
                    fileBytes = File.ReadAllBytes(filePath);
                }
                catch (System.Exception ex)
                {
                    _loadError = $"Error reading PLY file: {ex.Message}";
                    Debug.LogError($"[RuntimePlyLoader] {_loadError}");
                    _isLoading = false;
                    yield break;
                }
            }

            if (fileBytes == null || fileBytes.Length == 0)
            {
                _loadError = "PLY file is empty";
                Debug.LogError($"[RuntimePlyLoader] {_loadError}");
                _isLoading = false;
                yield break;
            }

            Debug.Log($"[RuntimePlyLoader] Loaded {fileBytes.Length} bytes, parsing PLY data...");

            // Parse PLY data on a separate frame to avoid blocking
            yield return null;

            PlyGaussianSplat plyData = null;
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

            Debug.Log($"[RuntimePlyLoader] Parsed {plyData.Count} splats, creating asset...");

            // Create runtime asset
            _loadedAsset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            _loadedAsset.name = Path.GetFileNameWithoutExtension(plyFileName);
            _loadedAsset.Count = plyData.Count;
            _loadedAsset.Centers = plyData.Centers;
            _loadedAsset.Rotations = plyData.Rotations;
            _loadedAsset.Scales = plyData.Scales;
            _loadedAsset.Colors = plyData.Colors;
            _loadedAsset.ShBands = plyData.ShBands;
            _loadedAsset.ShCoeffsPerSplat = plyData.ShCoeffsPerSplat;
            _loadedAsset.ShCoeffs = plyData.ShCoeffs;

            _isLoaded = true;
            _isLoading = false;

            Debug.Log($"[RuntimePlyLoader] Successfully loaded '{plyFileName}' with {plyData.Count} splats");

            // Auto-assign to target renderer if specified
            if (targetRenderer != null)
            {
                Debug.Log($"[RuntimePlyLoader] Assigning asset to renderer");
                targetRenderer.PlyAsset = _loadedAsset;
            }
        }

        private void OnDestroy()
        {
            // Clean up runtime-created asset
            if (_loadedAsset != null && Application.isPlaying)
            {
                Destroy(_loadedAsset);
                _loadedAsset = null;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // In editor, auto-find renderer if not set
            if (targetRenderer == null)
            {
                targetRenderer = GetComponent<GaussianSplatRenderer>();
            }
        }
#endif
    }
}

