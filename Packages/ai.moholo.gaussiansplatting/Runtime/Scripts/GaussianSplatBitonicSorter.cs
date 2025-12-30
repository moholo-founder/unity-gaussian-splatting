using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    /// <summary>
    /// GPU-based local sort for gaussian splats.
    /// OpenGL ES 3.1 compatible - uses only local sorting within workgroups.
    /// Avoids cross-dispatch synchronization issues on mobile GPUs.
    /// Provides approximate sorting which is visually acceptable for gaussian splatting.
    /// </summary>
    public sealed class GaussianSplatBitonicSorter : IDisposable
    {
        private readonly ComputeShader _sortShader;
        private readonly int _resetKernel;
        private readonly int _calcAndLocalSortKernel;
        private readonly int _globalPassKernel;
        private readonly int _copyResultsKernel;

        private GraphicsBuffer _sortKeys;
        private GraphicsBuffer _sortIndices;
        private GraphicsBuffer _visibleCount;
        
        private readonly int _splatCount;
        private readonly int _paddedCount;
        private const int WorkgroupSize = 128;
        private const int ElementsPerGroup = WorkgroupSize * 2; // 256 elements per group

        private Vector3 _lastCamPos;
        private Vector3 _lastCamDir;
        private const float Epsilon = 0.001f;
        
        private int _lastVisibleCount;
        private bool _pendingReadback = false;

        public GaussianSplatBitonicSorter(ComputeShader sortShader, int splatCount)
        {
            _sortShader = sortShader;
            _splatCount = splatCount;
            
            // Bitonic sort works best on power-of-two sizes.
            // Pad to the next power of two that is at least splatCount and a multiple of ElementsPerGroup.
            int minPadded = ((splatCount + ElementsPerGroup - 1) / ElementsPerGroup) * ElementsPerGroup;
            _paddedCount = Mathf.NextPowerOfTwo(minPadded);

            // Find kernels
            _resetKernel = _sortShader.FindKernel("CSResetVisibleCount");
            _calcAndLocalSortKernel = _sortShader.FindKernel("CSCalcDistancesAndLocalSort");
            _globalPassKernel = _sortShader.FindKernel("CSGlobalBitonicPass");
            _copyResultsKernel = _sortShader.FindKernel("CSCopySortedIndices");

            // Allocate buffers
            _sortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _paddedCount, sizeof(uint));
            _sortIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _paddedCount, sizeof(uint));
            _visibleCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

            _lastCamPos = new Vector3(float.NaN, 0, 0);
            _lastCamDir = new Vector3(float.NaN, 0, 0);
            _lastVisibleCount = splatCount;

            Debug.Log($"[GaussianSplatBitonicSorter] Initialized for {splatCount} splats (padded to {_paddedCount}). " +
                      $"Workgroup size: {WorkgroupSize}, Elements per group: {ElementsPerGroup}. " +
                      $"Performing CORRECT GLOBAL BITONIC SORT on GPU.");
        }

        public void Dispose()
        {
            _sortKeys?.Dispose();
            _sortIndices?.Dispose();
            _visibleCount?.Dispose();
        }

        /// <summary>
        /// Sort splats back-to-front using local bitonic sort with frustum culling.
        /// Uses only local sorting within workgroups - no global merge passes.
        /// This provides approximate sorting which is visually acceptable.
        /// </summary>
        public bool Sort(GraphicsBuffer posBuffer, GraphicsBuffer orderBuffer, Matrix4x4 viewMatrix, 
                        Matrix4x4 viewProjMatrix, Vector3 camPosOS, Vector3 camDirOS, 
                        float frustumCullMargin, out int visibleCount)
        {
            visibleCount = _lastVisibleCount;
            
            // Skip if camera hasn't moved
            if (Mathf.Abs(camPosOS.x - _lastCamPos.x) < Epsilon &&
                Mathf.Abs(camPosOS.y - _lastCamPos.y) < Epsilon &&
                Mathf.Abs(camPosOS.z - _lastCamPos.z) < Epsilon &&
                Mathf.Abs(camDirOS.x - _lastCamDir.x) < Epsilon &&
                Mathf.Abs(camDirOS.y - _lastCamDir.y) < Epsilon &&
                Mathf.Abs(camDirOS.z - _lastCamDir.z) < Epsilon)
            {
                return false;
            }

            _lastCamPos = camPosOS;
            _lastCamDir = camDirOS;

            // Reset visible count via kernel
            _sortShader.SetBuffer(_resetKernel, "_VisibleCount", _visibleCount);
            _sortShader.Dispatch(_resetKernel, 1, 1, 1);

            // Step 1: Calculate distances AND sort locally within groups of 256
            _sortShader.SetInt("_SplatCount", _splatCount);
            _sortShader.SetInt("_PaddedCount", _paddedCount);
            _sortShader.SetVector("_CamPos", camPosOS);
            _sortShader.SetVector("_CamDir", camDirOS);
            _sortShader.SetMatrix("_MatrixMV", viewMatrix);
            _sortShader.SetMatrix("_MatrixVP", viewProjMatrix);
            _sortShader.SetFloat("_FrustumCullMargin", frustumCullMargin);
            _sortShader.SetBuffer(_calcAndLocalSortKernel, "_SplatPosCovA", posBuffer);
            _sortShader.SetBuffer(_calcAndLocalSortKernel, "_VisibleCount", _visibleCount);
            _sortShader.SetBuffer(_calcAndLocalSortKernel, "_SortKeys", _sortKeys);
            _sortShader.SetBuffer(_calcAndLocalSortKernel, "_SortIndices", _sortIndices);
            
            int numGroups = _paddedCount / ElementsPerGroup;
            _sortShader.Dispatch(_calcAndLocalSortKernel, numGroups, 1, 1);

            // Step 2: Global bitonic sort passes
            // We already did k up to ElementsPerGroup in the first kernel.
            // Now do k = ElementsPerGroup*2 up to _paddedCount.
            for (uint k = (uint)ElementsPerGroup * 2; k <= (uint)_paddedCount; k <<= 1)
            {
                for (uint j = k >> 1; j > 0; j >>= 1)
                {
                    // If j is small enough, we COULD do it in a local kernel, 
                    // but for simplicity and correctness on mobile (avoiding complex multi-step kernels),
                    // we do one dispatch per j for k > ElementsPerGroup.
                    _sortShader.SetInt("_K", (int)k);
                    _sortShader.SetInt("_J", (int)j);
                    _sortShader.SetBuffer(_globalPassKernel, "_SortKeys", _sortKeys);
                    _sortShader.SetBuffer(_globalPassKernel, "_SortIndices", _sortIndices);
                    
                    // Each thread handles 1 pair, so we need _paddedCount / 2 threads
                    int globalThreads = _paddedCount / 2;
                    int globalGroups = (globalThreads + WorkgroupSize - 1) / WorkgroupSize;
                    _sortShader.Dispatch(_globalPassKernel, globalGroups, 1, 1);
                }
            }

            // Step 3: Copy results back to the output order buffer
            _sortShader.SetInt("_SplatCount", _splatCount);
            _sortShader.SetBuffer(_copyResultsKernel, "_SortIndices", _sortIndices);
            _sortShader.SetBuffer(_copyResultsKernel, "_OutputOrder", orderBuffer);
            int copyGroups = (_splatCount + WorkgroupSize - 1) / WorkgroupSize;
            _sortShader.Dispatch(_copyResultsKernel, copyGroups, 1, 1);

            // Async readback for visible count
            if (!_pendingReadback)
            {
                _pendingReadback = true;
                AsyncGPUReadback.Request(_visibleCount, (request) =>
                {
                    if (!request.hasError && request.done)
                    {
                        var data = request.GetData<uint>();
                        if (data.Length > 0)
                        {
                            _lastVisibleCount = (int)data[0];
                        }
                    }
                    _pendingReadback = false;
                });
            }
            
            visibleCount = _lastVisibleCount;
            return true;
        }

        /// <summary>
        /// Sort without frustum culling (legacy overload).
        /// </summary>
        public bool Sort(GraphicsBuffer posBuffer, GraphicsBuffer orderBuffer, Matrix4x4 viewMatrix, 
                        Vector3 camPosOS, Vector3 camDirOS)
        {
            return Sort(posBuffer, orderBuffer, viewMatrix, viewMatrix, camPosOS, camDirOS, 0.5f, out _);
        }

        public void ForceUpdate()
        {
            _lastCamPos = new Vector3(float.NaN, 0, 0);
            _lastCamDir = new Vector3(float.NaN, 0, 0);
        }
    }
}
