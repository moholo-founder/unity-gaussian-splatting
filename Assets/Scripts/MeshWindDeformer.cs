using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Applies wind-like deformation to a mesh by displacing vertices based on noise.
/// Useful for testing Gaussian splat mesh deformation.
/// </summary>
[ExecuteAlways]
public class MeshWindDeformer : MonoBehaviour
{
    [Header("Wind Settings")]
    [Tooltip("Overall strength of the wind effect")]
    [Range(0f, 1f)]
    public float WindStrength = 0.1f;
    
    [Tooltip("Speed of the wind animation")]
    [Range(0.1f, 5f)]
    public float WindSpeed = 1f;
    
    [Tooltip("Scale of the noise pattern (smaller = more detailed)")]
    [Range(0.1f, 10f)]
    public float NoiseScale = 2f;
    
    [Tooltip("Direction of the wind in world space")]
    public Vector3 WindDirection = new Vector3(1f, 0f, 0.5f);
    
    [Header("Vertex Influence")]
    [Tooltip("How much height affects wind influence (higher vertices move more)")]
    [Range(0f, 2f)]
    public float HeightInfluence = 1f;
    
    [Tooltip("Minimum height (normalized 0-1) for wind to affect vertices")]
    [Range(0f, 1f)]
    public float MinHeightThreshold = 0.2f;
    
    [Header("Debug")]
    [Tooltip("Show vertex displacement gizmos")]
    public bool ShowGizmos = false;
    
    // Cached mesh data
    private class MeshData
    {
        public MeshFilter MeshFilter;
        public Mesh OriginalMesh;
        public Mesh DeformedMesh;
        public Vector3[] OriginalVertices;
        public Vector3[] DeformedVertices;
        public float MinY;
        public float MaxY;
    }
    
    private List<MeshData> _meshDataList = new List<MeshData>();
    private bool _initialized;
    private float _timeOffset;
    
    // Event for notifying listeners when mesh is deformed
    public event System.Action<MeshWindDeformer> OnMeshDeformed;
    
    private void OnEnable()
    {
        Initialize();
    }
    
    private void OnDisable()
    {
        RestoreOriginalMeshes();
    }
    
    private void OnDestroy()
    {
        RestoreOriginalMeshes();
        CleanupMeshData();
    }
    
    /// <summary>
    /// Initialize the deformer by finding all meshes in children.
    /// Call this after the GLB model is loaded.
    /// </summary>
    public void Initialize()
    {
        CleanupMeshData();
        _meshDataList.Clear();
        
        var meshFilters = GetComponentsInChildren<MeshFilter>();
        
        foreach (var mf in meshFilters)
        {
            if (mf.sharedMesh == null) continue;
            
            var data = new MeshData
            {
                MeshFilter = mf,
                OriginalMesh = mf.sharedMesh,
                DeformedMesh = Instantiate(mf.sharedMesh),
                OriginalVertices = mf.sharedMesh.vertices,
            };
            
            data.DeformedVertices = new Vector3[data.OriginalVertices.Length];
            data.OriginalVertices.CopyTo(data.DeformedVertices, 0);
            
            // Calculate bounds for height influence
            data.MinY = float.MaxValue;
            data.MaxY = float.MinValue;
            foreach (var v in data.OriginalVertices)
            {
                Vector3 worldPos = mf.transform.TransformPoint(v);
                if (worldPos.y < data.MinY) data.MinY = worldPos.y;
                if (worldPos.y > data.MaxY) data.MaxY = worldPos.y;
            }
            
            // Use deformed mesh for rendering
            mf.sharedMesh = data.DeformedMesh;
            
            _meshDataList.Add(data);
        }
        
        _initialized = _meshDataList.Count > 0;
        _timeOffset = Random.Range(0f, 100f);
        
        if (_initialized)
        {
            Debug.Log($"[MeshWindDeformer] Initialized with {_meshDataList.Count} meshes, " +
                      $"total {GetTotalVertexCount()} vertices");
        }
    }
    
    private void Update()
    {
        if (!_initialized || !Application.isPlaying) return;
        
        ApplyWindDeformation();
    }
    
    /// <summary>
    /// Apply wind deformation to all meshes.
    /// </summary>
    public void ApplyWindDeformation()
    {
        if (!_initialized) return;
        
        float time = Time.time * WindSpeed + _timeOffset;
        Vector3 windDir = WindDirection.normalized;
        
        foreach (var data in _meshDataList)
        {
            ApplyWindToMesh(data, time, windDir);
        }
        
        // Notify listeners
        OnMeshDeformed?.Invoke(this);
    }
    
    private void ApplyWindToMesh(MeshData data, float time, Vector3 windDir)
    {
        float heightRange = data.MaxY - data.MinY;
        if (heightRange < 0.001f) heightRange = 1f;
        
        for (int i = 0; i < data.OriginalVertices.Length; i++)
        {
            Vector3 localPos = data.OriginalVertices[i];
            Vector3 worldPos = data.MeshFilter.transform.TransformPoint(localPos);
            
            // Calculate height influence (0 at bottom, 1 at top)
            float normalizedHeight = Mathf.Clamp01((worldPos.y - data.MinY) / heightRange);
            
            // Skip vertices below threshold
            if (normalizedHeight < MinHeightThreshold)
            {
                data.DeformedVertices[i] = localPos;
                continue;
            }
            
            // Remap height above threshold to 0-1
            float heightFactor = (normalizedHeight - MinHeightThreshold) / (1f - MinHeightThreshold);
            heightFactor = Mathf.Pow(heightFactor, HeightInfluence);
            
            // Sample 3D Perlin noise for organic movement
            float noiseX = worldPos.x * NoiseScale + time;
            float noiseY = worldPos.y * NoiseScale + time * 0.7f;
            float noiseZ = worldPos.z * NoiseScale + time * 0.5f;
            
            // Use multiple octaves for more natural movement
            float noise1 = Mathf.PerlinNoise(noiseX, noiseZ) * 2f - 1f;
            float noise2 = Mathf.PerlinNoise(noiseY, noiseX * 0.5f) * 2f - 1f;
            float noise3 = Mathf.PerlinNoise(noiseZ * 0.7f, noiseY * 0.3f) * 2f - 1f;
            
            // Combine noises for displacement
            Vector3 displacement = new Vector3(
                noise1 * windDir.x + noise2 * 0.3f,
                noise3 * 0.2f, // Less vertical movement
                noise1 * windDir.z + noise2 * 0.3f
            );
            
            // Apply displacement scaled by wind strength and height
            displacement *= WindStrength * heightFactor;
            
            // Transform displacement back to local space
            Vector3 localDisplacement = data.MeshFilter.transform.InverseTransformVector(displacement);
            
            data.DeformedVertices[i] = localPos + localDisplacement;
        }
        
        // Update mesh
        data.DeformedMesh.vertices = data.DeformedVertices;
        data.DeformedMesh.RecalculateNormals();
        data.DeformedMesh.RecalculateBounds();
    }
    
    /// <summary>
    /// Get all current vertices from all meshes (in local space of root).
    /// </summary>
    public Vector3[] GetAllCurrentVertices()
    {
        int totalCount = GetTotalVertexCount();
        Vector3[] allVertices = new Vector3[totalCount];
        
        int offset = 0;
        foreach (var data in _meshDataList)
        {
            for (int i = 0; i < data.DeformedVertices.Length; i++)
            {
                // Transform to world, then to root local space
                Vector3 worldPos = data.MeshFilter.transform.TransformPoint(data.DeformedVertices[i]);
                allVertices[offset + i] = transform.InverseTransformPoint(worldPos);
            }
            offset += data.DeformedVertices.Length;
        }
        
        return allVertices;
    }
    
    /// <summary>
    /// Get all original vertices from all meshes (in local space of root).
    /// </summary>
    public Vector3[] GetAllOriginalVertices()
    {
        int totalCount = GetTotalVertexCount();
        Vector3[] allVertices = new Vector3[totalCount];
        
        int offset = 0;
        foreach (var data in _meshDataList)
        {
            for (int i = 0; i < data.OriginalVertices.Length; i++)
            {
                Vector3 worldPos = data.MeshFilter.transform.TransformPoint(data.OriginalVertices[i]);
                allVertices[offset + i] = transform.InverseTransformPoint(worldPos);
            }
            offset += data.OriginalVertices.Length;
        }
        
        return allVertices;
    }
    
    /// <summary>
    /// Get all triangle indices from all meshes.
    /// </summary>
    public int[] GetAllTriangles()
    {
        List<int> allTriangles = new List<int>();
        int vertexOffset = 0;
        
        foreach (var data in _meshDataList)
        {
            for (int subMesh = 0; subMesh < data.OriginalMesh.subMeshCount; subMesh++)
            {
                int[] tris = data.OriginalMesh.GetTriangles(subMesh);
                foreach (int t in tris)
                {
                    allTriangles.Add(t + vertexOffset);
                }
            }
            vertexOffset += data.OriginalVertices.Length;
        }
        
        return allTriangles.ToArray();
    }
    
    private int GetTotalVertexCount()
    {
        int count = 0;
        foreach (var data in _meshDataList)
        {
            count += data.OriginalVertices.Length;
        }
        return count;
    }
    
    private void RestoreOriginalMeshes()
    {
        foreach (var data in _meshDataList)
        {
            if (data.MeshFilter != null && data.OriginalMesh != null)
            {
                data.MeshFilter.sharedMesh = data.OriginalMesh;
            }
        }
    }
    
    private void CleanupMeshData()
    {
        foreach (var data in _meshDataList)
        {
            if (data.DeformedMesh != null)
            {
                if (Application.isPlaying)
                    Destroy(data.DeformedMesh);
                else
                    DestroyImmediate(data.DeformedMesh);
            }
        }
        _meshDataList.Clear();
        _initialized = false;
    }
    
    private void OnDrawGizmosSelected()
    {
        if (!ShowGizmos || !_initialized) return;
        
        Gizmos.color = Color.cyan;
        
        foreach (var data in _meshDataList)
        {
            for (int i = 0; i < data.DeformedVertices.Length; i += 10) // Sample every 10th vertex
            {
                Vector3 origWorld = data.MeshFilter.transform.TransformPoint(data.OriginalVertices[i]);
                Vector3 defWorld = data.MeshFilter.transform.TransformPoint(data.DeformedVertices[i]);
                
                Gizmos.DrawLine(origWorld, defWorld);
                Gizmos.DrawSphere(defWorld, 0.002f);
            }
        }
    }
    
    /// <summary>
    /// Whether the deformer is initialized and ready.
    /// </summary>
    public bool IsInitialized => _initialized;
    
    /// <summary>
    /// Get the total number of faces across all meshes.
    /// </summary>
    public int GetFaceCount()
    {
        int count = 0;
        foreach (var data in _meshDataList)
        {
            for (int subMesh = 0; subMesh < data.OriginalMesh.subMeshCount; subMesh++)
            {
                count += data.OriginalMesh.GetTriangles(subMesh).Length / 3;
            }
        }
        return count;
    }
}


