using System.Collections;
using GaussianSplatting;
using UnityEngine;

/// <summary>
/// Test controller that connects mesh wind deformation with Gaussian splat deformation.
/// Attach this to a GameObject that has both GLBRenderer and GaussianSplatRenderer as children,
/// or reference them directly.
/// </summary>
public class GaussianMeshDeformTest : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The Gaussian splat renderer to deform")]
    public GaussianSplatRenderer GaussianRenderer;
    
    [Tooltip("The GLB renderer containing the mesh")]
    public GLBRenderer GlbRenderer;
    
    [Header("Settings")]
    [Tooltip("Auto-initialize when both components are ready")]
    public bool AutoInitialize = true;
    
    [Tooltip("Coordinate conversion for PLY loading (must match how Gaussians were loaded)")]
    public CoordinateConversion CoordConversion = CoordinateConversion.RightHandedToUnity;
    
    [Header("Status")]
    [SerializeField]
    private bool _isInitialized;
    [SerializeField]
    private string _statusMessage = "Not initialized";
    
    private MeshWindDeformer _windDeformer;
    private Vector3[] _originalVertices;
    private int[] _triangles;
    private bool _waitingForLoad;
    
    private void Start()
    {
        if (AutoInitialize)
        {
            StartCoroutine(WaitAndInitialize());
        }
    }
    
    private IEnumerator WaitAndInitialize()
    {
        _statusMessage = "Waiting for components to load...";
        _waitingForLoad = true;
        
        // Wait for GLB to load
        while (GlbRenderer == null || !GlbRenderer.IsLoaded)
        {
            yield return new WaitForSeconds(0.1f);
        }
        
        _statusMessage = "GLB loaded, waiting for Gaussians...";
        
        // Wait for Gaussians to load
        while (GaussianRenderer == null || GaussianRenderer.PositionBuffer == null)
        {
            yield return new WaitForSeconds(0.1f);
        }
        
        _statusMessage = "Both loaded, initializing...";
        yield return new WaitForSeconds(0.5f); // Extra wait for stability
        
        _waitingForLoad = false;
        Initialize();
    }
    
    /// <summary>
    /// Initialize the mesh-to-Gaussian deformation system.
    /// </summary>
    public void Initialize()
    {
        if (GaussianRenderer == null)
        {
            _statusMessage = "Error: GaussianRenderer not assigned";
            Debug.LogError("[GaussianMeshDeformTest] GaussianRenderer not assigned");
            return;
        }
        
        if (GlbRenderer == null)
        {
            _statusMessage = "Error: GlbRenderer not assigned";
            Debug.LogError("[GaussianMeshDeformTest] GlbRenderer not assigned");
            return;
        }
        
        if (!GlbRenderer.IsLoaded)
        {
            _statusMessage = "Error: GLB not loaded yet";
            Debug.LogError("[GaussianMeshDeformTest] GLB not loaded yet");
            return;
        }
        
        if (GaussianRenderer.PositionBuffer == null)
        {
            _statusMessage = "Error: Gaussians not loaded yet";
            Debug.LogError("[GaussianMeshDeformTest] Gaussians not loaded yet");
            return;
        }
        
        // Add or get MeshWindDeformer on the GLB model
        _windDeformer = GlbRenderer.LoadedModel.GetComponent<MeshWindDeformer>();
        if (_windDeformer == null)
        {
            _windDeformer = GlbRenderer.LoadedModel.AddComponent<MeshWindDeformer>();
        }
        
        // Initialize wind deformer
        _windDeformer.Initialize();
        
        if (!_windDeformer.IsInitialized)
        {
            _statusMessage = "Error: Wind deformer failed to initialize";
            Debug.LogError("[GaussianMeshDeformTest] Wind deformer failed to initialize");
            return;
        }
        
        // Get original mesh data
        _originalVertices = _windDeformer.GetAllOriginalVertices();
        _triangles = _windDeformer.GetAllTriangles();
        
        int faceCount = _triangles.Length / 3;
        Debug.Log($"[GaussianMeshDeformTest] Mesh has {_originalVertices.Length} vertices, {faceCount} faces");
        
        // Load or create mapping
        string plyPath = GetPlyPath();
        string glbPath = GetGlbPath();
        
        if (string.IsNullOrEmpty(plyPath))
        {
            _statusMessage = "Error: Could not determine PLY path";
            Debug.LogError("[GaussianMeshDeformTest] Could not determine PLY path");
            return;
        }
        
        if (string.IsNullOrEmpty(glbPath))
        {
            _statusMessage = "Error: Could not determine GLB path";
            Debug.LogError("[GaussianMeshDeformTest] Could not determine GLB path");
            return;
        }
        
        Debug.Log($"[GaussianMeshDeformTest] PLY path: {plyPath}");
        Debug.Log($"[GaussianMeshDeformTest] GLB path: {glbPath}");
        
        _statusMessage = "Loading/creating mapping...";
        
        GaussianMeshMappingService.Instance.GetOrCreateMappingAsync(plyPath, glbPath, CoordConversion, OnMappingReady);
    }
    
    private void OnMappingReady(GaussianMeshMappingService.MappingResult result)
    {
        if (!result.Success)
        {
            _statusMessage = $"Error: Mapping failed - {result.Error}";
            Debug.LogError($"[GaussianMeshDeformTest] Mapping failed: {result.Error}");
            return;
        }
        
        Debug.Log($"[GaussianMeshDeformTest] Mapping ready: {result.Mappings.Length} Gaussians mapped to {result.FaceCount} faces");
        
        // Convert from service mapping to package mapping type
        var packageMappings = new GaussianSplatting.GaussianFaceMapping[result.Mappings.Length];
        for (int i = 0; i < result.Mappings.Length; i++)
        {
            packageMappings[i] = new GaussianSplatting.GaussianFaceMapping(
                result.Mappings[i].FaceId,
                result.Mappings[i].Offset
            );
        }
        
        // Initialize Gaussian mesh deformation
        bool success = GaussianRenderer.InitializeMeshDeformation(packageMappings, _originalVertices, _triangles);
        
        if (!success)
        {
            _statusMessage = "Error: Failed to initialize Gaussian deformation";
            Debug.LogError("[GaussianMeshDeformTest] Failed to initialize Gaussian deformation");
            return;
        }
        
        // Subscribe to mesh deformation events
        _windDeformer.OnMeshDeformed += OnMeshDeformed;
        
        _isInitialized = true;
        _statusMessage = $"Initialized! {result.Mappings.Length} Gaussians tracking {result.FaceCount} faces";
        Debug.Log($"[GaussianMeshDeformTest] {_statusMessage}");
    }
    
    private void OnMeshDeformed(MeshWindDeformer deformer)
    {
        if (!_isInitialized) return;
        
        // Get current deformed vertices
        Vector3[] currentVertices = deformer.GetAllCurrentVertices();
        
        // Update Gaussian deformation
        GaussianRenderer.UpdateMeshDeformation(currentVertices, _triangles);
    }
    
    private string GetPlyPath()
    {
        // Try to get PLY path from GaussianRenderer
        if (!string.IsNullOrEmpty(GaussianRenderer.PlyUrl))
        {
            // For URLs, we need local path for mapping
            // Assume corresponding local file exists
            return System.IO.Path.Combine(
                Application.streamingAssetsPath, 
                "GaussianSplatting", 
                GaussianRenderer.PlyFileName);
        }
        
        if (!string.IsNullOrEmpty(GaussianRenderer.PlyFileName))
        {
            return System.IO.Path.Combine(
                Application.streamingAssetsPath, 
                "GaussianSplatting", 
                GaussianRenderer.PlyFileName);
        }
        
        return null;
    }
    
    private string GetGlbPath()
    {
        // GLB files are in StreamingAssets/GLB/ folder
        // Get base name from PLY filename and look for corresponding GLB
        string plyFileName = GaussianRenderer.PlyFileName;
        if (string.IsNullOrEmpty(plyFileName))
            return null;
            
        string baseName = System.IO.Path.GetFileNameWithoutExtension(plyFileName);
        return System.IO.Path.Combine(
            Application.streamingAssetsPath,
            "GLB",
            baseName + ".glb");
    }
    
    private void OnDestroy()
    {
        if (_windDeformer != null)
        {
            _windDeformer.OnMeshDeformed -= OnMeshDeformed;
        }
    }
    
    private void OnGUI()
    {
        // Show status in corner of screen
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.fontSize = 14;
        style.normal.textColor = _isInitialized ? Color.green : Color.yellow;
        
        GUI.Label(new Rect(10, 10, 500, 30), $"Gaussian Mesh Deform: {_statusMessage}", style);
        
        if (_isInitialized && _windDeformer != null)
        {
            style.normal.textColor = Color.white;
            GUI.Label(new Rect(10, 35, 500, 30), 
                $"Wind: Strength={_windDeformer.WindStrength:F2}, Speed={_windDeformer.WindSpeed:F1}", style);
        }
    }
    
    /// <summary>
    /// Whether the system is initialized and running.
    /// </summary>
    public bool IsInitialized => _isInitialized;
    
    /// <summary>
    /// Get the wind deformer for external control.
    /// </summary>
    public MeshWindDeformer WindDeformer => _windDeformer;
}

