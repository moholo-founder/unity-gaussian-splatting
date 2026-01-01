#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public class GaussianMappingEditorUtils
{
    [MenuItem("Tools/Gaussian Splatting/Open Mapping Folder")]
    private static void OpenMappingFolder()
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, "MappingGLB2Gaussian");
        if (!System.IO.Directory.Exists(path))
        {
            System.IO.Directory.CreateDirectory(path);
        }
        EditorUtility.RevealInFinder(path);
    }
    
    [MenuItem("Tools/Gaussian Splatting/Clear All Mappings")]
    private static void ClearAllMappings()
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, "MappingGLB2Gaussian");
        if (System.IO.Directory.Exists(path))
        {
            if (EditorUtility.DisplayDialog("Clear Mappings", 
                "Are you sure you want to delete all cached Gaussian-to-mesh mappings?", 
                "Yes", "Cancel"))
            {
                System.IO.Directory.Delete(path, true);
                System.IO.Directory.CreateDirectory(path);
                AssetDatabase.Refresh();
                Debug.Log("[GaussianMappingEditorUtils] All mappings cleared.");
            }
        }
        else
        {
            Debug.Log("[GaussianMappingEditorUtils] No mapping folder exists yet.");
        }
    }
}
#endif


