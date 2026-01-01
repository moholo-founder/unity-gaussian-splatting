using System.Collections;
using System.IO;
using GaussianSplatting;
using UnityEngine;

/// <summary>
/// Add this component to automatically generate Gaussian-to-mesh mappings at runtime.
/// Place PLY files in StreamingAssets/GaussianSplatting/ and GLB files with the same name.
/// Mappings will be cached in StreamingAssets/MappingGLB2Gaussian/.
/// </summary>
public class GaussianMeshMappingTrigger : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Folder containing PLY files (relative to StreamingAssets)")]
    public string plyFolder = "GaussianSplatting";
    
    [Tooltip("Folder containing GLB files (relative to StreamingAssets)")]
    public string glbFolder = "GLB";
    
    [Tooltip("Coordinate conversion for PLY files")]
    public CoordinateConversion coordConversion = CoordinateConversion.None;
    
    [Tooltip("Auto-generate mappings on Start")]
    public bool generateOnStart = true;

    private void Start()
    {
        if (generateOnStart)
        {
            GenerateAllMappings();
        }
    }

    [ContextMenu("Generate All Mappings")]
    public void GenerateAllMappings()
    {
        StartCoroutine(GenerateAllMappingsCoroutine());
    }

    private IEnumerator GenerateAllMappingsCoroutine()
    {
        string plyFolderPath = Path.Combine(Application.streamingAssetsPath, plyFolder);
        string glbFolderPath = Path.Combine(Application.streamingAssetsPath, glbFolder);
        
        if (!Directory.Exists(plyFolderPath))
        {
            Debug.LogWarning($"[GaussianMeshMappingTrigger] PLY folder not found: {plyFolderPath}");
            yield break;
        }
        
        string[] plyFiles = Directory.GetFiles(plyFolderPath, "*.ply");
        Debug.Log($"[GaussianMeshMappingTrigger] Found {plyFiles.Length} PLY files");
        
        foreach (string plyPath in plyFiles)
        {
            string baseName = Path.GetFileNameWithoutExtension(plyPath);
            string glbPath = Path.Combine(glbFolderPath, baseName + ".glb");
            
            // Check if GLB exists
            if (!File.Exists(glbPath))
            {
                Debug.Log($"[GaussianMeshMappingTrigger] No GLB for {baseName}, skipping");
                continue;
            }
            
            // Check if mapping already exists
            if (GaussianMeshMappingService.MappingExists(plyPath))
            {
                Debug.Log($"[GaussianMeshMappingTrigger] Mapping already exists for {baseName}");
                continue;
            }
            
            // Generate mapping
            Debug.Log($"[GaussianMeshMappingTrigger] Generating mapping for {baseName}...");
            
            bool done = false;
            GaussianMeshMappingService.Instance.GetOrCreateMappingAsync(plyPath, glbPath, coordConversion, result =>
            {
                if (result.Success)
                {
                    Debug.Log($"[GaussianMeshMappingTrigger] Successfully created mapping for {baseName}");
                }
                else
                {
                    Debug.LogError($"[GaussianMeshMappingTrigger] Failed to create mapping for {baseName}: {result.Error}");
                }
                done = true;
            });
            
            // Wait for completion
            while (!done)
            {
                yield return null;
            }
        }
        
        Debug.Log("[GaussianMeshMappingTrigger] All mappings processed");
    }
}


