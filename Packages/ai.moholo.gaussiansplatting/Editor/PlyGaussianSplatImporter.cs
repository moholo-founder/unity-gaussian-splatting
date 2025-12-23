#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

#if UNITY_2020_2_OR_NEWER
using UnityEditor.AssetImporters;
#else
using UnityEditor.Experimental.AssetImporters;
#endif

namespace GaussianSplatting.Editor
{
    [ScriptedImporter(3, "ply")]
    public sealed class PlyGaussianSplatImporter : ScriptedImporter
    {
        [Tooltip("How to convert coordinates from the PLY file to Unity. Try None first.")]
        public CoordinateConversion coordConversion = CoordinateConversion.None;

        [Tooltip("Force refresh of the asset when settings change.")]
        public bool forceRefresh = false;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            var bytes = File.ReadAllBytes(ctx.assetPath);
            var ply = PlyGaussianSplatLoader.Load(bytes, coordConversion);

            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.name = Path.GetFileNameWithoutExtension(ctx.assetPath);

            asset.Count = ply.Count;
            asset.Centers = ply.Centers;
            asset.Rotations = ply.Rotations;
            asset.Scales = ply.Scales;
            asset.Colors = ply.Colors;

            asset.ShBands = ply.ShBands;
            asset.ShCoeffsPerSplat = ply.ShCoeffsPerSplat;
            asset.ShCoeffs = ply.ShCoeffs;

            ctx.AddObjectToAsset("GaussianSplatAsset", asset);
            ctx.SetMainObject(asset);
            
            Debug.Log($"[PlyGaussianSplatImporter] Imported {asset.Count} splats from {ctx.assetPath} (conversion={coordConversion})");
        }
    }
}
#endif


