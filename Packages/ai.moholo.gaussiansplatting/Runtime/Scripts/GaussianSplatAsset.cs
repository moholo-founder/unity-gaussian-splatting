using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// Imported gaussian splat data ready for GPU upload.
    /// Created automatically by the .ply importer.
    /// </summary>
    public sealed class GaussianSplatAsset : ScriptableObject
    {
        public int Count;

        public Vector3[] Centers;
        public Vector4[] Rotations; // (x,y,z,w)
        public Vector3[] Scales;    // exp() already applied
        public Vector4[] Colors;    // rgb in [0..1], a in [0..1]

        public int ShBands;          // 0..3
        public int ShCoeffsPerSplat; // 0, 3, 8, 15
        public Vector3[] ShCoeffs;   // length = Count * ShCoeffsPerSplat
    }
}


