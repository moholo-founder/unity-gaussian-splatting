using UnityEngine;

/// <summary>
/// ScriptableObject that holds references to glTFast materials.
/// This ensures the shaders are included in the build for runtime GLB loading.
/// Place this asset in a Resources folder.
/// </summary>
[CreateAssetMenu(fileName = "GLTFastShaderIncludes", menuName = "glTFast/Shader Includes")]
public class GLTFastShaderIncludes : ScriptableObject
{
    [Tooltip("Reference materials using glTFast shaders. These ensure the shaders are included in builds.")]
    public Material[] materials;
}


