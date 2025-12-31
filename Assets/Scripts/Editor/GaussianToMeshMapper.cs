#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GaussianSplatting;
using GLTFast;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor tool that maps Gaussian splat positions to their nearest mesh faces.
/// For each Gaussian, outputs: (face_id, offset_x, offset_y, offset_z)
/// where offset = gaussian_position - triangle_center
/// </summary>
public class GaussianToMeshMapper : EditorWindow
{
    private string _plyPath = "";
    private string _glbPath = "";
    private string _outputPath = "";
    private CoordinateConversion _plyCoordConversion = CoordinateConversion.None;
    private bool _isProcessing = false;
    private string _statusMessage = "";
    private Vector2 _scrollPosition;
    
    // Results preview
    private int _gaussianCount = 0;
    private int _faceCount = 0;
    private float _avgDistance = 0f;
    private float _maxDistance = 0f;

    [MenuItem("Tools/Gaussian Splatting/Gaussian to Mesh Mapper")]
    public static void ShowWindow()
    {
        var window = GetWindow<GaussianToMeshMapper>("Gaussian to Mesh Mapper");
        window.minSize = new Vector2(450, 400);
    }

    private void OnGUI()
    {
        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
        
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Gaussian to Mesh Face Mapper", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Maps each Gaussian position to its nearest mesh face.\n" +
            "Output: For each Gaussian, stores (face_id, offset_x, offset_y, offset_z)\n" +
            "where offset = gaussian_position - triangle_center",
            MessageType.Info);
        
        EditorGUILayout.Space(10);
        
        // PLY File Selection
        EditorGUILayout.LabelField("Input Files", EditorStyles.boldLabel);
        
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.TextField("PLY File (Gaussian Splat)", _plyPath);
        if (GUILayout.Button("Browse...", GUILayout.Width(80)))
        {
            string path = EditorUtility.OpenFilePanel("Select PLY File", Application.dataPath, "ply");
            if (!string.IsNullOrEmpty(path))
            {
                _plyPath = path;
            }
        }
        EditorGUILayout.EndHorizontal();
        
        _plyCoordConversion = (CoordinateConversion)EditorGUILayout.EnumPopup("PLY Coordinate Conversion", _plyCoordConversion);
        
        EditorGUILayout.Space(5);
        
        // GLB File Selection
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.TextField("GLB File (Mesh)", _glbPath);
        if (GUILayout.Button("Browse...", GUILayout.Width(80)))
        {
            string path = EditorUtility.OpenFilePanel("Select GLB File", Application.dataPath, "glb");
            if (!string.IsNullOrEmpty(path))
            {
                _glbPath = path;
            }
        }
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.Space(10);
        
        // Output File Selection
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.TextField("Output File", _outputPath);
        if (GUILayout.Button("Browse...", GUILayout.Width(80)))
        {
            string defaultName = "";
            if (!string.IsNullOrEmpty(_plyPath))
            {
                defaultName = Path.GetFileNameWithoutExtension(_plyPath) + "_mapping";
            }
            string path = EditorUtility.SaveFilePanel("Save Mapping File", Application.dataPath, defaultName, "bin");
            if (!string.IsNullOrEmpty(path))
            {
                _outputPath = path;
            }
        }
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.HelpBox(
            "Binary format: For each Gaussian, 4 values (16 bytes):\n" +
            "  - int32: face_id (triangle index)\n" +
            "  - float32: offset_x\n" +
            "  - float32: offset_y\n" +
            "  - float32: offset_z",
            MessageType.None);
        
        EditorGUILayout.Space(10);
        
        // Process Button
        EditorGUI.BeginDisabledGroup(_isProcessing || string.IsNullOrEmpty(_plyPath) || string.IsNullOrEmpty(_glbPath) || string.IsNullOrEmpty(_outputPath));
        if (GUILayout.Button(_isProcessing ? "Processing..." : "Generate Mapping", GUILayout.Height(30)))
        {
            ProcessMappingAsync();
        }
        EditorGUI.EndDisabledGroup();
        
        // Status
        if (!string.IsNullOrEmpty(_statusMessage))
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox(_statusMessage, _statusMessage.Contains("Error") ? MessageType.Error : MessageType.Info);
        }
        
        // Results
        if (_gaussianCount > 0 && _faceCount > 0)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Results", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Gaussians: {_gaussianCount:N0}");
            EditorGUILayout.LabelField($"Mesh Faces: {_faceCount:N0}");
            EditorGUILayout.LabelField($"Average Distance to Face: {_avgDistance:F4}");
            EditorGUILayout.LabelField($"Max Distance to Face: {_maxDistance:F4}");
        }
        
        EditorGUILayout.EndScrollView();
    }

    private async void ProcessMappingAsync()
    {
        _isProcessing = true;
        _statusMessage = "Loading files...";
        Repaint();

        try
        {
            // Load PLY file
            _statusMessage = "Loading PLY file...";
            Repaint();
            
            byte[] plyBytes = File.ReadAllBytes(_plyPath);
            PlyGaussianSplat plyData = PlyGaussianSplatLoader.Load(plyBytes, _plyCoordConversion);
            Vector3[] gaussianPositions = plyData.Centers;
            _gaussianCount = gaussianPositions.Length;
            
            Debug.Log($"[GaussianToMeshMapper] Loaded {_gaussianCount} Gaussians from PLY");
            
            // Load GLB file
            _statusMessage = "Loading GLB file...";
            Repaint();
            
            var meshData = await LoadGlbMeshDataAsync(_glbPath);
            if (meshData.vertices == null || meshData.triangles == null)
            {
                _statusMessage = "Error: Failed to load mesh from GLB file.";
                _isProcessing = false;
                Repaint();
                return;
            }
            
            _faceCount = meshData.triangles.Length / 3;
            Debug.Log($"[GaussianToMeshMapper] Loaded mesh with {meshData.vertices.Length} vertices and {_faceCount} faces");
            
            // Compute triangle centers
            _statusMessage = "Computing triangle centers...";
            Repaint();
            
            Vector3[] triangleCenters = new Vector3[_faceCount];
            for (int i = 0; i < _faceCount; i++)
            {
                int idx0 = meshData.triangles[i * 3];
                int idx1 = meshData.triangles[i * 3 + 1];
                int idx2 = meshData.triangles[i * 3 + 2];
                
                Vector3 v0 = meshData.vertices[idx0];
                Vector3 v1 = meshData.vertices[idx1];
                Vector3 v2 = meshData.vertices[idx2];
                
                triangleCenters[i] = (v0 + v1 + v2) / 3f;
            }
            
            // Build spatial acceleration structure (simple grid)
            _statusMessage = "Building spatial index...";
            Repaint();
            
            var spatialIndex = BuildSpatialIndex(triangleCenters);
            
            // Map each Gaussian to nearest face
            _statusMessage = "Mapping Gaussians to faces...";
            Repaint();
            
            int[] faceIds = new int[_gaussianCount];
            Vector3[] offsets = new Vector3[_gaussianCount];
            float totalDistance = 0f;
            _maxDistance = 0f;
            
            int progressStep = Mathf.Max(1, _gaussianCount / 100);
            
            for (int i = 0; i < _gaussianCount; i++)
            {
                if (i % progressStep == 0)
                {
                    float progress = (float)i / _gaussianCount;
                    _statusMessage = $"Mapping Gaussians to faces... {progress * 100:F0}%";
                    EditorUtility.DisplayProgressBar("Gaussian to Mesh Mapper", _statusMessage, progress);
                }
                
                Vector3 gaussianPos = gaussianPositions[i];
                
                // Find nearest face
                int nearestFace = FindNearestFace(gaussianPos, triangleCenters, spatialIndex);
                
                // Compute offset (gaussian_pos - triangle_center)
                Vector3 offset = gaussianPos - triangleCenters[nearestFace];
                
                faceIds[i] = nearestFace;
                offsets[i] = offset;
                
                float dist = offset.magnitude;
                totalDistance += dist;
                if (dist > _maxDistance) _maxDistance = dist;
            }
            
            EditorUtility.ClearProgressBar();
            
            _avgDistance = totalDistance / _gaussianCount;
            
            // Write output file
            _statusMessage = "Writing output file...";
            Repaint();
            
            WriteOutputFile(_outputPath, faceIds, offsets);
            
            _statusMessage = $"Mapping complete! Saved to:\n{_outputPath}";
            Debug.Log($"[GaussianToMeshMapper] Mapping complete. File saved to: {_outputPath}");
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error: {ex.Message}";
            Debug.LogError($"[GaussianToMeshMapper] Error: {ex.Message}\n{ex.StackTrace}");
            EditorUtility.ClearProgressBar();
        }
        
        _isProcessing = false;
        Repaint();
    }

    private struct MeshData
    {
        public Vector3[] vertices;
        public int[] triangles;
    }

    private async Task<MeshData> LoadGlbMeshDataAsync(string glbPath)
    {
        var result = new MeshData();
        GameObject tempRoot = null;
        
        try
        {
            var gltfImport = new GltfImport();
            
            // Load using file:// URI for local files
            string uri = "file://" + glbPath.Replace("\\", "/");
            bool success = await gltfImport.Load(uri);
            
            if (!success)
            {
                Debug.LogError($"[GaussianToMeshMapper] Failed to load GLB: {glbPath}");
                gltfImport.Dispose();
                return result;
            }
            
            // Create a temporary GameObject to instantiate the mesh into
            tempRoot = new GameObject("_TempGLBRoot");
            tempRoot.hideFlags = HideFlags.HideAndDontSave;
            
            success = await gltfImport.InstantiateMainSceneAsync(tempRoot.transform);
            
            if (!success)
            {
                Debug.LogError($"[GaussianToMeshMapper] Failed to instantiate GLB mesh");
                gltfImport.Dispose();
                if (tempRoot != null) DestroyImmediate(tempRoot);
                return result;
            }
            
            // Collect all vertices and triangles from all MeshFilters in the hierarchy
            var allVertices = new List<Vector3>();
            var allTriangles = new List<int>();
            
            var meshFilters = tempRoot.GetComponentsInChildren<MeshFilter>();
            var skinnedMeshRenderers = tempRoot.GetComponentsInChildren<SkinnedMeshRenderer>();
            
            Debug.Log($"[GaussianToMeshMapper] Found {meshFilters.Length} MeshFilters and {skinnedMeshRenderers.Length} SkinnedMeshRenderers");
            
            // Process MeshFilters
            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh == null) continue;
                
                var mesh = mf.sharedMesh;
                var transform = mf.transform;
                
                int vertexOffset = allVertices.Count;
                
                // Add vertices (transformed to world space, then back to root local space)
                foreach (var v in mesh.vertices)
                {
                    // Transform vertex to world space, then to root's local space
                    Vector3 worldPos = transform.TransformPoint(v);
                    Vector3 localPos = tempRoot.transform.InverseTransformPoint(worldPos);
                    allVertices.Add(localPos);
                }
                
                // Add triangles (with offset)
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    int[] tris = mesh.GetTriangles(subMesh);
                    for (int t = 0; t < tris.Length; t++)
                    {
                        allTriangles.Add(tris[t] + vertexOffset);
                    }
                }
            }
            
            // Process SkinnedMeshRenderers
            foreach (var smr in skinnedMeshRenderers)
            {
                if (smr.sharedMesh == null) continue;
                
                var mesh = smr.sharedMesh;
                var transform = smr.transform;
                
                int vertexOffset = allVertices.Count;
                
                // Add vertices (transformed to world space, then back to root local space)
                foreach (var v in mesh.vertices)
                {
                    Vector3 worldPos = transform.TransformPoint(v);
                    Vector3 localPos = tempRoot.transform.InverseTransformPoint(worldPos);
                    allVertices.Add(localPos);
                }
                
                // Add triangles (with offset)
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    int[] tris = mesh.GetTriangles(subMesh);
                    for (int t = 0; t < tris.Length; t++)
                    {
                        allTriangles.Add(tris[t] + vertexOffset);
                    }
                }
            }
            
            gltfImport.Dispose();
            
            result.vertices = allVertices.ToArray();
            result.triangles = allTriangles.ToArray();
            
            Debug.Log($"[GaussianToMeshMapper] Loaded GLB with {result.vertices.Length} vertices and {result.triangles.Length / 3} triangles");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GaussianToMeshMapper] Error loading GLB: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            // Clean up temporary GameObject
            if (tempRoot != null)
            {
                DestroyImmediate(tempRoot);
            }
        }
        
        return result;
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
        
        // Find bounds
        Vector3 min = points[0];
        Vector3 max = points[0];
        
        for (int i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }
        
        // Expand bounds slightly
        Vector3 extent = max - min;
        min -= extent * 0.01f;
        max += extent * 0.01f;
        extent = max - min;
        
        // Determine cell count (aim for ~100 points per cell on average)
        int targetCellCount = Mathf.Max(1, points.Length / 100);
        float cellCountCubeRoot = Mathf.Pow(targetCellCount, 1f / 3f);
        
        var index = new SpatialIndex();
        index.min = min;
        index.max = max;
        
        // Distribute cells proportionally to extent
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
        
        // Create cells
        int totalCells = index.cellsX * index.cellsY * index.cellsZ;
        index.cells = new List<int>[totalCells];
        for (int i = 0; i < totalCells; i++)
        {
            index.cells[i] = new List<int>();
        }
        
        // Insert points
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
            // Fallback: brute force
            return FindNearestFaceBruteForce(point, triangleCenters);
        }
        
        GetCellCoords(index, point, out int cx, out int cy, out int cz);
        
        int bestFace = -1;
        float bestDistSq = float.MaxValue;
        
        // Search expanding rings of cells until we find a result
        for (int radius = 0; radius <= Mathf.Max(index.cellsX, Mathf.Max(index.cellsY, index.cellsZ)); radius++)
        {
            bool foundInRing = false;
            
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        // Only check cells on the current ring boundary
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
            
            // If we found something and the next ring would be farther than our best, we're done
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

    private void WriteOutputFile(string path, int[] faceIds, Vector3[] offsets)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            // Write header
            bw.Write((int)faceIds.Length); // Gaussian count
            
            // Write data: for each Gaussian, write (face_id, offset_x, offset_y, offset_z)
            for (int i = 0; i < faceIds.Length; i++)
            {
                bw.Write(faceIds[i]);
                bw.Write(offsets[i].x);
                bw.Write(offsets[i].y);
                bw.Write(offsets[i].z);
            }
        }
        
        // Also write a human-readable text file for inspection
        string txtPath = Path.ChangeExtension(path, ".txt");
        using (var sw = new StreamWriter(txtPath))
        {
            sw.WriteLine($"# Gaussian to Mesh Face Mapping");
            sw.WriteLine($"# Source PLY: {_plyPath}");
            sw.WriteLine($"# Source GLB: {_glbPath}");
            sw.WriteLine($"# Gaussian Count: {faceIds.Length}");
            sw.WriteLine($"# Face Count: {_faceCount}");
            sw.WriteLine($"# Format: gaussian_index, face_id, offset_x, offset_y, offset_z");
            sw.WriteLine();
            
            for (int i = 0; i < faceIds.Length; i++)
            {
                sw.WriteLine($"{i}, {faceIds[i]}, {offsets[i].x:F6}, {offsets[i].y:F6}, {offsets[i].z:F6}");
            }
        }
        
        Debug.Log($"[GaussianToMeshMapper] Also wrote text file: {txtPath}");
    }
}
#endif

