using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    /// <summary>
    /// Sort algorithm options for GPU sorting.
    /// </summary>
    public enum SortAlgorithm
    {
        /// <summary>Radix sort - 4 passes, good for medium splat counts (&lt;500K)</summary>
        Radix,
        /// <summary>Bitonic sort - fewer dispatches, better for large splat counts (&gt;500K)</summary>
        Bitonic,
        /// <summary>No sorting - fastest but may have visual artifacts. Use with SortEveryNFrames for periodic sorting.</summary>
        None
    }

    [ExecuteAlways]
    public sealed class GaussianSplatRenderer : MonoBehaviour, ISerializationCallbackReceiver
    {
        // Helper to pack two floats into a single uint using half-precision (16-bit float)
        private static uint PackHalf2x16(float x, float y)
        {
            ushort hx = FloatToHalf(x);
            ushort hy = FloatToHalf(y);
            return (uint)hx | ((uint)hy << 16);
        }
        
        // Convert float to half-precision (IEEE 754 binary16)
        private static ushort FloatToHalf(float value)
        {
            int i = BitConverter.SingleToInt32Bits(value);
            int s = (i >> 16) & 0x8000;
            int e = ((i >> 23) & 0xff) - 127 + 15;
            int m = i & 0x7fffff;
            
            if (e <= 0)
            {
                if (e < -10) return (ushort)s;
                m = (m | 0x800000) >> (1 - e);
                return (ushort)(s | (m >> 13));
            }
            if (e == 0xff - 127 + 15)
            {
                if (m == 0) return (ushort)(s | 0x7c00);
                return (ushort)(s | 0x7c00 | (m >> 13));
            }
            if (e > 30)
            {
                return (ushort)(s | 0x7c00);
            }
            return (ushort)(s | (e << 10) | (m >> 13));
        }

        /// <summary>
        /// Precompute 3D covariance matrix from quaternion and scale.
        /// Returns 6 unique values of the symmetric 3x3 covariance matrix.
        /// covA = (xx, xy, xz), covB = (yy, yz, zz)
        /// </summary>
        private static void ComputeCovariance3D(Vector4 rotation, Vector3 scale, out Vector3 covA, out Vector3 covB)
        {
            // Quaternion to rotation matrix (q = w,x,y,z stored as x,y,z,w in Unity)
            float qx = rotation.x, qy = rotation.y, qz = rotation.z, qw = rotation.w;
            float len = Mathf.Sqrt(qx*qx + qy*qy + qz*qz + qw*qw);
            if (len > 0.0001f) { qx /= len; qy /= len; qz /= len; qw /= len; }
            
            // Rotation matrix R from quaternion (column-major for math, row-major in code)
            // Using standard quaternion-to-matrix formula with q = (w, x, y, z)
            float r00 = 1f - 2f * (qy*qy + qz*qz);
            float r01 = 2f * (qx*qy - qw*qz);
            float r02 = 2f * (qx*qz + qw*qy);
            float r10 = 2f * (qx*qy + qw*qz);
            float r11 = 1f - 2f * (qx*qx + qz*qz);
            float r12 = 2f * (qy*qz - qw*qx);
            float r20 = 2f * (qx*qz - qw*qy);
            float r21 = 2f * (qy*qz + qw*qx);
            float r22 = 1f - 2f * (qx*qx + qy*qy);
            
            // M = R * S where S is diagonal scale matrix
            float m00 = r00 * scale.x, m01 = r01 * scale.y, m02 = r02 * scale.z;
            float m10 = r10 * scale.x, m11 = r11 * scale.y, m12 = r12 * scale.z;
            float m20 = r20 * scale.x, m21 = r21 * scale.y, m22 = r22 * scale.z;
            
            // Covariance V = M * M^T (symmetric 3x3 matrix)
            // Only compute 6 unique values
            float v00 = m00*m00 + m01*m01 + m02*m02;
            float v01 = m00*m10 + m01*m11 + m02*m12;
            float v02 = m00*m20 + m01*m21 + m02*m22;
            float v11 = m10*m10 + m11*m11 + m12*m12;
            float v12 = m10*m20 + m11*m21 + m12*m22;
            float v22 = m20*m20 + m21*m21 + m22*m22;
            
            covA = new Vector3(v00, v01, v02);
            covB = new Vector3(v11, v12, v22);
        }

        [Header("Input - StreamingAssets or URL")]
        [Tooltip("Full URL to load PLY from (prioritized over PlyFileName). Example: https://example.com/splat.ply")]
        public string PlyUrl = "";
        
        [Tooltip("PLY filename in StreamingAssets/GaussianSplatting/ folder (e.g., 'testsplat.ply'). Used if PlyUrl is empty.")]
        public string PlyFileName = "testsplat.ply";
        
        [System.NonSerialized]
        [HideInInspector]
        public GaussianSplatAsset PlyAsset;
        
        [System.NonSerialized]
        private string _lastLoadedSource = ""; // Track last loaded file/URL

        [Header("Rendering")]
        [Tooltip("Material using the unified GaussianSplatting/Gaussian Splat shader. The shader automatically selects the correct SubShader for Vulkan/Metal/D3D vs GLES.")]
        public Material Material;
        public Camera TargetCamera;
        
        [Tooltip("How often to sort splats (in frames). 1 = every frame, 2 = every other frame, etc.")]
        [Range(1, 90)]
        public int SortEveryNFrames = 1;
        
        [Tooltip("Use URP Render Feature for proper matrix setup. Add GaussianSplatRenderFeature to your URP Renderer.")]
        public bool UseRenderFeature = false;

        [Header("Splat Properties")]
        [Tooltip("Scale multiplier for all splats.")]
        [Range(0.1f, 5.0f)]
        public float ScaleMultiplier = 1.0f;
        [Tooltip("Limit number of splats to LOAD (0 = load all). Reduces sorting overhead for debugging.")]
        [Min(0)]
        public int MaxSplatsToLoad = 0;
        
        [Header("Performance")]
        [Tooltip("Enable GPU frustum culling to skip off-screen splats. Improves performance when only part of the scene is visible.")]
        public bool EnableFrustumCulling = true;
        [Tooltip("Extra margin for frustum culling in NDC space. Larger values prevent popping at screen edges but reduce culling efficiency.")]
        [Range(0.0f, 1.0f)]
        public float FrustumCullMargin = 0.3f;
        
        [Tooltip("Sort algorithm for GLES. Bitonic is simpler with fewer dispatches, better for very large splat counts. Radix is faster for smaller counts.")]
        public SortAlgorithm GLESSortAlgorithm = SortAlgorithm.Bitonic;
        
        [Header("Debug")]
        [Tooltip("Log performance metrics every N frames (0 = disabled)")]
        public int LogPerformanceEveryNFrames = 60;

        private GraphicsBuffer _order;
        private GraphicsBuffer _centers;
        private GraphicsBuffer _rotations;
        private GraphicsBuffer _scales;
        private GraphicsBuffer _colors;
        private GraphicsBuffer _shCoeffs;
        
        // GLES packed buffers (4 SSBOs strict for GLES 3.1)
        private GraphicsBuffer _glesPosScale;    // float4: xyz=center, w=scale.x
        private GraphicsBuffer _glesRotation;    // float4: scale.y, scale.z, rot.x, rot.y
        private GraphicsBuffer _glesColor;       // float4: rot.z, rot.w, colorRG_packed, colorBA_packed
        
        private int _shBands;
        private int _shCoeffsPerSplat;

        private uint[] _orderCpu = Array.Empty<uint>();
        private Vector3[] _centersCpu = Array.Empty<Vector3>();
        private int _count;
        private int _visibleCount;

        // scratch buffers for sorting (similar to gsplat-sort-worker.js)
        private uint[] _distances = Array.Empty<uint>();
        private uint[] _countBuffer = Array.Empty<uint>();

        private Vector3 _lastCamPos;
        private Vector3 _lastCamDir;
        private Bounds _localBounds;
        private Bounds _worldBounds;

        private GaussianSplatGPUSorter _gpuSorter;
        private GaussianSplatGPUSorterGLES _gpuSorterGLES;
        private GaussianSplatBitonicSorter _gpuSorterBitonic;
        private MaterialPropertyBlock _mpb;
        private GaussianSplatAsset _loadedAsset; // Track which asset is currently loaded
        private Material _activeMaterial; // The material currently in use
        private bool _isUsingGLES; // Track if we're using GLES path
        
        [System.NonSerialized]
        private bool _needsReload = false; // Set to true after deserialization to force buffer reload
        
        private int _frameCounter = 0; // Track frames for sort frequency
        
        // Performance tracking
        private float _lastSortTimeMs = 0f;
        private float _avgSortTimeMs = 0f;
        private float _avgFrameTimeMs = 0f;
        private int _perfLogCounter = 0;
        private System.Diagnostics.Stopwatch _sortStopwatch = new System.Diagnostics.Stopwatch();
        private float _lastFrameTime = 0f;
        private bool _firstRenderLogged = false;

        private void OnEnable()
        {
            Debug.Log($"[GaussianSplatRenderer] OnEnable called. isPlaying={Application.isPlaying}, PlyAsset={PlyAsset}, PlyUrl={PlyUrl}, PlyFileName={PlyFileName}");
            
            RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            
            // In play mode: auto-load from URL or StreamingAssets
            if (Application.isPlaying)
            {
                string currentSource = GetCurrentSource();
                if (PlyAsset == null && !string.IsNullOrEmpty(currentSource))
                {
                    Debug.Log($"[GaussianSplatRenderer] Starting runtime load from '{currentSource}'");
                    StartCoroutine(LoadPlyRuntime());
                }
                else if (PlyAsset != null)
                {
                    Debug.Log($"[GaussianSplatRenderer] Asset already loaded, loading to GPU");
                    TryLoad();
                }
            }
            // In edit mode: try to load GPU buffers if we have an asset
            // (Editor script will handle loading the asset file itself if PlyAsset is null)
            else
            {
                if (PlyAsset != null)
                {
                    Debug.Log($"[GaussianSplatRenderer] Edit mode: loading existing asset to GPU");
                    TryLoad();
                }
                // If PlyAsset is null but we have a URL/filename, editor script will reload it
                // This handles the case where asset was destroyed when exiting Play mode
            }
        }
        
        private string GetCurrentSource()
        {
            // URL takes precedence
            if (!string.IsNullOrEmpty(PlyUrl))
                return PlyUrl;
            return PlyFileName;
        }

        private void OnDestroy()
        {
            // Clean up runtime-loaded asset (only in play mode)
            if (PlyAsset != null && Application.isPlaying)
            {
                Destroy(PlyAsset);
            }
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
            
            // In edit mode, keep the asset but release GPU buffers
            // In play mode, release everything
            if (!Application.isPlaying)
            {
                ReleaseBuffersOnly();
            }
            else
            {
                Release();
            }
        }

        private void ReleaseBuffersOnly()
        {
            // Release GPU buffers but keep CPU data
            _gpuSorter?.Dispose(); _gpuSorter = null;
            _gpuSorterGLES?.Dispose(); _gpuSorterGLES = null;
            _gpuSorterBitonic?.Dispose(); _gpuSorterBitonic = null;
            _order?.Release(); _order = null;
            _centers?.Release(); _centers = null;
            _rotations?.Release(); _rotations = null;
            _scales?.Release(); _scales = null;
            _colors?.Release(); _colors = null;
            _shCoeffs?.Release(); _shCoeffs = null;
            // GLES packed buffers
            _glesPosScale?.Release(); _glesPosScale = null;
            _glesRotation?.Release(); _glesRotation = null;
            _glesColor?.Release(); _glesColor = null;
        }

        private void OnValidate()
        {
            if (!isActiveAndEnabled) return;
            
            string currentSource = GetCurrentSource();
            
            // Check if source changed in play mode - trigger reload
            if (Application.isPlaying && currentSource != _lastLoadedSource && !string.IsNullOrEmpty(currentSource))
            {
                Debug.Log($"[GaussianSplatRenderer] Source changed from '{_lastLoadedSource}' to '{currentSource}', reloading...");
                // Clear current asset and trigger reload
                if (PlyAsset != null)
                {
                    Destroy(PlyAsset);
                    PlyAsset = null;
                }
                StartCoroutine(LoadPlyRuntime());
            }
            // In edit mode with asset loaded, reload GPU buffers
            else if (!Application.isPlaying && PlyAsset != null)
            {
                TryLoad();
            }
            // In play mode with asset loaded, reload GPU buffers
            else if (Application.isPlaying && PlyAsset != null)
            {
                TryLoad();
            }
        }

        private void Release()
        {
            _gpuSorter?.Dispose(); _gpuSorter = null;
            _gpuSorterGLES?.Dispose(); _gpuSorterGLES = null;
            _gpuSorterBitonic?.Dispose(); _gpuSorterBitonic = null;
            _order?.Release(); _order = null;
            _centers?.Release(); _centers = null;
            _rotations?.Release(); _rotations = null;
            _scales?.Release(); _scales = null;
            _colors?.Release(); _colors = null;
            _shCoeffs?.Release(); _shCoeffs = null;
            // GLES packed buffers
            _glesPosScale?.Release(); _glesPosScale = null;
            _glesRotation?.Release(); _glesRotation = null;
            _glesColor?.Release(); _glesColor = null;
            
            _orderCpu = Array.Empty<uint>();
            _centersCpu = Array.Empty<Vector3>();
            _distances = Array.Empty<uint>();
            _countBuffer = Array.Empty<uint>();
            _count = 0;
            _visibleCount = 0;
            _shBands = 0;
            _shCoeffsPerSplat = 0;
            _localBounds = new Bounds(Vector3.zero, Vector3.zero);
            _worldBounds = new Bounds(Vector3.zero, Vector3.zero);
            _loadedAsset = null;
        }

        /// <summary>
        /// Public method for editor to trigger loading
        /// </summary>
        public void LoadAssetToGPU()
        {
            TryLoad();
        }

        private void TryLoad()
        {
            if (PlyAsset == null)
            {
                Debug.LogWarning("[GaussianSplatRenderer] TryLoad: PlyAsset is null");
                return;
            }
            
            if (Material == null)
            {
                Debug.LogWarning("[GaussianSplatRenderer] TryLoad: Material is null");
                return;
            }

            // Determine if we need GLES path (affects buffer layout, not material selection)
            // The unified shader has SubShaders for both APIs - Unity selects the correct one automatically
            _isUsingGLES = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 ||
                           SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2;
            
            // Use the single unified material - shader has SubShaders for Vulkan/Metal/D3D and GLES
            _activeMaterial = Material;
            
            Debug.Log($"[GaussianSplatRenderer] Using unified shader, API path: {(_isUsingGLES ? "GLES (packed buffers)" : "standard (separate buffers)")}. Graphics API: {SystemInfo.graphicsDeviceType}");

            // Force reload if deserialization occurred (e.g., after scene save/load)
            // or if buffers don't exist or asset changed
            bool needsReload = _needsReload || _order == null || _loadedAsset != PlyAsset || _count == 0;
            
            if (!needsReload)
            {
                Debug.Log($"[GaussianSplatRenderer] TryLoad: skipping reload (_count={_count}, _loadedAsset={_loadedAsset?.name})");
                return;
            }

            Debug.Log($"[GaussianSplatRenderer] TryLoad: loading {PlyAsset.name} to GPU ({PlyAsset.Count} splats)");
            _needsReload = false; // Clear the flag
            Release();
            _loadedAsset = PlyAsset;

            int count = PlyAsset.Count;
            Vector3[] centers = PlyAsset.Centers;
            Vector4[] rotations = PlyAsset.Rotations;
            Vector3[] scales = PlyAsset.Scales;
            Vector4[] colors = PlyAsset.Colors;
            _shBands = PlyAsset.ShBands;
            _shCoeffsPerSplat = PlyAsset.ShCoeffsPerSplat;
            Vector3[] shCoeffs = PlyAsset.ShCoeffs;

            // Apply MaxSplatsToLoad limit BEFORE creating buffers (affects sorting too)
            int originalCount = count;
            if (MaxSplatsToLoad > 0 && MaxSplatsToLoad < count)
            {
                count = MaxSplatsToLoad;
                Array.Resize(ref centers, count);
                Array.Resize(ref rotations, count);
                Array.Resize(ref scales, count);
                Array.Resize(ref colors, count);
                if (shCoeffs != null && shCoeffs.Length > 0)
                {
                    Array.Resize(ref shCoeffs, count * _shCoeffsPerSplat);
                }
            }

            _count = count;
            _visibleCount = _count;

            _centersCpu = centers;
            _orderCpu = new uint[_count];
            for (uint i = 0; i < _orderCpu.Length; i++) _orderCpu[i] = i;

            _order = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, _count, sizeof(uint));
            _order.SetData(_orderCpu);
            
            // Apply scale multiplier
            for (int i = 0; i < scales.Length; i++)
            {
                scales[i] = scales[i] * ScaleMultiplier;
            }

            if (_isUsingGLES)
            {
                // GLES 3.1 STRICT: Must use only 4 SSBOs total (including order buffer)
                // OPTIMIZATION: Precompute 3D covariance on CPU to avoid per-vertex computation
                //
                // 3D covariance is a symmetric 3x3 matrix = 6 unique values (xx, xy, xz, yy, yz, zz)
                // This replaces rotation(4) + scale(3) = 7 floats with covariance(6) = 6 floats
                //
                // Packing scheme (4 SSBOs total):
                // Buffer 0: _SplatOrder (uint) - sort indices
                // Buffer 1: _SplatPosCovA (float4) - pos.xyz, cov.xx
                // Buffer 2: _SplatCovB (float4) - cov.xy, cov.xz, cov.yy, cov.yz
                // Buffer 3: _SplatCovCColor (float4) - cov.zz, unused, packHalf(color.rg), packHalf(color.ba)
                
                Vector4[] posCovA = new Vector4[_count];
                Vector4[] covB = new Vector4[_count];
                Vector4[] covCColor = new Vector4[_count];
                
                for (int i = 0; i < _count; i++)
                {
                    // Precompute 3D covariance from rotation and scale
                    ComputeCovariance3D(rotations[i], scales[i], out Vector3 covA, out Vector3 covBVec);
                    
                    posCovA[i] = new Vector4(centers[i].x, centers[i].y, centers[i].z, covA.x);
                    covB[i] = new Vector4(covA.y, covA.z, covBVec.x, covBVec.y);
                    
                    // Pack color RGBA into 2 floats using half precision
                    uint colorRG = PackHalf2x16(colors[i].x, colors[i].y);
                    uint colorBA = PackHalf2x16(colors[i].z, colors[i].w);
                    covCColor[i] = new Vector4(covBVec.z, 0f, 
                        BitConverter.Int32BitsToSingle((int)colorRG),
                        BitConverter.Int32BitsToSingle((int)colorBA));
                }
                
                _glesPosScale = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);
                _glesRotation = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);
                _glesColor = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);
                
                _glesPosScale.SetData(posCovA);
                _glesRotation.SetData(covB);
                _glesColor.SetData(covCColor);
                
                _activeMaterial.SetBuffer("_SplatOrder", _order);
                _activeMaterial.SetBuffer("_SplatPosCovA", _glesPosScale);
                _activeMaterial.SetBuffer("_SplatCovB", _glesRotation);
                _activeMaterial.SetBuffer("_SplatCovCColor", _glesColor);
                
                Debug.Log($"[GaussianSplatRenderer] Created GLES packed buffers with PRECOMPUTED 3D COVARIANCE (4 SSBOs strict)");
            }
            else
            {
                // Standard path: Separate buffers (5+ SSBOs OK on Vulkan/Metal/D3D)
                _centers = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 3);
                _rotations = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);
                _scales = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 3);
                _colors = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);

                _centers.SetData(centers);
                _rotations.SetData(rotations);
                _scales.SetData(scales);
                _colors.SetData(colors);

                // Set up SH coefficients (not supported in GLES mode)
                if (_shBands > 0 && _shCoeffsPerSplat > 0 && shCoeffs != null && shCoeffs.Length == _count * _shCoeffsPerSplat)
                {
                    _shCoeffs = new GraphicsBuffer(GraphicsBuffer.Target.Structured, shCoeffs.Length, sizeof(float) * 3);
                    _shCoeffs.SetData(shCoeffs);
                    _activeMaterial.SetBuffer("_SHCoeffs", _shCoeffs);
                }

                _activeMaterial.SetBuffer("_SplatOrder", _order);
                _activeMaterial.SetBuffer("_Centers", _centers);
                _activeMaterial.SetBuffer("_Rotations", _rotations);
                _activeMaterial.SetBuffer("_Scales", _scales);
                _activeMaterial.SetBuffer("_Colors", _colors);
            }

            _lastCamPos = new Vector3(float.NaN, 0, 0);
            _lastCamDir = new Vector3(float.NaN, 0, 0);

            _localBounds = ComputeLocalBounds(_centersCpu);
            _worldBounds = TransformBounds(_localBounds, transform.localToWorldMatrix);

            // Create GPU sorter - choose based on graphics API capability
            // OpenGL ES doesn't support wave intrinsics, so use GLES-compatible sorter
            bool useGLESSorter = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 ||
                                 SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2 ||
                                 !SystemInfo.supportsComputeShaders;
            
            if (useGLESSorter)
            {
                if (GLESSortAlgorithm == SortAlgorithm.None)
                {
                    Debug.Log($"[GaussianSplatRenderer] Sorting DISABLED - fastest but may have visual artifacts (Graphics API: {SystemInfo.graphicsDeviceType})");
                    // No sorter needed, identity order will be used
                }
                else if (GLESSortAlgorithm == SortAlgorithm.Bitonic)
                {
                    var sortShaderBitonic = Resources.Load<ComputeShader>("GaussianSplatBitonicSort");
                    if (sortShaderBitonic != null)
                    {
                        _gpuSorterBitonic = new GaussianSplatBitonicSorter(sortShaderBitonic, _count);
                        Debug.Log($"[GaussianSplatRenderer] Using BITONIC sort (Graphics API: {SystemInfo.graphicsDeviceType})");
                    }
                    else
                    {
                        Debug.LogWarning("[GaussianSplatRenderer] Could not load GaussianSplatBitonicSort compute shader. Falling back to radix sort.");
                        GLESSortAlgorithm = SortAlgorithm.Radix;
                    }
                }
                
                if (GLESSortAlgorithm == SortAlgorithm.Radix)
                {
                    var sortShaderGLES = Resources.Load<ComputeShader>("GaussianSplatSortGLES");
                    if (sortShaderGLES != null)
                    {
                        _gpuSorterGLES = new GaussianSplatGPUSorterGLES(sortShaderGLES, _count);
                        Debug.Log($"[GaussianSplatRenderer] Using RADIX sort (Graphics API: {SystemInfo.graphicsDeviceType})");
                    }
                    else
                    {
                        Debug.LogWarning("[GaussianSplatRenderer] Could not load GaussianSplatSortGLES compute shader. Falling back to CPU sorting.");
                    }
                }
            }
            else
            {
                var sortShader = Resources.Load<ComputeShader>("GaussianSplatSort");
                if (sortShader != null)
                {
                    _gpuSorter = new GaussianSplatGPUSorter(sortShader, _count);
                    Debug.Log($"[GaussianSplatRenderer] Using standard GPU sorter with wave intrinsics (Graphics API: {SystemInfo.graphicsDeviceType})");
                }
                else
                {
                    Debug.LogWarning("[GaussianSplatRenderer] Could not load GaussianSplatSort compute shader. Falling back to CPU sorting.");
                }
            }
            
            // Reset frame counter to ensure first frame sorts
            _frameCounter = 0;
            
            Debug.Log($"[GaussianSplatRenderer] TryLoad: completed! GPU buffers created for {_count} splats. Material={_activeMaterial.name}, _order valid={_order != null}");
            Debug.Log($"[GaussianSplatRenderer] Local bounds: center={_localBounds.center}, size={_localBounds.size}");
            Debug.Log($"[GaussianSplatRenderer] World bounds: center={_worldBounds.center}, size={_worldBounds.size}");
            Debug.Log($"[GaussianSplatRenderer] Transform position: {transform.position}, rotation: {transform.rotation.eulerAngles}, scale: {transform.lossyScale}");
        }

        private IEnumerator LoadPlyRuntime()
        {
            Debug.Log($"[GaussianSplatRenderer] LoadPlyRuntime coroutine started");
            
            // Check if component still exists (might be destroyed when exiting Play mode)
            if (this == null)
            {
                Debug.LogWarning("[GaussianSplatRenderer] Component was destroyed, cancelling PLY load");
                yield break;
            }
            
            string source = GetCurrentSource();
            if (string.IsNullOrEmpty(source))
            {
                Debug.LogError("[GaussianSplatRenderer] Both PlyUrl and PlyFileName are empty. Cannot load PLY.");
                yield break;
            }

            string loadPath;
            bool isUrl = !string.IsNullOrEmpty(PlyUrl);
            
            if (isUrl)
            {
                loadPath = PlyUrl;
                // Check URL extension early to provide better error message
                string urlExtension = Path.GetExtension(loadPath).ToLower();
                if (!string.IsNullOrEmpty(urlExtension) && urlExtension != ".ply")
                {
                    Debug.LogWarning($"[GaussianSplatRenderer] URL has extension '{urlExtension}' but expected '.ply'. Gaussian Splatting requires PLY files. The file may not load correctly.");
                }
                Debug.Log($"[GaussianSplatRenderer] Loading PLY from URL: {loadPath}");
            }
            else
            {
                loadPath = Path.Combine(Application.streamingAssetsPath, "GaussianSplatting", PlyFileName);
                Debug.Log($"[GaussianSplatRenderer] Loading PLY from StreamingAssets: {loadPath}");
            }
            
            Debug.Log($"[GaussianSplatRenderer] Platform: {Application.platform}, path contains '://': {loadPath.Contains("://")}");

            byte[] fileBytes = null;

            // Use UnityWebRequest for URLs or StreamingAssets on platforms that need it
            if (isUrl || loadPath.Contains("://") || Application.platform == RuntimePlatform.Android)
            {
                Debug.Log($"[GaussianSplatRenderer] Using UnityWebRequest");
                using (UnityWebRequest uwr = UnityWebRequest.Get(loadPath))
                {
                    yield return uwr.SendWebRequest();

                    if (uwr.result == UnityWebRequest.Result.ConnectionError || 
                        uwr.result == UnityWebRequest.Result.ProtocolError)
                    {
                        Debug.LogError($"[GaussianSplatRenderer] Error loading PLY: {uwr.error}");
                        yield break;
                    }

                    fileBytes = uwr.downloadHandler.data;
                    Debug.Log($"[GaussianSplatRenderer] UnityWebRequest completed, received {fileBytes?.Length ?? 0} bytes");
                }
            }
            else
            {
                // For Editor and Standalone desktop builds with local files
                Debug.Log($"[GaussianSplatRenderer] Using File.ReadAllBytes");
                if (!File.Exists(loadPath))
                {
                    Debug.LogError($"[GaussianSplatRenderer] PLY file not found: {loadPath}");
                    yield break;
                }

                try
                {
                    fileBytes = File.ReadAllBytes(loadPath);
                    Debug.Log($"[GaussianSplatRenderer] Read {fileBytes.Length} bytes from file");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[GaussianSplatRenderer] Error reading PLY file: {ex.Message}");
                    yield break;
                }
            }

            if (fileBytes == null || fileBytes.Length == 0)
            {
                Debug.LogError("[GaussianSplatRenderer] PLY file is empty");
                yield break;
            }

            // Validate file format - check if it's actually a PLY file
            if (fileBytes.Length < 3)
            {
                Debug.LogError("[GaussianSplatRenderer] File is too small to be a valid PLY file");
                yield break;
            }

            // Check PLY header magic bytes (PLY files start with "ply")
            string headerStart = Encoding.ASCII.GetString(fileBytes, 0, Math.Min(3, fileBytes.Length));
            if (!headerStart.Equals("ply", StringComparison.OrdinalIgnoreCase))
            {
                string fileExtension = isUrl ? Path.GetExtension(loadPath).ToLower() : Path.GetExtension(PlyFileName).ToLower();
                Debug.LogError($"[GaussianSplatRenderer] File is not a PLY file. File extension: '{fileExtension}'. PLY files must start with 'ply' header. The URL/file may point to a different file format (e.g., .glb, .obj, .fbx). Please use a .ply file containing Gaussian Splat data.");
                yield break;
            }

            Debug.Log($"[GaussianSplatRenderer] Loaded {fileBytes.Length} bytes, parsing PLY...");

            // Check if component still exists before parsing
            if (this == null)
            {
                Debug.LogWarning("[GaussianSplatRenderer] Component was destroyed during download, cancelling PLY load");
                yield break;
            }

            // Parse PLY data (always use RightHandedToUnity conversion)
            PlyGaussianSplat plyData = null;
            try
            {
                plyData = PlyGaussianSplatLoader.Load(fileBytes, CoordinateConversion.RightHandedToUnity);
                Debug.Log($"[GaussianSplatRenderer] PLY parsed successfully: {plyData.Count} splats");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GaussianSplatRenderer] Error parsing PLY: {ex.Message}\n{ex.StackTrace}");
                yield break;
            }

            if (plyData == null || plyData.Count == 0)
            {
                Debug.LogError("[GaussianSplatRenderer] PLY file contains no splats");
                yield break;
            }

            // Check again if component still exists before creating asset
            if (this == null)
            {
                Debug.LogWarning("[GaussianSplatRenderer] Component was destroyed during PLY parsing, cancelling asset creation");
                yield break;
            }

            Debug.Log($"[GaussianSplatRenderer] Creating ScriptableObject asset...");
            
            // Create runtime asset
            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            string assetName = isUrl ? "url_splat" : Path.GetFileNameWithoutExtension(PlyFileName);
            asset.name = assetName;
            asset.Count = plyData.Count;
            asset.Centers = plyData.Centers;
            asset.Rotations = plyData.Rotations;
            asset.Scales = plyData.Scales;
            asset.Colors = plyData.Colors;
            asset.ShBands = plyData.ShBands;
            asset.ShCoeffsPerSplat = plyData.ShCoeffsPerSplat;
            asset.ShCoeffs = plyData.ShCoeffs;

            PlyAsset = asset;
            _lastLoadedSource = source;
            Debug.Log($"[GaussianSplatRenderer] Successfully loaded from '{source}' with {plyData.Count} splats");

            // Now load the asset into GPU buffers
            Debug.Log($"[GaussianSplatRenderer] Calling TryLoad to create GPU buffers...");
            TryLoad();
            Debug.Log($"[GaussianSplatRenderer] LoadPlyRuntime coroutine completed");
        }

        private void BeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            // Skip if using render feature (it will call RenderWithCommandBuffer instead)
            if (UseRenderFeature && GraphicsSettings.currentRenderPipeline != null)
            {
                return;
            }
            
            // Lazy reload if data was released (e.g., after scene save in editor)
            if ((_needsReload || _order == null) && PlyAsset != null && Material != null)
                TryLoad();
            
            if (!enabled)
            {
                Debug.Log($"[GaussianSplatRenderer] Skipping render - component disabled. Camera: {camera.name}");
                return;
            }
            if (_activeMaterial == null)
            {
                Debug.LogWarning($"[GaussianSplatRenderer] Skipping render - Material is null. Camera: {camera.name}");
                return;
            }
            if (_order == null)
            {
                Debug.LogWarning($"[GaussianSplatRenderer] Skipping render - GPU buffers not initialized. Camera: {camera.name}");
                return;
            }
            if (_count == 0)
            {
                Debug.LogWarning($"[GaussianSplatRenderer] Skipping render - no splats loaded. Camera: {camera.name}");
                return;
            }
            
            // Scene view handling - always draw
            if (camera.cameraType != CameraType.SceneView)
            {
                // For non-scene cameras, filter by TargetCamera
                var cam = TargetCamera != null ? TargetCamera : Camera.main;
                if (cam != null && camera != cam)
                {
                    Debug.Log($"[GaussianSplatRenderer] Skipping render - camera mismatch. Target: {cam?.name}, Current: {camera.name}");
                    return;
                }
            }

            RenderForCamera(camera, (CommandBuffer)null);
        }

        private void OnRenderObject()
        {
            // Built-in RP fallback
            if (GraphicsSettings.currentRenderPipeline != null) return;
            if (!enabled || _activeMaterial == null || _order == null || _count == 0) return;

            var camera = Camera.current;
            if (camera == null) return;
            // Always render in scene view

            var cam = TargetCamera != null ? TargetCamera : Camera.main;
            if (cam != null && camera != cam) return;

            RenderForCamera(camera, (CommandBuffer)null);
        }

        /// <summary>
        /// Called by GaussianSplatRenderFeature to render with proper URP matrix setup.
        /// </summary>
        public void RenderWithCommandBuffer(CommandBuffer cmd, Camera camera)
        {
            // Lazy reload if data was released (e.g., after scene save in editor)
            if ((_needsReload || _order == null) && PlyAsset != null && Material != null)
                TryLoad();
            
            if (!enabled || _activeMaterial == null || _order == null || _count == 0) return;
            
            // Scene view handling - always draw
            if (camera.cameraType != CameraType.SceneView)
            {
                // For non-scene cameras, filter by TargetCamera
                var cam = TargetCamera != null ? TargetCamera : Camera.main;
                if (cam != null && camera != cam) return;
            }

            RenderForCamera(camera, cmd);
        }

        /// <summary>
        /// Called by GaussianSplatRenderFeature RenderGraph path (URP uses RasterCommandBuffer).
        /// </summary>
        public void RenderWithRasterCommandBuffer(RasterCommandBuffer cmd, Camera camera)
        {
            // Lazy reload if data was released (e.g., after scene save in editor)
            if ((_needsReload || _order == null) && PlyAsset != null && Material != null)
                TryLoad();
            
            if (!enabled || _activeMaterial == null || _order == null || _count == 0) return;
            
            // Scene view handling - always draw
            if (camera.cameraType != CameraType.SceneView)
            {
                // For non-scene cameras, filter by TargetCamera
                var cam = TargetCamera != null ? TargetCamera : Camera.main;
                if (cam != null && camera != cam) return;
            }

            RenderForCamera(camera, cmd);
        }

        private void RenderForCamera(Camera camera, CommandBuffer cmd)
        {
            // Log first render call
            if (!_firstRenderLogged)
            {
                _firstRenderLogged = true;
                Debug.Log($"[GS-PERF] First render call! Camera={camera.name}, UseRenderFeature={UseRenderFeature}, cmd={(cmd != null ? "CommandBuffer" : "null")}");
            }
            
            // Track frame time
            float currentTime = Time.realtimeSinceStartup;
            float frameTime = (currentTime - _lastFrameTime) * 1000f;
            _lastFrameTime = currentTime;
            _avgFrameTimeMs = Mathf.Lerp(_avgFrameTimeMs, frameTime, 0.1f);
            
            // Sort based on frame frequency
            _frameCounter++;
            bool shouldSort = (_frameCounter % SortEveryNFrames) == 0;
            
            if (shouldSort)
            {
                _sortStopwatch.Restart();
                
                // transform camera into object space (matches gsplat-instance.js sort path)
                var camPosOS = transform.InverseTransformPoint(camera.transform.position);
                var camDirOS = transform.InverseTransformDirection(camera.transform.forward).normalized;

                if (GLESSortAlgorithm == SortAlgorithm.None && _isUsingGLES)
                {
                    // No sorting - use identity order (fastest, may have visual artifacts)
                    _visibleCount = _count;
                }
                else if (_gpuSorterBitonic != null)
                {
                    // Bitonic sort path
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;
                    
                    if (EnableFrustumCulling)
                    {
                        Matrix4x4 viewProjMatrix = camera.projectionMatrix * sortViewMatrix * sortModelMatrix;
                        if (_gpuSorterBitonic.Sort(_glesPosScale, _order, modelViewMatrix, viewProjMatrix, camPosOS, camDirOS, FrustumCullMargin, out int visible))
                        {
                            _visibleCount = visible;
                        }
                    }
                    else
                    {
                        _gpuSorterBitonic.Sort(_glesPosScale, _order, modelViewMatrix, camPosOS, camDirOS);
                        _visibleCount = _count;
                    }
                }
                else if (_gpuSorterGLES != null)
                {
                    // Radix sort path
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;
                    
                    if (EnableFrustumCulling)
                    {
                        Matrix4x4 viewProjMatrix = camera.projectionMatrix * sortViewMatrix * sortModelMatrix;
                        if (_gpuSorterGLES.Sort(_glesPosScale, _order, modelViewMatrix, viewProjMatrix, camPosOS, camDirOS, FrustumCullMargin, out int visible))
                        {
                            _visibleCount = visible;
                        }
                    }
                    else
                    {
                        _gpuSorterGLES.Sort(_glesPosScale, _order, modelViewMatrix, camPosOS, camDirOS);
                        _visibleCount = _count;
                    }
                }
                else if (_gpuSorter != null)
                {
                    // Standard GPU sorter (wave intrinsics)
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;
                    _gpuSorter.Sort(_centers, _order, modelViewMatrix, camPosOS, camDirOS);
                    _visibleCount = _count;
                }
                // No CPU fallback - if no GPU sorter, identity order is used
                
                _sortStopwatch.Stop();
                _lastSortTimeMs = (float)_sortStopwatch.Elapsed.TotalMilliseconds;
                _avgSortTimeMs = Mathf.Lerp(_avgSortTimeMs, _lastSortTimeMs, 0.1f);
            }
            
            // Performance logging (CommandBuffer path)
            if (LogPerformanceEveryNFrames > 0)
            {
                _perfLogCounter++;
                if (_perfLogCounter >= LogPerformanceEveryNFrames)
                {
                    _perfLogCounter = 0;
                    float fps = _avgFrameTimeMs > 0 ? 1000f / _avgFrameTimeMs : 0;
                    float cullPercent = _count > 0 ? (1f - (float)_visibleCount / _count) * 100f : 0;
                    Debug.Log($"[GS-PERF] FPS: {fps:F1} | Frame: {_avgFrameTimeMs:F1}ms | Sort: {_avgSortTimeMs:F2}ms | Visible: {_visibleCount}/{_count} ({cullPercent:F0}% culled) | Vertices: {_visibleCount * 6}");
                }
            }

            float w = camera.pixelWidth;
            float h = camera.pixelHeight;
            var viewportSize = new Vector4(w, h, 1f / Mathf.Max(1f, w), 1f / Mathf.Max(1f, h));


            // Ensure buffers are bound (may be lost after scene save/reload)
            _activeMaterial.SetBuffer("_SplatOrder", _order);
            if (_isUsingGLES)
            {
                _activeMaterial.SetBuffer("_SplatPosCovA", _glesPosScale);
                _activeMaterial.SetBuffer("_SplatCovB", _glesRotation);
                _activeMaterial.SetBuffer("_SplatCovCColor", _glesColor);
            }
            else
            {
                _activeMaterial.SetBuffer("_Centers", _centers);
                _activeMaterial.SetBuffer("_Rotations", _rotations);
                _activeMaterial.SetBuffer("_Scales", _scales);
                _activeMaterial.SetBuffer("_Colors", _colors);
                if (_shCoeffs != null)
                    _activeMaterial.SetBuffer("_SHCoeffs", _shCoeffs);
            }

            if (cmd != null)
            {
                // CommandBuffer path (URP Render Feature / RenderGraph):
                // Use MaterialPropertyBlock so per-object/per-camera data is captured per draw.
                _mpb.Clear();
                _mpb.SetVector("_ViewportSize", viewportSize);
                _mpb.SetFloat("_IsOrtho", camera.orthographic ? 1f : 0f);
                _mpb.SetInt("_NumSplats", _visibleCount);
                _mpb.SetInt("_SHBands", _isUsingGLES ? 0 : _shBands);
                _mpb.SetInt("_SHCoeffsPerSplat", _isUsingGLES ? 0 : _shCoeffsPerSplat);

                // Custom model matrices (UNITY_MATRIX_M can't be written in URP constant buffers)
                _mpb.SetMatrix("_SplatObjectToWorld", transform.localToWorldMatrix);
                _mpb.SetMatrix("_SplatWorldToObject", transform.worldToLocalMatrix);
            }
            else
            {
                // Immediate path (beginCameraRendering / built-in fallback)
                _activeMaterial.SetVector("_ViewportSize", viewportSize);
                _activeMaterial.SetFloat("_IsOrtho", camera.orthographic ? 1f : 0f);
                _activeMaterial.SetInt("_NumSplats", _visibleCount);
                _activeMaterial.SetInt("_SHBands", _isUsingGLES ? 0 : _shBands);
                _activeMaterial.SetInt("_SHCoeffsPerSplat", _isUsingGLES ? 0 : _shCoeffsPerSplat);

                // Custom model matrices (UNITY_MATRIX_M can't be written in URP constant buffers)
                _activeMaterial.SetMatrix("_SplatObjectToWorld", transform.localToWorldMatrix);
                _activeMaterial.SetMatrix("_SplatWorldToObject", transform.worldToLocalMatrix);
            }

            // Draw
            // 6 vertices per splat (2 triangles)
            // Recalculate world bounds from current transform (so rotation/movement works)
            var bounds = _localBounds.size.sqrMagnitude > 0 
                ? TransformBounds(_localBounds, transform.localToWorldMatrix) 
                : new Bounds(transform.position, Vector3.one * 100000f);
            
            if (cmd != null)
            {
                // URP Render Feature path: use CommandBuffer for proper matrix setup
                cmd.DrawProcedural(Matrix4x4.identity, _activeMaterial, 0, MeshTopology.Triangles, _visibleCount * 6, 1, _mpb);
            }
            else if (GraphicsSettings.currentRenderPipeline == null)
            {
                // Built-in RP: DrawProceduralNow inside OnRenderObject is the most reliable path.
                _activeMaterial.SetPass(0);
                Graphics.DrawProceduralNow(MeshTopology.Triangles, _visibleCount * 6, 1);
            }
            else
            {
                // SRP fallback (when not using render feature): bounds-based draw call
                Graphics.DrawProcedural(_activeMaterial, bounds, MeshTopology.Triangles, _visibleCount * 6, 1, camera);
            }
        }

        private void RenderForCamera(Camera camera, RasterCommandBuffer cmd)
        {
            // Log first render call
            if (!_firstRenderLogged)
            {
                _firstRenderLogged = true;
                Debug.Log($"[GS-PERF] First render call (RasterCB)! Camera={camera.name}, UseRenderFeature={UseRenderFeature}");
            }
            
            // Track frame time for this path too
            float currentTime = Time.realtimeSinceStartup;
            float frameTime = (currentTime - _lastFrameTime) * 1000f;
            _lastFrameTime = currentTime;
            _avgFrameTimeMs = Mathf.Lerp(_avgFrameTimeMs, frameTime, 0.1f);
            
            // Sort based on frame frequency (share counter with other render path)
            _frameCounter++;
            bool shouldSort = (_frameCounter % SortEveryNFrames) == 0;
            
            if (shouldSort)
            {
                // transform camera into object space (matches gsplat-instance.js sort path)
                var camPosOS = transform.InverseTransformPoint(camera.transform.position);
                var camDirOS = transform.InverseTransformDirection(camera.transform.forward).normalized;

                if (GLESSortAlgorithm == SortAlgorithm.None && _isUsingGLES)
                {
                    // No sorting - use identity order (fastest, may have visual artifacts)
                    _visibleCount = _count;
                }
                else if (_gpuSorterBitonic != null)
                {
                    // Bitonic sort path
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;
                    
                    if (EnableFrustumCulling)
                    {
                        Matrix4x4 viewProjMatrix = camera.projectionMatrix * sortViewMatrix * sortModelMatrix;
                        if (_gpuSorterBitonic.Sort(_glesPosScale, _order, modelViewMatrix, viewProjMatrix, camPosOS, camDirOS, FrustumCullMargin, out int visible))
                        {
                            _visibleCount = visible;
                        }
                    }
                    else
                    {
                        _gpuSorterBitonic.Sort(_glesPosScale, _order, modelViewMatrix, camPosOS, camDirOS);
                        _visibleCount = _count;
                    }
                }
                else if (_gpuSorterGLES != null)
                {
                    // Radix sort path
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;
                    
                    if (EnableFrustumCulling)
                    {
                        Matrix4x4 viewProjMatrix = camera.projectionMatrix * sortViewMatrix * sortModelMatrix;
                        if (_gpuSorterGLES.Sort(_glesPosScale, _order, modelViewMatrix, viewProjMatrix, camPosOS, camDirOS, FrustumCullMargin, out int visible))
                        {
                            _visibleCount = visible;
                        }
                    }
                    else
                    {
                        _gpuSorterGLES.Sort(_glesPosScale, _order, modelViewMatrix, camPosOS, camDirOS);
                        _visibleCount = _count;
                    }
                }
                else if (_gpuSorter != null)
                {
                    // Standard GPU sorter (wave intrinsics)
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;
                    _gpuSorter.Sort(_centers, _order, modelViewMatrix, camPosOS, camDirOS);
                    _visibleCount = _count;
                }
                // No CPU fallback - if no GPU sorter, identity order is used
            }

            float w = camera.pixelWidth;
            float h = camera.pixelHeight;
            var viewportSize = new Vector4(w, h, 1f / Mathf.Max(1f, w), 1f / Mathf.Max(1f, h));


            // RasterCommandBuffer path (RenderGraph): use MPB so per-object/per-camera data is captured per draw.
            // Note: Buffers must be set on Material (not MaterialPropertyBlock) as MPB doesn't support buffers
            _activeMaterial.SetBuffer("_SplatOrder", _order);
            if (_isUsingGLES)
            {
                _activeMaterial.SetBuffer("_SplatPosCovA", _glesPosScale);
                _activeMaterial.SetBuffer("_SplatCovB", _glesRotation);
                _activeMaterial.SetBuffer("_SplatCovCColor", _glesColor);
            }
            else
            {
                _activeMaterial.SetBuffer("_Centers", _centers);
                _activeMaterial.SetBuffer("_Rotations", _rotations);
                _activeMaterial.SetBuffer("_Scales", _scales);
                _activeMaterial.SetBuffer("_Colors", _colors);
                if (_shCoeffs != null)
                    _activeMaterial.SetBuffer("_SHCoeffs", _shCoeffs);
            }
            
            _mpb.Clear();
            _mpb.SetVector("_ViewportSize", viewportSize);
            _mpb.SetFloat("_IsOrtho", camera.orthographic ? 1f : 0f);
            _mpb.SetInt("_NumSplats", _visibleCount);
            _mpb.SetInt("_SHBands", _isUsingGLES ? 0 : _shBands);
            _mpb.SetInt("_SHCoeffsPerSplat", _isUsingGLES ? 0 : _shCoeffsPerSplat);
            _mpb.SetMatrix("_SplatObjectToWorld", transform.localToWorldMatrix);
            _mpb.SetMatrix("_SplatWorldToObject", transform.worldToLocalMatrix);

            // Set view/projection matrices - use raw camera projection (no Y-flip)
            var viewMatrix = camera.worldToCameraMatrix;
            var projMatrix = camera.projectionMatrix;
            cmd.SetViewProjectionMatrices(viewMatrix, projMatrix);
            
            // Pass the projection matrix values for focal length calculation
            _mpb.SetFloat("_CamProjM00", projMatrix[0, 0]);
            _mpb.SetFloat("_CamProjM11", projMatrix[1, 1]);

            // Draw (URP/RenderGraph) - buffers are set on Material, other properties in MPB
            cmd.DrawProcedural(Matrix4x4.identity, _activeMaterial, 0, MeshTopology.Triangles, _visibleCount * 6, 1, _mpb);
            
            // Performance logging (shared with CommandBuffer path)
            if (LogPerformanceEveryNFrames > 0)
            {
                _perfLogCounter++;
                if (_perfLogCounter >= LogPerformanceEveryNFrames)
                {
                    _perfLogCounter = 0;
                    float fps = _avgFrameTimeMs > 0 ? 1000f / _avgFrameTimeMs : 0;
                    float cullPercent = _count > 0 ? (1f - (float)_visibleCount / _count) * 100f : 0;
                    Debug.Log($"[GS-PERF] FPS: {fps:F1} | Frame: {_avgFrameTimeMs:F1}ms | Visible: {_visibleCount}/{_count} ({cullPercent:F0}% culled) | Vertices: {_visibleCount * 6}");
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            // Always draw bounds gizmo when selected
            if (_localBounds.size.sqrMagnitude <= 0) return;

            Gizmos.color = Color.yellow;
            var b = TransformBounds(_localBounds, transform.localToWorldMatrix);
            Gizmos.DrawWireCube(b.center, b.size);

            if (_centersCpu != null && _centersCpu.Length > 0)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawSphere(transform.TransformPoint(_centersCpu[0]), 0.02f);
            }
        }

        private static Bounds ComputeLocalBounds(Vector3[] centers)
        {
            if (centers == null || centers.Length == 0)
                return new Bounds(Vector3.zero, Vector3.zero);

            var min = centers[0];
            var max = centers[0];
            for (int i = 1; i < centers.Length; i++)
            {
                var c = centers[i];
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
            }

            var b = new Bounds((min + max) * 0.5f, (max - min));
            b.Expand(Mathf.Max(0.01f, b.size.magnitude * 0.02f));
            return b;
        }

        private static Bounds TransformBounds(Bounds localBounds, Matrix4x4 localToWorld)
        {
            var center = localToWorld.MultiplyPoint3x4(localBounds.center);
            var ext = localBounds.extents;
            var axisX = localToWorld.MultiplyVector(new Vector3(ext.x, 0, 0));
            var axisY = localToWorld.MultiplyVector(new Vector3(0, ext.y, 0));
            var axisZ = localToWorld.MultiplyVector(new Vector3(0, 0, ext.z));
            var worldExt = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z)
            );
            return new Bounds(center, worldExt * 2f);
        }

        /// <summary>
        /// Sort order is back-to-front along camera direction, and computes a visible count that excludes splats behind the camera.
        /// This is a C# port of the core approach in `gsplat/gsplat-sort-worker.js`.
        /// </summary>
        private void SortBackToFront(Vector3 camPosOS, Vector3 camDirOS)
        {
            int n = _count;
            if (_distances.Length != n) _distances = new uint[n];

            // Calculate a min/max range using center projection only (no chunk optimization here).
            // We use dot(center, dir) as distance along view direction (in object space).
            float minDist = float.PositiveInfinity;
            float maxDist = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var c = _centersCpu[i];
                float d = c.x * camDirOS.x + c.y * camDirOS.y + c.z * camDirOS.z;
                if (d < minDist) minDist = d;
                if (d > maxDist) maxDist = d;
            }

            // bits in worker: clamp 10..20 based on N
            int compareBits = Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(Mathf.Max(1, n / 4), 2f)), 10, 20);
            int bucketCount = (1 << compareBits) + 1;

            if (_countBuffer.Length != bucketCount) _countBuffer = new uint[bucketCount];
            else Array.Clear(_countBuffer, 0, _countBuffer.Length);

            float range = maxDist - minDist;
            if (range < 1e-6f)
            {
                for (int i = 0; i < n; i++)
                {
                    _distances[i] = 0;
                    _countBuffer[0]++;
                }
            }
            else
            {
                float invRange = 1f / range;
                for (int i = 0; i < n; i++)
                {
                    var c = _centersCpu[i];
                    float d = (c.x * camDirOS.x + c.y * camDirOS.y + c.z * camDirOS.z - minDist) * invRange;
                    uint key = (uint)Mathf.Clamp((int)(d * (bucketCount - 1)), 0, bucketCount - 1);
                    _distances[i] = key;
                    _countBuffer[key]++;
                }
            }

            // prefix sum
            for (int i = 1; i < bucketCount; i++)
                _countBuffer[i] += _countBuffer[i - 1];

            // counting sort into _orderCpu: REVERSE order - far splats first (index 0), near splats last
            // Back-to-front: far splats drawn first, near splats drawn last (painter's algorithm)
            for (int i = 0; i < n; i++)
            {
                uint key = _distances[i];
                int destIndex = n - 1 - (int)(--_countBuffer[key]);
                _orderCpu[destIndex] = (uint)i;
            }

            // visible count: exclude behind camera plane using cam position
            float cameraDist = camPosOS.x * camDirOS.x + camPosOS.y * camDirOS.y + camPosOS.z * camDirOS.z;

            float DistAtOrderIndex(int orderIndex)
            {
                var c = _centersCpu[_orderCpu[orderIndex]];
                return c.x * camDirOS.x + c.y * camDirOS.y + c.z * camDirOS.z - cameraDist;
            }

            // Find last splat with dist >= 0 (in front) using binary search on the sorted order
            // Our order is far..near (DECREASING), so dist decreases. Behind-camera splats (dist < 0) are at the end.
            int lo = 0, hi = n - 1, lastNonNeg = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (DistAtOrderIndex(mid) >= 0f)
                {
                    lastNonNeg = mid;
                    lo = mid + 1;  // search right half for later valid index
                }
                else
                {
                    hi = mid - 1;  // search left half
                }
            }

            // We draw [0..lastNonNeg], so visibleCount = lastNonNeg + 1
            _visibleCount = Mathf.Clamp(lastNonNeg + 1, 0, n);
        }

        // ISerializationCallbackReceiver implementation
        // This ensures buffers are reloaded after Unity serializes/deserializes (e.g., scene save/load)
        public void OnBeforeSerialize()
        {
            // Nothing to do before serialization
        }

        public void OnAfterDeserialize()
        {
            // Mark that we need to reload buffers after deserialization
            // GraphicsBuffers don't survive serialization and must be recreated
            _needsReload = true;
        }
    }
}



