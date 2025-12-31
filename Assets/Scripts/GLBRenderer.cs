using System;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

[ExecuteAlways]
public class GLBRenderer : MonoBehaviour
{
    [Header("Input - StreamingAssets or URL")]
    [Tooltip("Full URL to load GLB from (disables Streaming Assets). Example: https://example.com/model.glb")]
    public string GlbUrl = "";
    
    [Tooltip("GLB filename in StreamingAssets/GLB/ folder (e.g., 'model.glb'). Used if GlbUrl is empty.")]
    public string GlbFileName = "";
    
    [System.NonSerialized]
    private string _lastLoadedSource = "";
    
    [System.NonSerialized]
    private GameObject _loadedModel;
    
    [System.NonSerialized]
    private GltfImport _gltfImport;

    private void OnEnable()
    {
        Debug.Log($"[GLBRenderer] OnEnable called. isPlaying={Application.isPlaying}, GlbUrl={GlbUrl}, GlbFileName={GlbFileName}");
        
        // Only auto-load in Play mode - glTFast uses DontDestroyOnLoad which doesn't work in Edit mode
        if (Application.isPlaying)
        {
            string currentSource = GetCurrentSource();
            if (_loadedModel == null && !string.IsNullOrEmpty(currentSource))
            {
                Debug.Log($"[GLBRenderer] Starting runtime load from '{currentSource}'");
                _ = LoadGlbAsync();
            }
        }
    }

    private void OnDisable()
    {
        if (Application.isPlaying)
        {
            Cleanup();
        }
    }

    private void OnDestroy()
    {
        Cleanup();
    }

    private void OnValidate()
    {
        if (!isActiveAndEnabled) return;
        
        string currentSource = GetCurrentSource();
        
        if (Application.isPlaying && currentSource != _lastLoadedSource && !string.IsNullOrEmpty(currentSource))
        {
            Debug.Log($"[GLBRenderer] Source changed from '{_lastLoadedSource}' to '{currentSource}', reloading...");
            Cleanup();
            _ = LoadGlbAsync();
        }
    }

    private string GetCurrentSource()
    {
        if (!string.IsNullOrEmpty(GlbUrl))
            return GlbUrl;
        return GlbFileName;
    }

    private void Cleanup()
    {
        if (_loadedModel != null)
        {
            if (Application.isPlaying)
                Destroy(_loadedModel);
            else
                DestroyImmediate(_loadedModel);
            _loadedModel = null;
        }
        
        _gltfImport?.Dispose();
        _gltfImport = null;
    }

    /// <summary>
    /// Public method for loading GLB at runtime
    /// </summary>
    public async Task LoadGlbAsync()
    {
        // glTFast uses DontDestroyOnLoad internally which doesn't work in Edit mode
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[GLBRenderer] GLB loading only works in Play mode. Enter Play mode to see the model.");
            return;
        }
        
        string source = GetCurrentSource();
        if (string.IsNullOrEmpty(source))
        {
            Debug.LogError("[GLBRenderer] Both GlbUrl and GlbFileName are empty. Cannot load GLB.");
            return;
        }

        string loadPath;
        bool isUrl = !string.IsNullOrEmpty(GlbUrl);
        
        if (isUrl)
        {
            loadPath = GlbUrl;
            Debug.Log($"[GLBRenderer] Loading GLB from URL: {loadPath}");
        }
        else
        {
            loadPath = Path.Combine(Application.streamingAssetsPath, "GLB", GlbFileName);
            Debug.Log($"[GLBRenderer] Loading GLB from StreamingAssets: {loadPath}");
        }

        try
        {
            Cleanup();
            
            _gltfImport = new GltfImport();
            
            bool success = await _gltfImport.Load(loadPath);
            
            if (!success)
            {
                Debug.LogError($"[GLBRenderer] Failed to load GLB from: {loadPath}");
                return;
            }

            // Check if component still exists
            if (this == null) return;

            // Instantiate the loaded model as a child of this GameObject
            success = await _gltfImport.InstantiateMainSceneAsync(transform);
            
            if (!success)
            {
                Debug.LogError($"[GLBRenderer] Failed to instantiate GLB model");
                return;
            }

            // Store reference to loaded model (the first child added)
            if (transform.childCount > 0)
            {
                _loadedModel = transform.GetChild(transform.childCount - 1).gameObject;
            }

            _lastLoadedSource = source;
            Debug.Log($"[GLBRenderer] Successfully loaded GLB from '{source}'");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GLBRenderer] Error loading GLB: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Returns true if a model is currently loaded
    /// </summary>
    public bool IsLoaded => _loadedModel != null;
    
    /// <summary>
    /// Gets the currently loaded model GameObject
    /// </summary>
    public GameObject LoadedModel => _loadedModel;
}

