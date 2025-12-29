using System;
using UnityEngine;

namespace GaussianSplatting
{
    /// <summary>
    /// GPU-based sorting for gaussian splats using compute shaders.
    /// Uses DeviceRadixSort (8-bit LSD radix sort) from b0nes164/GPUSorting.
    /// </summary>
    public sealed class GaussianSplatGPUSorter : IDisposable
    {
        private readonly ComputeShader _sortShader;
        private readonly int _calcDistancesKernel;
        private readonly int _initKernel;
        private readonly int _upsweepKernel;
        private readonly int _scanKernel;
        private readonly int _downsweepKernel;

        private GraphicsBuffer _sortDistances; // b_sort - distances to sort
        private GraphicsBuffer _sortDistancesAlt; // b_alt - alternate buffer for ping-pong
        private GraphicsBuffer _sortIndices; // b_sortPayload - indices (payload)
        private GraphicsBuffer _sortIndicesAlt; // b_altPayload - alternate indices buffer
        private GraphicsBuffer _globalHist; // b_globalHist
        private GraphicsBuffer _passHist; // b_passHist

        private readonly int _splatCount;
        private readonly int _partSize;
        private const int Radix = 256;

        private Vector3 _lastCamPos;
        private Vector3 _lastCamDir;
        private const float Epsilon = 0.001f;

        private readonly bool _isMobile;
        private readonly uint[] _identityIndices;

        public GaussianSplatGPUSorter(ComputeShader sortShader, int splatCount, bool isMobile = false)
        {
            _sortShader = sortShader;
            _splatCount = splatCount;
            _isMobile = isMobile;
            
            // Mobile version uses smaller partition size due to shared memory constraints
            _partSize = isMobile ? 1024 : 3840;

            _calcDistancesKernel = _sortShader.FindKernel("CSCalcDistances");
            _initKernel = _sortShader.FindKernel("InitDeviceRadixSort");
            _upsweepKernel = _sortShader.FindKernel("Upsweep");
            _scanKernel = _sortShader.FindKernel("Scan");
            _downsweepKernel = _sortShader.FindKernel("Downsweep");

            int threadBlocks = (splatCount + _partSize - 1) / _partSize;

            _sortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, sizeof(uint));
            _sortDistancesAlt = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, sizeof(uint));
            _sortIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, splatCount, sizeof(uint));
            _sortIndicesAlt = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, splatCount, sizeof(uint));
            _globalHist = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Radix * 4, sizeof(uint));  // 4 passes for 32-bit
            _passHist = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Radix * threadBlocks, sizeof(uint));

            _identityIndices = new uint[splatCount];
            for (int i = 0; i < splatCount; i++)
                _identityIndices[i] = (uint)i;

            _lastCamPos = new Vector3(float.NaN, 0, 0);
            _lastCamDir = new Vector3(float.NaN, 0, 0);
        }

        public static GaussianSplatGPUSorter Create(int splatCount, bool useMobile = false)
        {
            string shaderName = useMobile ? "GaussianSplatSortMobile" : "GaussianSplatSort";
            
            var sortShader = Resources.Load<ComputeShader>(shaderName);
            if (sortShader == null)
            {
                Debug.LogWarning($"[GaussianSplatGPUSorter] Could not load {shaderName} compute shader from Resources.");
                return null;
            }
            
            return new GaussianSplatGPUSorter(sortShader, splatCount, useMobile);
        }

        public void Dispose()
        {
            _sortDistances?.Dispose();
            _sortDistancesAlt?.Dispose();
            _sortIndices?.Dispose();
            _sortIndicesAlt?.Dispose();
            _globalHist?.Dispose();
            _passHist?.Dispose();
        }

        /// <summary>
        /// Sort splats back-to-front based on camera view matrix.
        /// </summary>
        /// <param name="posBuffer">Buffer containing splat positions</param>
        /// <param name="orderBuffer">Output buffer for sorted indices</param>
        /// <param name="viewMatrix">Camera view matrix</param>
        /// <param name="camPosOS">Camera position in object space (for change detection)</param>
        /// <param name="camDirOS">Camera direction in object space (for change detection)</param>
        /// <returns>True if sort was performed, false if skipped due to no camera movement</returns>
        public bool Sort(GraphicsBuffer posBuffer, GraphicsBuffer orderBuffer, Matrix4x4 viewMatrix, Vector3 camPosOS, Vector3 camDirOS)
        {
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

            int threadBlocks = (_splatCount + _partSize - 1) / _partSize;

            _sortShader.SetInt("e_numKeys", _splatCount);
            _sortShader.SetInt("e_threadBlocks", threadBlocks);
            _sortShader.SetMatrix("_MatrixMV", viewMatrix);
            _sortShader.SetInt("_SplatCount", _splatCount);

            _sortIndices.SetData(_identityIndices);

            // Step 2: Calculate distances
            _sortShader.SetBuffer(_calcDistancesKernel, "_SplatPos", posBuffer);
            _sortShader.SetBuffer(_calcDistancesKernel, "_SplatSortDistances", _sortDistances);
            _sortShader.SetBuffer(_calcDistancesKernel, "_SplatSortKeys", _sortIndices);
            int calcThreadGroups = (_splatCount + 255) / 256;
            _sortShader.Dispatch(_calcDistancesKernel, calcThreadGroups, 1, 1);

            // Step 3-6: Radix sort (4 passes for 32-bit keys)
            // Ping-pong between buffers
            var currentDistances = _sortDistances;
            var altDistances = _sortDistancesAlt;
            var currentIndices = _sortIndices;
            var altIndices = _sortIndicesAlt;

            for (int pass = 0; pass < 4; pass++)
            {
                _sortShader.SetInt("e_radixShift", pass * 8);
                // Init: Clear global histogram
                int initThreadGroups = (Radix * 4 + 255) / 256;
                _sortShader.SetBuffer(_initKernel, "b_globalHist", _globalHist);
                _sortShader.Dispatch(_initKernel, initThreadGroups, 1, 1);

                // Upsweep: Build histograms
                _sortShader.SetBuffer(_upsweepKernel, "b_sort", currentDistances);
                _sortShader.SetBuffer(_upsweepKernel, "b_globalHist", _globalHist);
                _sortShader.SetBuffer(_upsweepKernel, "b_passHist", _passHist);
                _sortShader.Dispatch(_upsweepKernel, threadBlocks, 1, 1);

                // Scan: Prefix sum over histograms
                // Mobile version needs RADIX+1 groups (extra group for global histogram scan)
                _sortShader.SetBuffer(_scanKernel, "b_passHist", _passHist);
                _sortShader.SetBuffer(_scanKernel, "b_globalHist", _globalHist);
                int scanGroups = _isMobile ? Radix + 1 : Radix;
                _sortShader.Dispatch(_scanKernel, scanGroups, 1, 1);

                // Downsweep: Scatter keys to sorted positions
                _sortShader.SetBuffer(_downsweepKernel, "b_sort", currentDistances);
                _sortShader.SetBuffer(_downsweepKernel, "b_alt", altDistances);
                _sortShader.SetBuffer(_downsweepKernel, "b_sortPayload", currentIndices);
                _sortShader.SetBuffer(_downsweepKernel, "b_altPayload", altIndices);
                _sortShader.SetBuffer(_downsweepKernel, "b_globalHist", _globalHist);
                _sortShader.SetBuffer(_downsweepKernel, "b_passHist", _passHist);
                _sortShader.Dispatch(_downsweepKernel, threadBlocks, 1, 1);

                // Swap buffers for next pass
                var tempDist = currentDistances;
                currentDistances = altDistances;
                altDistances = tempDist;

                var tempIdx = currentIndices;
                currentIndices = altIndices;
                altIndices = tempIdx;
            }

            // After 4 passes, sorted indices are in currentIndices
            // Copy to output buffer (need to reverse for back-to-front)
            // TODO: Add a reverse kernel or do it in CPU
            Graphics.CopyBuffer(currentIndices, orderBuffer);

            return true;
        }

        public void ForceUpdate()
        {
            _lastCamPos = new Vector3(float.NaN, 0, 0);
            _lastCamDir = new Vector3(float.NaN, 0, 0);
        }
    }
}
