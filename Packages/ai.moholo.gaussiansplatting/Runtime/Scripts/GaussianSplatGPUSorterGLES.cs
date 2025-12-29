using System;
using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// GPU-based sorting for gaussian splats using compute shaders.
    /// OpenGL ES compatible implementation - does NOT use wave intrinsics.
    /// Uses a simple radix sort based on VkRadixSort.
    /// </summary>
    public sealed class GaussianSplatGPUSorterGLES : IDisposable
    {
        private readonly ComputeShader _sortShader;
        private readonly int _calcDistancesKernel;
        private readonly int _histogramKernel;
        private readonly int _globalOffsetsKernel;
        private readonly int _scatterKernel;
        private readonly int _initIdentityKernel;
        private readonly int _resetVisibleCountKernel;

        private GraphicsBuffer _sortKeys;           // Keys to sort (distances)
        private GraphicsBuffer _sortKeysAlt;        // Alternate buffer for ping-pong
        private GraphicsBuffer _sortPayload;        // Payload (indices)
        private GraphicsBuffer _sortPayloadAlt;     // Alternate payload buffer
        private GraphicsBuffer _histograms;         // Per-workgroup histograms
        private GraphicsBuffer _globalOffsets;      // Global offsets for scattering
        private GraphicsBuffer _visibleCount;       // Atomic counter for visible splats after frustum culling
        private uint[] _visibleCountReadback = new uint[1];  // CPU readback buffer
        private int _lastVisibleCount;  // Cached visible count from previous frame (avoids GPU sync)
        private bool _pendingReadback = false;  // Track if we have a pending async readback

        private readonly int _splatCount;
        private const int WorkgroupSize = 256;
        private const int RadixSortBins = 256;
        private const int BitsPerPass = 8;
        private const int NumPasses = 4; // 32-bit keys / 8 bits per pass

        private Vector3 _lastCamPos;
        private Vector3 _lastCamDir;
        private const float Epsilon = 0.001f;

        public GaussianSplatGPUSorterGLES(ComputeShader sortShader, int splatCount)
        {
            _sortShader = sortShader;
            _splatCount = splatCount;

            // Find kernels
            _calcDistancesKernel = _sortShader.FindKernel("CSCalcDistances");
            _histogramKernel = _sortShader.FindKernel("CSHistogram");
            _globalOffsetsKernel = _sortShader.FindKernel("CSGlobalOffsets");
            _scatterKernel = _sortShader.FindKernel("CSScatter");
            _initIdentityKernel = _sortShader.FindKernel("CSInitIdentity");
            _resetVisibleCountKernel = _sortShader.FindKernel("CSResetVisibleCount");

            // Calculate number of workgroups needed
            int numWorkgroups = (splatCount + WorkgroupSize - 1) / WorkgroupSize;
            // Ensure at least 1 workgroup
            numWorkgroups = Mathf.Max(1, numWorkgroups);

            // Allocate buffers
            _sortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, sizeof(uint));
            _sortKeysAlt = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, sizeof(uint));
            _sortPayload = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, splatCount, sizeof(uint));
            _sortPayloadAlt = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, splatCount, sizeof(uint));
            _histograms = new GraphicsBuffer(GraphicsBuffer.Target.Structured, RadixSortBins * numWorkgroups, sizeof(uint));
            _globalOffsets = new GraphicsBuffer(GraphicsBuffer.Target.Structured, RadixSortBins * numWorkgroups, sizeof(uint));
            _visibleCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));

            _lastCamPos = new Vector3(float.NaN, 0, 0);
            _lastCamDir = new Vector3(float.NaN, 0, 0);
            _lastVisibleCount = splatCount;  // Start with all visible

            Debug.Log($"[GaussianSplatGPUSorterGLES] Initialized for {splatCount} splats, {numWorkgroups} workgroups. Frustum culling with async readback enabled.");
        }

        public void Dispose()
        {
            _sortKeys?.Dispose();
            _sortKeysAlt?.Dispose();
            _sortPayload?.Dispose();
            _sortPayloadAlt?.Dispose();
            _histograms?.Dispose();
            _globalOffsets?.Dispose();
            _visibleCount?.Dispose();
        }

        /// <summary>
        /// Sort splats back-to-front based on camera view matrix with frustum culling.
        /// Returns the number of visible splats after culling via out parameter.
        /// </summary>
        public bool Sort(GraphicsBuffer posBuffer, GraphicsBuffer orderBuffer, Matrix4x4 viewMatrix, Matrix4x4 viewProjMatrix, Vector3 camPosOS, Vector3 camDirOS, float frustumCullMargin, out int visibleCount)
        {
            visibleCount = _splatCount;  // Default to all visible
            
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

            int numWorkgroups = (_splatCount + WorkgroupSize - 1) / WorkgroupSize;
            numWorkgroups = Mathf.Max(1, numWorkgroups);
            
            // Calculate blocks per workgroup (each thread processes multiple elements)
            int numBlocksPerWorkgroup = (_splatCount + (numWorkgroups * WorkgroupSize) - 1) / (numWorkgroups * WorkgroupSize);
            numBlocksPerWorkgroup = Mathf.Max(1, numBlocksPerWorkgroup);

            // Step 1: Initialize payload with identity indices on GPU (0, 1, 2, ...)
            // This avoids allocating a managed array every frame and CPU->GPU transfer
            _sortShader.SetInt("_NumElements", _splatCount);
            _sortShader.SetBuffer(_initIdentityKernel, "_SortPayload", _sortPayload);
            int initThreadGroups = (_splatCount + WorkgroupSize - 1) / WorkgroupSize;
            _sortShader.Dispatch(_initIdentityKernel, initThreadGroups, 1, 1);

            // Step 1.5: Reset visible count for frustum culling
            _sortShader.SetBuffer(_resetVisibleCountKernel, "_VisibleCount", _visibleCount);
            _sortShader.Dispatch(_resetVisibleCountKernel, 1, 1, 1);

            // Step 2: Calculate distances (keys) with frustum culling
            _sortShader.SetInt("_SplatCount", _splatCount);
            _sortShader.SetMatrix("_MatrixMV", viewMatrix);
            _sortShader.SetMatrix("_MatrixVP", viewProjMatrix);
            _sortShader.SetFloat("_FrustumCullMargin", frustumCullMargin);
            _sortShader.SetBuffer(_calcDistancesKernel, "_SplatPosCovA", posBuffer);
            _sortShader.SetBuffer(_calcDistancesKernel, "_SortKeys", _sortKeys);
            _sortShader.SetBuffer(_calcDistancesKernel, "_SortPayload", _sortPayload);
            _sortShader.SetBuffer(_calcDistancesKernel, "_VisibleCount", _visibleCount);
            
            int calcThreadGroups = (_splatCount + WorkgroupSize - 1) / WorkgroupSize;
            _sortShader.Dispatch(_calcDistancesKernel, calcThreadGroups, 1, 1);

            // Step 3: Radix sort (4 passes for 32-bit keys)
            var currentKeys = _sortKeys;
            var altKeys = _sortKeysAlt;
            var currentPayload = _sortPayload;
            var altPayload = _sortPayloadAlt;

            for (int pass = 0; pass < NumPasses; pass++)
            {
                int radixShift = pass * BitsPerPass;

                // Set common parameters
                _sortShader.SetInt("_NumElements", _splatCount);
                _sortShader.SetInt("_RadixShift", radixShift);
                _sortShader.SetInt("_NumWorkgroups", numWorkgroups);
                _sortShader.SetInt("_NumBlocksPerWorkgroup", numBlocksPerWorkgroup);

                // Histogram kernel
                _sortShader.SetBuffer(_histogramKernel, "_SortKeys", currentKeys);
                _sortShader.SetBuffer(_histogramKernel, "_Histograms", _histograms);
                _sortShader.Dispatch(_histogramKernel, numWorkgroups, 1, 1);

                // Global offsets kernel (compute prefix sums)
                _sortShader.SetBuffer(_globalOffsetsKernel, "_Histograms", _histograms);
                _sortShader.SetBuffer(_globalOffsetsKernel, "_GlobalOffsets", _globalOffsets);
                _sortShader.Dispatch(_globalOffsetsKernel, numWorkgroups, 1, 1);

                // Scatter kernel
                _sortShader.SetBuffer(_scatterKernel, "_SortKeys", currentKeys);
                _sortShader.SetBuffer(_scatterKernel, "_SortKeysAlt", altKeys);
                _sortShader.SetBuffer(_scatterKernel, "_SortPayload", currentPayload);
                _sortShader.SetBuffer(_scatterKernel, "_SortPayloadAlt", altPayload);
                _sortShader.SetBuffer(_scatterKernel, "_GlobalOffsets", _globalOffsets);
                _sortShader.Dispatch(_scatterKernel, numWorkgroups, 1, 1);

                // Swap buffers for next pass
                var tempKeys = currentKeys;
                currentKeys = altKeys;
                altKeys = tempKeys;

                var tempPayload = currentPayload;
                currentPayload = altPayload;
                altPayload = tempPayload;
            }

            // After 4 passes, sorted indices are in currentPayload
            Graphics.CopyBuffer(currentPayload, orderBuffer);

            // Use async readback to avoid GPU sync stall
            // Return the PREVIOUS frame's visible count (1 frame latency is acceptable for culling)
            // This prevents the massive performance hit of synchronous GPU readback
            if (!_pendingReadback)
            {
                _pendingReadback = true;
                UnityEngine.Rendering.AsyncGPUReadback.Request(_visibleCount, (request) =>
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
        /// Sort splats back-to-front (legacy overload without frustum culling).
        /// </summary>
        public bool Sort(GraphicsBuffer posBuffer, GraphicsBuffer orderBuffer, Matrix4x4 viewMatrix, Vector3 camPosOS, Vector3 camDirOS)
        {
            // Call full version with default frustum margin and discard visible count
            return Sort(posBuffer, orderBuffer, viewMatrix, viewMatrix, camPosOS, camDirOS, 0.5f, out _);
        }

        public void ForceUpdate()
        {
            _lastCamPos = new Vector3(float.NaN, 0, 0);
            _lastCamDir = new Vector3(float.NaN, 0, 0);
        }
    }
}

