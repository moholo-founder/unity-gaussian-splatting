using System;
using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// Mapping data for a single Gaussian splat to a mesh face.
    /// </summary>
    [Serializable]
    public struct GaussianFaceMapping
    {
        /// <summary>The index of the mesh face this Gaussian is mapped to.</summary>
        public int FaceId;
        /// <summary>Offset from face center to Gaussian position in rest pose.</summary>
        public Vector3 Offset;
        
        public GaussianFaceMapping(int faceId, Vector3 offset)
        {
            FaceId = faceId;
            Offset = offset;
        }
    }

    /// <summary>
    /// Handles GPU-based deformation of Gaussian splats based on mesh transformations.
    /// Each Gaussian is mapped to a mesh face. When the mesh deforms, the Gaussian moves
    /// and rotates according to how its associated face has transformed.
    /// </summary>
    public sealed class GaussianMeshDeformer : IDisposable
    {
        private readonly ComputeShader _deformShader;
        private readonly int _deformKernel;
        
        private GraphicsBuffer _mappingBuffer;        // (faceId, offset.xyz) per Gaussian
        private GraphicsBuffer _origFaceCenters;      // Original face centers
        private GraphicsBuffer _origFaceNormals;      // Original face normals
        private GraphicsBuffer _origFaceTangents;     // Original face tangents
        private GraphicsBuffer _currFaceCenters;      // Current (deformed) face centers
        private GraphicsBuffer _currFaceNormals;      // Current face normals
        private GraphicsBuffer _currFaceTangents;     // Current face tangents
        
        // Original Gaussian covariance data (stored at initialization)
        private GraphicsBuffer _origPosCovA;          // Original pos.xyz, cov.xx
        private GraphicsBuffer _origCovB;             // Original cov.xy, cov.xz, cov.yy, cov.yz
        private GraphicsBuffer _origCovCColor;        // Original cov.zz, unused, colorRG, colorBA
        
        private readonly int _gaussianCount;
        private readonly int _faceCount;
        private bool _initialized;
        
        private const int WorkgroupSize = 256;

        /// <summary>
        /// Create a new mesh deformer.
        /// </summary>
        /// <param name="deformShader">The GaussianMeshDeform compute shader</param>
        /// <param name="gaussianCount">Number of Gaussian splats</param>
        /// <param name="faceCount">Number of mesh faces</param>
        public GaussianMeshDeformer(ComputeShader deformShader, int gaussianCount, int faceCount)
        {
            _deformShader = deformShader;
            _deformKernel = deformShader.FindKernel("CSDeformGaussians");
            _gaussianCount = gaussianCount;
            _faceCount = faceCount;
            
            // Allocate buffers for face data
            _mappingBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gaussianCount, sizeof(float) * 4);
            _origFaceCenters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, faceCount, sizeof(float) * 4);
            _origFaceNormals = new GraphicsBuffer(GraphicsBuffer.Target.Structured, faceCount, sizeof(float) * 4);
            _origFaceTangents = new GraphicsBuffer(GraphicsBuffer.Target.Structured, faceCount, sizeof(float) * 4);
            _currFaceCenters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, faceCount, sizeof(float) * 4);
            _currFaceNormals = new GraphicsBuffer(GraphicsBuffer.Target.Structured, faceCount, sizeof(float) * 4);
            _currFaceTangents = new GraphicsBuffer(GraphicsBuffer.Target.Structured, faceCount, sizeof(float) * 4);
            
            // Allocate buffers for original Gaussian covariance data
            _origPosCovA = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gaussianCount, sizeof(float) * 4);
            _origCovB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gaussianCount, sizeof(float) * 4);
            _origCovCColor = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gaussianCount, sizeof(float) * 4);
            
            Debug.Log($"[GaussianMeshDeformer] Initialized for {gaussianCount} Gaussians, {faceCount} faces");
        }

        /// <summary>
        /// Initialize the deformer with mapping data and original mesh state.
        /// </summary>
        /// <param name="mappings">Array of (faceId, offset) for each Gaussian</param>
        /// <param name="vertices">Original mesh vertices</param>
        /// <param name="triangles">Mesh triangle indices (3 per face)</param>
        public void Initialize(GaussianFaceMapping[] mappings, 
                               Vector3[] vertices, int[] triangles)
        {
            if (mappings.Length != _gaussianCount)
            {
                Debug.LogError($"[GaussianMeshDeformer] Mapping count mismatch: {mappings.Length} != {_gaussianCount}");
                return;
            }
            
            int faceCount = triangles.Length / 3;
            if (faceCount != _faceCount)
            {
                Debug.LogError($"[GaussianMeshDeformer] Face count mismatch: {faceCount} != {_faceCount}");
                return;
            }
            
            // Upload mapping data
            Vector4[] mappingData = new Vector4[_gaussianCount];
            for (int i = 0; i < _gaussianCount; i++)
            {
                mappingData[i] = new Vector4(
                    mappings[i].FaceId,
                    mappings[i].Offset.x,
                    mappings[i].Offset.y,
                    mappings[i].Offset.z
                );
            }
            _mappingBuffer.SetData(mappingData);
            
            // Compute and upload original face data
            Vector4[] centers = new Vector4[_faceCount];
            Vector4[] normals = new Vector4[_faceCount];
            Vector4[] tangents = new Vector4[_faceCount];
            
            ComputeFaceData(vertices, triangles, centers, normals, tangents);
            
            _origFaceCenters.SetData(centers);
            _origFaceNormals.SetData(normals);
            _origFaceTangents.SetData(tangents);
            
            // Initialize current state to original state
            _currFaceCenters.SetData(centers);
            _currFaceNormals.SetData(normals);
            _currFaceTangents.SetData(tangents);
            
            _initialized = true;
            Debug.Log($"[GaussianMeshDeformer] Initialized with {_gaussianCount} mappings");
        }

        /// <summary>
        /// Update the current mesh state. Call this when the mesh has been deformed.
        /// </summary>
        /// <param name="vertices">Current deformed mesh vertices</param>
        /// <param name="triangles">Mesh triangle indices (unchanged from original)</param>
        public void UpdateMeshState(Vector3[] vertices, int[] triangles)
        {
            if (!_initialized)
            {
                Debug.LogWarning("[GaussianMeshDeformer] Not initialized");
                return;
            }
            
            Vector4[] centers = new Vector4[_faceCount];
            Vector4[] normals = new Vector4[_faceCount];
            Vector4[] tangents = new Vector4[_faceCount];
            
            ComputeFaceData(vertices, triangles, centers, normals, tangents);
            
            _currFaceCenters.SetData(centers);
            _currFaceNormals.SetData(normals);
            _currFaceTangents.SetData(tangents);
        }

        /// <summary>
        /// Apply the deformation to Gaussian positions and covariances.
        /// Call this before sorting, typically every frame when the mesh is animating.
        /// </summary>
        /// <param name="splatPosCovA">The Gaussian position/cov buffer (_SplatPosCovA)</param>
        /// <param name="splatCovB">The Gaussian covariance buffer (_SplatCovB)</param>
        /// <param name="splatCovCColor">The Gaussian covariance/color buffer (_SplatCovCColor)</param>
        public void ApplyDeformation(GraphicsBuffer splatPosCovA, GraphicsBuffer splatCovB, GraphicsBuffer splatCovCColor)
        {
            if (!_initialized)
            {
                Debug.LogWarning("[GaussianMeshDeformer] Not initialized, skipping deformation");
                return;
            }
            
            _deformShader.SetInt("_GaussianCount", _gaussianCount);
            _deformShader.SetInt("_FaceCount", _faceCount);
            
            // Current Gaussian buffers (read/write)
            _deformShader.SetBuffer(_deformKernel, "_SplatPosCovA", splatPosCovA);
            _deformShader.SetBuffer(_deformKernel, "_SplatCovB", splatCovB);
            _deformShader.SetBuffer(_deformKernel, "_SplatCovCColor", splatCovCColor);
            
            // Original Gaussian buffers (read only - for proper rotation from rest pose)
            _deformShader.SetBuffer(_deformKernel, "_OriginalPosCovA", _origPosCovA);
            _deformShader.SetBuffer(_deformKernel, "_OriginalCovB", _origCovB);
            _deformShader.SetBuffer(_deformKernel, "_OriginalCovCColor", _origCovCColor);
            
            // Mapping and face data
            _deformShader.SetBuffer(_deformKernel, "_GaussianFaceMapping", _mappingBuffer);
            _deformShader.SetBuffer(_deformKernel, "_OriginalFaceCenters", _origFaceCenters);
            _deformShader.SetBuffer(_deformKernel, "_OriginalFaceNormals", _origFaceNormals);
            _deformShader.SetBuffer(_deformKernel, "_OriginalFaceTangents", _origFaceTangents);
            _deformShader.SetBuffer(_deformKernel, "_CurrentFaceCenters", _currFaceCenters);
            _deformShader.SetBuffer(_deformKernel, "_CurrentFaceNormals", _currFaceNormals);
            _deformShader.SetBuffer(_deformKernel, "_CurrentFaceTangents", _currFaceTangents);
            
            int threadGroups = (_gaussianCount + WorkgroupSize - 1) / WorkgroupSize;
            _deformShader.Dispatch(_deformKernel, threadGroups, 1, 1);
        }
        
        /// <summary>
        /// Store the original Gaussian covariance data. Call this once after Gaussians are loaded.
        /// </summary>
        public void StoreOriginalCovariances(GraphicsBuffer splatPosCovA, GraphicsBuffer splatCovB, GraphicsBuffer splatCovCColor)
        {
            // Read data from source buffers to CPU, then upload to our storage buffers
            // (Graphics.CopyBuffer requires CopySource flag on source which the renderer buffers don't have)
            Vector4[] posCovAData = new Vector4[_gaussianCount];
            Vector4[] covBData = new Vector4[_gaussianCount];
            Vector4[] covCColorData = new Vector4[_gaussianCount];
            
            splatPosCovA.GetData(posCovAData);
            splatCovB.GetData(covBData);
            splatCovCColor.GetData(covCColorData);
            
            _origPosCovA.SetData(posCovAData);
            _origCovB.SetData(covBData);
            _origCovCColor.SetData(covCColorData);
            
            // Debug: print sample covariance values
            if (_gaussianCount > 0)
            {
                Debug.Log($"[GaussianMeshDeformer] Sample original covariance[0]: " +
                          $"xx={posCovAData[0].w:F4}, xy={covBData[0].x:F4}, xz={covBData[0].y:F4}, " +
                          $"yy={covBData[0].z:F4}, yz={covBData[0].w:F4}, zz={covCColorData[0].x:F4}");
            }
            
            Debug.Log($"[GaussianMeshDeformer] Stored original covariance data for {_gaussianCount} Gaussians");
        }

        /// <summary>
        /// Compute face centers, normals, and tangents from mesh data.
        /// </summary>
        private void ComputeFaceData(Vector3[] vertices, int[] triangles, 
                                     Vector4[] centers, Vector4[] normals, Vector4[] tangents)
        {
            int faceCount = triangles.Length / 3;
            
            for (int i = 0; i < faceCount; i++)
            {
                int idx0 = triangles[i * 3];
                int idx1 = triangles[i * 3 + 1];
                int idx2 = triangles[i * 3 + 2];
                
                Vector3 v0 = vertices[idx0];
                Vector3 v1 = vertices[idx1];
                Vector3 v2 = vertices[idx2];
                
                // Center is average of vertices
                Vector3 center = (v0 + v1 + v2) / 3f;
                
                // Edges
                Vector3 edge1 = v1 - v0;
                Vector3 edge2 = v2 - v0;
                
                // Normal from cross product
                Vector3 normal = Vector3.Cross(edge1, edge2).normalized;
                
                // Tangent is first edge normalized
                Vector3 tangent = edge1.normalized;
                
                // Ensure tangent is perpendicular to normal (Gram-Schmidt)
                tangent = (tangent - Vector3.Dot(tangent, normal) * normal).normalized;
                
                centers[i] = new Vector4(center.x, center.y, center.z, 0);
                normals[i] = new Vector4(normal.x, normal.y, normal.z, 0);
                tangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, 0);
            }
        }

        public void Dispose()
        {
            _mappingBuffer?.Dispose();
            _origFaceCenters?.Dispose();
            _origFaceNormals?.Dispose();
            _origFaceTangents?.Dispose();
            _currFaceCenters?.Dispose();
            _currFaceNormals?.Dispose();
            _currFaceTangents?.Dispose();
            _origPosCovA?.Dispose();
            _origCovB?.Dispose();
            _origCovCColor?.Dispose();
        }

        /// <summary>
        /// Whether the deformer has been initialized with mapping data.
        /// </summary>
        public bool IsInitialized => _initialized;
    }
}

