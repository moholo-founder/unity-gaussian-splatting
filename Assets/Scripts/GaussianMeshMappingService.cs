using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting;
using GLTFast;
using UnityEngine;

/// <summary>
/// Service that creates and loads Gaussian-to-mesh mappings.
/// Mappings are cached in StreamingAssets/MappingGLB2Gaussian/.
/// </summary>
public class GaussianMeshMappingService : MonoBehaviour
{
    private static GaussianMeshMappingService _instance;
    public static GaussianMeshMappingService Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject("_GaussianMeshMappingService");
                _instance = go.AddComponent<GaussianMeshMappingService>();
                DontDestroyOnLoad(go);
            }
            return _instance;
        }
    }

    /// <summary>
    /// Mapping data for a single Gaussian splat.
    /// </summary>
    [Serializable]
    public struct GaussianFaceMapping
    {
        public int FaceId;
        public Vector3 Offset; // gaussian_position - triangle_center
    }

    /// <summary>
    /// Complete mapping result.
    /// </summary>
    public class MappingResult
    {
        public bool Success;
        public string Error;
        public GaussianFaceMapping[] Mappings;
        public int FaceCount;
    }

    private const string MappingFolderName = "MappingGLB2Gaussian";

    /// <summary>
    /// Gets the mapping file path for a given PLY file.
    /// </summary>
    public static string GetMappingPath(string plyPath)
    {
        string baseName = Path.GetFileNameWithoutExtension(plyPath);
        string mappingFolder = Path.Combine(Application.streamingAssetsPath, MappingFolderName);
        return Path.Combine(mappingFolder, baseName + "_mapping.bin");
    }

    /// <summary>
    /// Gets the corresponding GLB path for a PLY file.
    /// </summary>
    public static string GetCorrespondingGlbPath(string plyPath)
    {
        string directory = Path.GetDirectoryName(plyPath);
        string baseName = Path.GetFileNameWithoutExtension(plyPath);
        return Path.Combine(directory, baseName + ".glb");
    }

    /// <summary>
    /// Checks if a mapping file exists for the given PLY.
    /// </summary>
    public static bool MappingExists(string plyPath)
    {
        return File.Exists(GetMappingPath(plyPath));
    }

    /// <summary>
    /// Checks if a corresponding GLB file exists for the given PLY.
    /// </summary>
    public static bool CorrespondingGlbExists(string plyPath)
    {
        return File.Exists(GetCorrespondingGlbPath(plyPath));
    }

    /// <summary>
    /// Loads an existing mapping from disk (synchronous - Editor/Desktop only).
    /// </summary>
    public static MappingResult LoadMapping(string plyPath)
    {
        string mappingPath = GetMappingPath(plyPath);
        
        if (!File.Exists(mappingPath))
        {
            return new MappingResult { Success = false, Error = "Mapping file not found" };
        }

        try
        {
            byte[] data = File.ReadAllBytes(mappingPath);
            return ParseMappingData(data, mappingPath);
        }
        catch (Exception ex)
        {
            return new MappingResult { Success = false, Error = ex.Message };
        }
    }
    
    /// <summary>
    /// Loads an existing mapping asynchronously (works on Android with StreamingAssets).
    /// </summary>
    public void LoadMappingAsync(string plyPath, Action<MappingResult> onComplete)
    {
        StartCoroutine(LoadMappingCoroutine(plyPath, onComplete));
    }
    
    private IEnumerator LoadMappingCoroutine(string plyPath, Action<MappingResult> onComplete)
    {
        string mappingPath = GetMappingPath(plyPath);
        
        // On Android, StreamingAssets requires UnityWebRequest
        if (Application.platform == RuntimePlatform.Android || mappingPath.Contains("://"))
        {
            using (var request = UnityEngine.Networking.UnityWebRequest.Get(mappingPath))
            {
                yield return request.SendWebRequest();
                
                if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    onComplete?.Invoke(new MappingResult { Success = false, Error = $"Failed to load mapping: {request.error}" });
                    yield break;
                }
                
                byte[] data = request.downloadHandler.data;
                var result = ParseMappingData(data, mappingPath);
                onComplete?.Invoke(result);
            }
        }
        else
        {
            // Desktop/Editor - use synchronous loading
            var result = LoadMapping(plyPath);
            onComplete?.Invoke(result);
        }
    }
    
    private static MappingResult ParseMappingData(byte[] data, string sourcePath)
    {
        try
        {
            using (var ms = new MemoryStream(data))
            using (var br = new BinaryReader(ms))
            {
                int gaussianCount = br.ReadInt32();
                int faceCount = br.ReadInt32();
                
                var mappings = new GaussianFaceMapping[gaussianCount];
                for (int i = 0; i < gaussianCount; i++)
                {
                    mappings[i] = new GaussianFaceMapping
                    {
                        FaceId = br.ReadInt32(),
                        Offset = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle())
                    };
                }
                
                Debug.Log($"[GaussianMeshMapping] Loaded mapping for {gaussianCount} Gaussians from {sourcePath}");
                
                return new MappingResult
                {
                    Success = true,
                    Mappings = mappings,
                    FaceCount = faceCount
                };
            }
        }
        catch (Exception ex)
        {
            return new MappingResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Gets or creates a mapping. If mapping exists, loads it. Otherwise generates it.
    /// Uses auto-detected GLB path (same name as PLY in same folder).
    /// </summary>
    public void GetOrCreateMappingAsync(string plyPath, CoordinateConversion coordConversion, Action<MappingResult> onComplete)
    {
        string glbPath = GetCorrespondingGlbPath(plyPath);
        GetOrCreateMappingAsync(plyPath, glbPath, coordConversion, onComplete);
    }

    /// <summary>
    /// Gets or creates a mapping. If mapping exists, loads it. Otherwise generates it.
    /// </summary>
    /// <param name="plyPath">Full path to PLY file</param>
    /// <param name="glbPath">Full path to GLB file</param>
    /// <param name="coordConversion">Coordinate conversion for PLY loading</param>
    /// <param name="onComplete">Callback when mapping is ready</param>
    public void GetOrCreateMappingAsync(string plyPath, string glbPath, CoordinateConversion coordConversion, Action<MappingResult> onComplete)
    {
        // On Android/mobile, always try to load pre-generated mapping first
        // (we can't generate mappings at runtime on Android due to file access limitations)
        if (Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer)
        {
            LoadMappingAsync(plyPath, (result) =>
            {
                if (result.Success)
                {
                    onComplete?.Invoke(result);
                }
                else
                {
                    Debug.LogError($"[GaussianMeshMapping] Pre-generated mapping not found for Android. " +
                                   $"Please generate the mapping in the Editor first. Error: {result.Error}");
                    onComplete?.Invoke(new MappingResult { Success = false, Error = "Mapping must be pre-generated for mobile platforms" });
                }
            });
            return;
        }
        
        // Desktop/Editor: Check if mapping already exists
        if (MappingExists(plyPath))
        {
            var result = LoadMapping(plyPath);
            onComplete?.Invoke(result);
            return;
        }

        // Check if GLB exists
        if (!File.Exists(glbPath))
        {
            Debug.Log($"[GaussianMeshMapping] GLB not found: {glbPath}");
            onComplete?.Invoke(new MappingResult { Success = false, Error = "GLB file not found" });
            return;
        }

        // Generate mapping
        Debug.Log($"[GaussianMeshMapping] Generating mapping for {Path.GetFileName(plyPath)}...");
        StartCoroutine(GenerateMappingCoroutine(plyPath, glbPath, coordConversion, onComplete));
    }

    private IEnumerator GenerateMappingCoroutine(string plyPath, string glbPath, CoordinateConversion coordConversion, Action<MappingResult> onComplete)
    {
        // Create temporary GameObject for GLB loading
        var tempGO = new GameObject("_TempGLBLoader");
        tempGO.transform.SetParent(transform);
        
        MappingResult result = null;

        try
        {
            // Load GLB
            var gltfAsset = tempGO.AddComponent<GltfAsset>();
            string uri = "file://" + glbPath.Replace("\\", "/");
            gltfAsset.Url = uri;
            
            // Wait for GLB to load
            float timeout = 60f;
            float elapsed = 0f;
            while (!gltfAsset.IsDone && elapsed < timeout)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
            
            if (!gltfAsset.IsDone)
            {
                result = new MappingResult { Success = false, Error = "GLB loading timed out" };
                yield break;
            }
            
            yield return null; // Wait one more frame
            
            // Extract mesh data
            var (vertices, triangles) = ExtractMeshData(tempGO);
            
            if (vertices.Length == 0 || triangles.Length == 0)
            {
                result = new MappingResult { Success = false, Error = "No mesh data found in GLB" };
                yield break;
            }
            
            int faceCount = triangles.Length / 3;
            Debug.Log($"[GaussianMeshMapping] GLB: {vertices.Length} vertices, {faceCount} faces");
            
            // Load PLY
            byte[] plyBytes = File.ReadAllBytes(plyPath);
            PlyGaussianSplat plyData = PlyGaussianSplatLoader.Load(plyBytes, coordConversion);
            Vector3[] gaussianPositions = plyData.Centers;
            int gaussianCount = gaussianPositions.Length;
            
            Debug.Log($"[GaussianMeshMapping] PLY: {gaussianCount} Gaussians");
            
            // Compute triangle centers
            Vector3[] triangleCenters = new Vector3[faceCount];
            for (int i = 0; i < faceCount; i++)
            {
                int idx0 = triangles[i * 3];
                int idx1 = triangles[i * 3 + 1];
                int idx2 = triangles[i * 3 + 2];
                triangleCenters[i] = (vertices[idx0] + vertices[idx1] + vertices[idx2]) / 3f;
            }
            
            // Build spatial index
            var spatialIndex = BuildSpatialIndex(triangleCenters);
            
            // Map Gaussians to faces
            var mappings = new GaussianFaceMapping[gaussianCount];
            float totalDistance = 0f;
            float maxDistance = 0f;
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int yieldInterval = Mathf.Max(1, gaussianCount / 20);
            
            for (int i = 0; i < gaussianCount; i++)
            {
                if (i % yieldInterval == 0 && i > 0)
                {
                    Debug.Log($"[GaussianMeshMapping] Progress: {i * 100 / gaussianCount}%");
                    yield return null;
                }
                
                Vector3 gaussianPos = gaussianPositions[i];
                int nearestFace = FindNearestFace(gaussianPos, triangleCenters, spatialIndex);
                Vector3 offset = gaussianPos - triangleCenters[nearestFace];
                
                mappings[i] = new GaussianFaceMapping
                {
                    FaceId = nearestFace,
                    Offset = offset
                };
                
                float dist = offset.magnitude;
                totalDistance += dist;
                if (dist > maxDistance) maxDistance = dist;
            }
            
            sw.Stop();
            float avgDistance = totalDistance / gaussianCount;
            
            Debug.Log($"[GaussianMeshMapping] Completed in {sw.ElapsedMilliseconds}ms (avg dist: {avgDistance:F4}, max: {maxDistance:F4})");
            
            // Save mapping
            SaveMapping(plyPath, mappings, faceCount);
            
            result = new MappingResult
            {
                Success = true,
                Mappings = mappings,
                FaceCount = faceCount
            };
        }
        finally
        {
            // Cleanup
            if (tempGO != null)
            {
                Destroy(tempGO);
            }
            
            onComplete?.Invoke(result ?? new MappingResult { Success = false, Error = "Unknown error" });
        }
    }

    private (Vector3[] vertices, int[] triangles) ExtractMeshData(GameObject root)
    {
        var allVertices = new List<Vector3>();
        var allTriangles = new List<int>();
        
        var meshFilters = root.GetComponentsInChildren<MeshFilter>();
        var skinnedMeshRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>();
        
        foreach (var mf in meshFilters)
        {
            if (mf.sharedMesh == null) continue;
            
            var mesh = mf.sharedMesh;
            int vertexOffset = allVertices.Count;
            
            foreach (var v in mesh.vertices)
            {
                Vector3 worldPos = mf.transform.TransformPoint(v);
                Vector3 localPos = root.transform.InverseTransformPoint(worldPos);
                allVertices.Add(localPos);
            }
            
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                int[] tris = mesh.GetTriangles(subMesh);
                foreach (int t in tris)
                {
                    allTriangles.Add(t + vertexOffset);
                }
            }
        }
        
        foreach (var smr in skinnedMeshRenderers)
        {
            if (smr.sharedMesh == null) continue;
            
            var mesh = smr.sharedMesh;
            int vertexOffset = allVertices.Count;
            
            foreach (var v in mesh.vertices)
            {
                Vector3 worldPos = smr.transform.TransformPoint(v);
                Vector3 localPos = root.transform.InverseTransformPoint(worldPos);
                allVertices.Add(localPos);
            }
            
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                int[] tris = mesh.GetTriangles(subMesh);
                foreach (int t in tris)
                {
                    allTriangles.Add(t + vertexOffset);
                }
            }
        }
        
        return (allVertices.ToArray(), allTriangles.ToArray());
    }

    private void SaveMapping(string plyPath, GaussianFaceMapping[] mappings, int faceCount)
    {
        string mappingPath = GetMappingPath(plyPath);
        string directory = Path.GetDirectoryName(mappingPath);
        
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        using (var fs = new FileStream(mappingPath, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(mappings.Length); // Gaussian count
            bw.Write(faceCount);       // Face count
            
            foreach (var m in mappings)
            {
                bw.Write(m.FaceId);
                bw.Write(m.Offset.x);
                bw.Write(m.Offset.y);
                bw.Write(m.Offset.z);
            }
        }
        
        Debug.Log($"[GaussianMeshMapping] Saved mapping to {mappingPath}");
        
        // Also write human-readable text file
        string txtPath = Path.ChangeExtension(mappingPath, ".txt");
        using (var sw = new StreamWriter(txtPath))
        {
            sw.WriteLine($"# Gaussian to Mesh Face Mapping");
            sw.WriteLine($"# Source PLY: {plyPath}");
            sw.WriteLine($"# Gaussian Count: {mappings.Length}");
            sw.WriteLine($"# Face Count: {faceCount}");
            sw.WriteLine($"# Format: gaussian_index, face_id, offset_x, offset_y, offset_z");
            sw.WriteLine();
            
            for (int i = 0; i < mappings.Length; i++)
            {
                sw.WriteLine($"{i}, {mappings[i].FaceId}, {mappings[i].Offset.x:F6}, {mappings[i].Offset.y:F6}, {mappings[i].Offset.z:F6}");
            }
        }
    }

    #region Spatial Indexing

    private class SpatialIndex
    {
        public Vector3 min;
        public Vector3 max;
        public Vector3 cellSize;
        public int cellsX, cellsY, cellsZ;
        public List<int>[] cells;
    }

    private SpatialIndex BuildSpatialIndex(Vector3[] points)
    {
        if (points.Length == 0) return null;
        
        Vector3 min = points[0];
        Vector3 max = points[0];
        
        for (int i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }
        
        Vector3 extent = max - min;
        min -= extent * 0.01f;
        max += extent * 0.01f;
        extent = max - min;
        
        int targetCellCount = Mathf.Max(1, points.Length / 100);
        float cellCountCubeRoot = Mathf.Pow(targetCellCount, 1f / 3f);
        
        var index = new SpatialIndex();
        index.min = min;
        index.max = max;
        
        float maxExtent = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));
        if (maxExtent < 0.001f) maxExtent = 1f;
        
        index.cellsX = Mathf.Max(1, Mathf.CeilToInt(cellCountCubeRoot * extent.x / maxExtent));
        index.cellsY = Mathf.Max(1, Mathf.CeilToInt(cellCountCubeRoot * extent.y / maxExtent));
        index.cellsZ = Mathf.Max(1, Mathf.CeilToInt(cellCountCubeRoot * extent.z / maxExtent));
        
        index.cellSize = new Vector3(
            extent.x / index.cellsX,
            extent.y / index.cellsY,
            extent.z / index.cellsZ
        );
        
        int totalCells = index.cellsX * index.cellsY * index.cellsZ;
        index.cells = new List<int>[totalCells];
        for (int i = 0; i < totalCells; i++)
        {
            index.cells[i] = new List<int>();
        }
        
        for (int i = 0; i < points.Length; i++)
        {
            int cellIdx = GetCellIndex(index, points[i]);
            if (cellIdx >= 0 && cellIdx < totalCells)
            {
                index.cells[cellIdx].Add(i);
            }
        }
        
        return index;
    }

    private int GetCellIndex(SpatialIndex index, Vector3 point)
    {
        int cx = Mathf.Clamp(Mathf.FloorToInt((point.x - index.min.x) / index.cellSize.x), 0, index.cellsX - 1);
        int cy = Mathf.Clamp(Mathf.FloorToInt((point.y - index.min.y) / index.cellSize.y), 0, index.cellsY - 1);
        int cz = Mathf.Clamp(Mathf.FloorToInt((point.z - index.min.z) / index.cellSize.z), 0, index.cellsZ - 1);
        
        return cx + cy * index.cellsX + cz * index.cellsX * index.cellsY;
    }

    private void GetCellCoords(SpatialIndex index, Vector3 point, out int cx, out int cy, out int cz)
    {
        cx = Mathf.Clamp(Mathf.FloorToInt((point.x - index.min.x) / index.cellSize.x), 0, index.cellsX - 1);
        cy = Mathf.Clamp(Mathf.FloorToInt((point.y - index.min.y) / index.cellSize.y), 0, index.cellsY - 1);
        cz = Mathf.Clamp(Mathf.FloorToInt((point.z - index.min.z) / index.cellSize.z), 0, index.cellsZ - 1);
    }

    private int FindNearestFace(Vector3 point, Vector3[] triangleCenters, SpatialIndex index)
    {
        if (index == null || triangleCenters.Length == 0)
        {
            return FindNearestFaceBruteForce(point, triangleCenters);
        }
        
        GetCellCoords(index, point, out int cx, out int cy, out int cz);
        
        int bestFace = -1;
        float bestDistSq = float.MaxValue;
        
        for (int radius = 0; radius <= Mathf.Max(index.cellsX, Mathf.Max(index.cellsY, index.cellsZ)); radius++)
        {
            bool foundInRing = false;
            
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (Mathf.Abs(dx) != radius && Mathf.Abs(dy) != radius && Mathf.Abs(dz) != radius)
                            continue;
                        
                        int ncx = cx + dx;
                        int ncy = cy + dy;
                        int ncz = cz + dz;
                        
                        if (ncx < 0 || ncx >= index.cellsX) continue;
                        if (ncy < 0 || ncy >= index.cellsY) continue;
                        if (ncz < 0 || ncz >= index.cellsZ) continue;
                        
                        int cellIdx = ncx + ncy * index.cellsX + ncz * index.cellsX * index.cellsY;
                        var cell = index.cells[cellIdx];
                        
                        foreach (int faceIdx in cell)
                        {
                            float distSq = (triangleCenters[faceIdx] - point).sqrMagnitude;
                            if (distSq < bestDistSq)
                            {
                                bestDistSq = distSq;
                                bestFace = faceIdx;
                                foundInRing = true;
                            }
                        }
                    }
                }
            }
            
            if (foundInRing)
            {
                float ringDistance = radius * Mathf.Min(index.cellSize.x, Mathf.Min(index.cellSize.y, index.cellSize.z));
                if (ringDistance * ringDistance > bestDistSq)
                {
                    break;
                }
            }
        }
        
        return bestFace >= 0 ? bestFace : FindNearestFaceBruteForce(point, triangleCenters);
    }

    private int FindNearestFaceBruteForce(Vector3 point, Vector3[] triangleCenters)
    {
        int bestFace = 0;
        float bestDistSq = float.MaxValue;
        
        for (int i = 0; i < triangleCenters.Length; i++)
        {
            float distSq = (triangleCenters[i] - point).sqrMagnitude;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestFace = i;
            }
        }
        
        return bestFace;
    }

    #endregion
}

