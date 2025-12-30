using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    public enum SortAlgorithm
    {
        Radix,
        Bitonic,
        None
    }

    [ExecuteAlways]
    public sealed class GaussianSplatRenderer : MonoBehaviour, ISerializationCallbackReceiver
    {
        private static uint PackHalf2x16(float x, float y)
        {
            ushort hx = FloatToHalf(x);
            ushort hy = FloatToHalf(y);
            return (uint)hx | ((uint)hy << 16);
        }

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

        private static void ComputeCovariance3D(Vector4 rotation, Vector3 scale, out Vector3 covA, out Vector3 covB)
        {
            float qx = rotation.x, qy = rotation.y, qz = rotation.z, qw = rotation.w;
            float len = Mathf.Sqrt(qx*qx + qy*qy + qz*qz + qw*qw);
            if (len > 0.0001f) { qx /= len; qy /= len; qz /= len; qw /= len; }

            float r00 = 1f - 2f * (qy*qy + qz*qz);
            float r01 = 2f * (qx*qy - qw*qz);
            float r02 = 2f * (qx*qz + qw*qy);
            float r10 = 2f * (qx*qy + qw*qz);
            float r11 = 1f - 2f * (qx*qx + qz*qz);
            float r12 = 2f * (qy*qz - qw*qx);
            float r20 = 2f * (qx*qz - qw*qy);
            float r21 = 2f * (qy*qz + qw*qx);
            float r22 = 1f - 2f * (qx*qx + qy*qy);

            float m00 = r00 * scale.x, m01 = r01 * scale.y, m02 = r02 * scale.z;
            float m10 = r10 * scale.x, m11 = r11 * scale.y, m12 = r12 * scale.z;
            float m20 = r20 * scale.x, m21 = r21 * scale.y, m22 = r22 * scale.z;

            float v00 = m00*m00 + m01*m01 + m02*m02;
            float v01 = m00*m10 + m01*m11 + m02*m12;
            float v02 = m00*m20 + m01*m21 + m02*m22;
            float v11 = m10*m10 + m11*m11 + m12*m12;
            float v12 = m10*m20 + m11*m21 + m12*m22;
            float v22 = m20*m20 + m21*m21 + m22*m22;

            covA = new Vector3(v00, v01, v02);
            covB = new Vector3(v11, v12, v22);
        }

        [Header("Rendering")]
        public Material Material;
        [Tooltip("Material for OpenGL ES (auto-selected when on GLES). Leave empty to auto-create from GaussianSplatGLES shader.")]
        public Material MaterialGLES;
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
        [Tooltip("Enable GPU frustum culling to skip off-screen splats.")]
        public bool EnableFrustumCulling = true;

        [Tooltip("Extra margin for frustum culling in NDC space.")]
        [Range(0.0f, 1.0f)]
        public float FrustumCullMargin = 0.3f;

        [Tooltip("Sort algorithm for GLES. Bitonic is simpler with fewer dispatches.")]
        public SortAlgorithm GLESSortAlgorithm = SortAlgorithm.Bitonic;

        [Header("Debug")]
        [Tooltip("Log performance metrics every N frames (0 = disabled)")]
        public int LogPerformanceEveryNFrames = 0;

        [Tooltip("Force use of mobile GPU sorting shader (for testing Android compatibility in editor).")]
        [SerializeField] private bool _forceMobileGPUSorting = false;

        [SerializeField] private bool _forceCPUSorting = false;

        private GaussianSplatData _splatData;
        private GraphicsBuffer _orderBuffer;
        private GraphicsBuffer _centersBuffer;
        private GraphicsBuffer _rotationsBuffer;
        private GraphicsBuffer _scalesBuffer;
        private GraphicsBuffer _colorsBuffer;
        private GraphicsBuffer _shCoeffsBuffer;

        private GraphicsBuffer _glesPosScale;
        private GraphicsBuffer _glesRotation;
        private GraphicsBuffer _glesColor;

        private int _shBands;
        private int _shCoeffsPerSplat;

        private uint[] _orderCpu = Array.Empty<uint>();
        private Vector3[] _centersCpu = Array.Empty<Vector3>();
        private int _count;
        private int _visibleCount;

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
        private GaussianSplatData _loadedData;
        private Material _activeMaterial;
        private bool _isUsingGLES;
        private bool _useMobilePath;
        private bool _lastUseMobilePath;

        [System.NonSerialized]
        private bool _needsReload = false;

        private int _frameCounter = 0;

        private float _lastSortTimeMs = 0f;
        private float _avgSortTimeMs = 0f;
        private float _avgFrameTimeMs = 0f;
        private int _perfLogCounter = 0;
        private System.Diagnostics.Stopwatch _sortStopwatch = new System.Diagnostics.Stopwatch();
        private float _lastFrameTime = 0f;
        private bool _firstRenderLogged = false;

        public bool IsLoaded => _count > 0 && _orderBuffer != null;
        public int SplatCount => _count;

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            if (_splatData != null)
            {
                LoadToGPU();
            }
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
            ReleaseBuffersOnly();
        }

        private void OnDestroy()
        {
            Release();
        }

        private void ReleaseBuffersOnly()
        {
            _gpuSorter?.Dispose(); _gpuSorter = null;
            _gpuSorterGLES?.Dispose(); _gpuSorterGLES = null;
            _gpuSorterBitonic?.Dispose(); _gpuSorterBitonic = null;
            _orderBuffer?.Release(); _orderBuffer = null;
            _centersBuffer?.Release(); _centersBuffer = null;
            _rotationsBuffer?.Release(); _rotationsBuffer = null;
            _scalesBuffer?.Release(); _scalesBuffer = null;
            _colorsBuffer?.Release(); _colorsBuffer = null;
            _shCoeffsBuffer?.Release(); _shCoeffsBuffer = null;
            _glesPosScale?.Release(); _glesPosScale = null;
            _glesRotation?.Release(); _glesRotation = null;
            _glesColor?.Release(); _glesColor = null;
        }

        private void OnValidate()
        {
            if (!isActiveAndEnabled) return;

            if (_splatData != null)
            {
                LoadToGPU();
            }
        }

        private void Release()
        {
            _gpuSorter?.Dispose(); _gpuSorter = null;
            _gpuSorterGLES?.Dispose(); _gpuSorterGLES = null;
            _gpuSorterBitonic?.Dispose(); _gpuSorterBitonic = null;
            _orderBuffer?.Release(); _orderBuffer = null;
            _centersBuffer?.Release(); _centersBuffer = null;
            _rotationsBuffer?.Release(); _rotationsBuffer = null;
            _scalesBuffer?.Release(); _scalesBuffer = null;
            _colorsBuffer?.Release(); _colorsBuffer = null;
            _shCoeffsBuffer?.Release(); _shCoeffsBuffer = null;
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
            _loadedData = null;
        }

        public void SetSplatData(GaussianSplatData data)
        {
            _splatData = data;
            _needsReload = true;
            _loadedData = null;

            if (isActiveAndEnabled && data != null)
            {
                LoadToGPU();
            }
        }

        public GaussianSplatData GetSplatData()
        {
            return _splatData;
        }

        [ContextMenu("Force Reload Splat")]
        public void ForceReload()
        {
            _needsReload = true;
            _loadedData = null;
            ReleaseBuffersOnly();

            if (_splatData != null)
            {
                LoadToGPU();
            }
        }

        private void LoadToGPU()
        {
            if (_splatData == null || Material == null)
                return;

            _isUsingGLES = true;
            _useMobilePath = true;

            if (MaterialGLES == null)
            {
                var glesShader = Shader.Find("GaussianSplatting/Gaussian Splat GLES");
                if (glesShader != null)
                {
                    MaterialGLES = new Material(glesShader);
                    MaterialGLES.name = "GaussianSplatGLES (Auto)";
                }
            }
            _activeMaterial = (MaterialGLES != null) ? MaterialGLES : Material;

            bool pathChanged = _useMobilePath != _lastUseMobilePath;
            bool needsReload = _needsReload || _orderBuffer == null || _loadedData != _splatData || _count == 0 || pathChanged;
            if (!needsReload)
                return;

            _needsReload = false;
            _lastUseMobilePath = _useMobilePath;
            ReleaseBuffersOnly();
            _loadedData = _splatData;

            int count = _splatData.Count;
            Vector3[] centers = _splatData.Centers;
            Vector4[] rotations = _splatData.Rotations;
            Vector3[] scales = _splatData.Scales;
            Vector4[] colors = _splatData.Colors;
            _shBands = _splatData.ShBands;
            _shCoeffsPerSplat = _splatData.ShCoeffsPerSplat;
            Vector3[] shCoeffs = _splatData.ShCoeffs;

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

            _orderBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, _count, sizeof(uint));
            _orderBuffer.SetData(_orderCpu);

            for (int i = 0; i < scales.Length; i++)
            {
                scales[i] = scales[i] * ScaleMultiplier;
            }

            if (_useMobilePath)
            {
                Vector4[] posCovA = new Vector4[_count];
                Vector4[] covB = new Vector4[_count];
                Vector4[] covCColor = new Vector4[_count];

                for (int i = 0; i < _count; i++)
                {
                    ComputeCovariance3D(rotations[i], scales[i], out Vector3 covA, out Vector3 covBVec);

                    posCovA[i] = new Vector4(centers[i].x, centers[i].y, centers[i].z, covA.x);
                    covB[i] = new Vector4(covA.y, covA.z, covBVec.x, covBVec.y);

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

                _activeMaterial.SetBuffer("_SplatOrder", _orderBuffer);
                _activeMaterial.SetBuffer("_SplatPosCovA", _glesPosScale);
                _activeMaterial.SetBuffer("_SplatCovB", _glesRotation);
                _activeMaterial.SetBuffer("_SplatCovCColor", _glesColor);
            }
            else
            {
                _centersBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 3);
                _rotationsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);
                _scalesBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 3);
                _colorsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);

                _centersBuffer.SetData(centers);
                _rotationsBuffer.SetData(rotations);
                _scalesBuffer.SetData(scales);
                _colorsBuffer.SetData(colors);

                if (_shBands > 0 && _shCoeffsPerSplat > 0 && shCoeffs != null && shCoeffs.Length == _count * _shCoeffsPerSplat)
                {
                    _shCoeffsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, shCoeffs.Length, sizeof(float) * 3);
                    _shCoeffsBuffer.SetData(shCoeffs);
                    _activeMaterial.SetBuffer("_SHCoeffs", _shCoeffsBuffer);
                }

                _activeMaterial.SetBuffer("_SplatOrder", _orderBuffer);
                _activeMaterial.SetBuffer("_Centers", _centersBuffer);
                _activeMaterial.SetBuffer("_Rotations", _rotationsBuffer);
                _activeMaterial.SetBuffer("_Scales", _scalesBuffer);
                _activeMaterial.SetBuffer("_Colors", _colorsBuffer);
            }

            _lastCamPos = new Vector3(float.NaN, 0, 0);
            _lastCamDir = new Vector3(float.NaN, 0, 0);

            _localBounds = ComputeLocalBounds(_centersCpu);
            _worldBounds = TransformBounds(_localBounds, transform.localToWorldMatrix);

            if (GLESSortAlgorithm == SortAlgorithm.None)
            {
                // No sorter
            }
            else if (GLESSortAlgorithm == SortAlgorithm.Bitonic)
            {
                var sortShaderBitonic = Resources.Load<ComputeShader>("GaussianSplatBitonicSort");
                if (sortShaderBitonic != null)
                {
                    _gpuSorterBitonic = new GaussianSplatBitonicSorter(sortShaderBitonic, _count);
                }
                else
                {
                    GLESSortAlgorithm = SortAlgorithm.Radix;
                }
            }

            if (GLESSortAlgorithm == SortAlgorithm.Radix)
            {
                var sortShaderGLES = Resources.Load<ComputeShader>("GaussianSplatSortGLES");
                if (sortShaderGLES != null)
                {
                    _gpuSorterGLES = new GaussianSplatGPUSorterGLES(sortShaderGLES, _count);
                }
            }

            if (!_useMobilePath && SystemInfo.supportsComputeShaders)
            {
                try
                {
                    bool isActuallyOnMobile = Application.platform == RuntimePlatform.Android;
                    bool useMobile = isActuallyOnMobile || _forceMobileGPUSorting;

                    _gpuSorter = GaussianSplatGPUSorter.Create(_count, useMobile);
                }
                catch (System.Exception)
                {
                    _gpuSorter = null;
                }
            }

            _frameCounter = 0;
        }

        private void BeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_splatData == null) return;

            if (UseRenderFeature && GraphicsSettings.currentRenderPipeline != null)
            {
                return;
            }

            if ((_needsReload || _orderBuffer == null) && _splatData != null && Material != null)
                LoadToGPU();

            if (!enabled || _activeMaterial == null || _orderBuffer == null || _count == 0)
                return;

            if (camera.cameraType != CameraType.SceneView)
            {
                var cam = TargetCamera != null ? TargetCamera : Camera.main;
                if (cam != null && camera != cam)
                    return;
            }

            RenderForCamera(camera, (CommandBuffer)null);
        }

        private void OnRenderObject()
        {
            if (GraphicsSettings.currentRenderPipeline != null) return;
            if (!enabled || _activeMaterial == null || _orderBuffer == null || _count == 0) return;

            var camera = Camera.current;
            if (camera == null) return;

            var cam = TargetCamera != null ? TargetCamera : Camera.main;
            if (cam != null && camera != cam) return;

            RenderForCamera(camera, (CommandBuffer)null);
        }

        public void RenderWithCommandBuffer(CommandBuffer cmd, Camera camera)
        {
            if ((_needsReload || _orderBuffer == null) && _splatData != null && Material != null)
                LoadToGPU();

            if (!enabled || _activeMaterial == null || _orderBuffer == null || _count == 0) return;

            if (camera.cameraType != CameraType.SceneView)
            {
                var cam = TargetCamera != null ? TargetCamera : Camera.main;
                if (cam != null && camera != cam) return;
            }

            RenderForCamera(camera, cmd);
        }

        public void RenderWithRasterCommandBuffer(RasterCommandBuffer cmd, Camera camera)
        {
            if ((_needsReload || _orderBuffer == null) && _splatData != null && Material != null)
                LoadToGPU();

            if (!enabled || _activeMaterial == null || _orderBuffer == null || _count == 0) return;

            if (camera.cameraType != CameraType.SceneView)
            {
                var cam = TargetCamera != null ? TargetCamera : Camera.main;
                if (cam != null && camera != cam) return;
            }

            RenderForCamera(camera, cmd);
        }

        private void RenderForCamera(Camera camera, CommandBuffer cmd)
        {
            if (!_firstRenderLogged)
            {
                _firstRenderLogged = true;
                Debug.Log($"[GS-PERF] First render call! Camera={camera.name}, visible={_visibleCount}/{_count}, mobile={_useMobilePath}");
            }

            float currentTime = Time.realtimeSinceStartup;
            float frameTime = (currentTime - _lastFrameTime) * 1000f;
            _lastFrameTime = currentTime;
            _avgFrameTimeMs = Mathf.Lerp(_avgFrameTimeMs, frameTime, 0.1f);

            _frameCounter++;
            bool shouldSort = (_frameCounter % SortEveryNFrames) == 0;

            if (shouldSort)
            {
                _sortStopwatch.Restart();

                var camPosOS = transform.InverseTransformPoint(camera.transform.position);
                var camDirOS = transform.InverseTransformDirection(camera.transform.forward).normalized;

                if (GLESSortAlgorithm == SortAlgorithm.None && _useMobilePath)
                {
                    _visibleCount = _count;
                }
                else if (_gpuSorterBitonic != null)
                {
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;

                    if (EnableFrustumCulling)
                    {
                        Matrix4x4 viewProjMatrix = camera.projectionMatrix * sortViewMatrix * sortModelMatrix;
                        if (_gpuSorterBitonic.Sort(_glesPosScale, _orderBuffer, modelViewMatrix, viewProjMatrix, camPosOS, camDirOS, FrustumCullMargin, out int visible))
                        {
                            _visibleCount = visible;
                        }
                    }
                    else
                    {
                        _gpuSorterBitonic.Sort(_glesPosScale, _orderBuffer, modelViewMatrix, camPosOS, camDirOS);
                        _visibleCount = _count;
                    }
                }
                else if (_gpuSorterGLES != null)
                {
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;

                    if (EnableFrustumCulling)
                    {
                        Matrix4x4 viewProjMatrix = camera.projectionMatrix * sortViewMatrix * sortModelMatrix;
                        if (_gpuSorterGLES.Sort(_glesPosScale, _orderBuffer, modelViewMatrix, viewProjMatrix, camPosOS, camDirOS, FrustumCullMargin, out int visible))
                        {
                            _visibleCount = visible;
                        }
                    }
                    else
                    {
                        _gpuSorterGLES.Sort(_glesPosScale, _orderBuffer, modelViewMatrix, camPosOS, camDirOS);
                        _visibleCount = _count;
                    }
                }
                else if (_gpuSorter != null && !_useMobilePath)
                {
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;
                    _gpuSorter.Sort(_centersBuffer, _orderBuffer, modelViewMatrix, camPosOS, camDirOS);
                    _visibleCount = _count;
                }
                else if (!_useMobilePath && _forceCPUSorting)
                {
                    const float eps = 0.001f;
                    if (Mathf.Abs(camPosOS.x - _lastCamPos.x) > eps ||
                        Mathf.Abs(camPosOS.y - _lastCamPos.y) > eps ||
                        Mathf.Abs(camPosOS.z - _lastCamPos.z) > eps ||
                        Mathf.Abs(camDirOS.x - _lastCamDir.x) > eps ||
                        Mathf.Abs(camDirOS.y - _lastCamDir.y) > eps ||
                        Mathf.Abs(camDirOS.z - _lastCamDir.z) > eps)
                    {
                        _lastCamPos = camPosOS;
                        _lastCamDir = camDirOS;
                        SortBackToFront(camPosOS, camDirOS);
                        _orderBuffer.SetData(_orderCpu);
                    }
                }

                _sortStopwatch.Stop();
                _lastSortTimeMs = (float)_sortStopwatch.Elapsed.TotalMilliseconds;
                _avgSortTimeMs = Mathf.Lerp(_avgSortTimeMs, _lastSortTimeMs, 0.1f);
            }

            if (LogPerformanceEveryNFrames > 0)
            {
                _perfLogCounter++;
                if (_perfLogCounter >= LogPerformanceEveryNFrames)
                {
                    _perfLogCounter = 0;
                    float fps = _avgFrameTimeMs > 0 ? 1000f / _avgFrameTimeMs : 0;
                    float cullPercent = _count > 0 ? (1f - (float)_visibleCount / _count) * 100f : 0;
                    Debug.Log($"[GS-PERF] FPS: {fps:F1} | Frame: {_avgFrameTimeMs:F1}ms | Sort: {_avgSortTimeMs:F2}ms | Visible: {_visibleCount}/{_count} ({cullPercent:F0}% culled)");
                }
            }

            float w = camera.pixelWidth;
            float h = camera.pixelHeight;
            var viewportSize = new Vector4(w, h, 1f / Mathf.Max(1f, w), 1f / Mathf.Max(1f, h));

            _activeMaterial.SetBuffer("_SplatOrder", _orderBuffer);
            if (_useMobilePath)
            {
                _activeMaterial.SetBuffer("_SplatPosCovA", _glesPosScale);
                _activeMaterial.SetBuffer("_SplatCovB", _glesRotation);
                _activeMaterial.SetBuffer("_SplatCovCColor", _glesColor);
            }
            else
            {
                _activeMaterial.SetBuffer("_Centers", _centersBuffer);
                _activeMaterial.SetBuffer("_Rotations", _rotationsBuffer);
                _activeMaterial.SetBuffer("_Scales", _scalesBuffer);
                _activeMaterial.SetBuffer("_Colors", _colorsBuffer);
                if (_shCoeffsBuffer != null)
                    _activeMaterial.SetBuffer("_SHCoeffs", _shCoeffsBuffer);
            }

            if (cmd != null)
            {
                _mpb.Clear();
                _mpb.SetVector("_ViewportSize", viewportSize);
                _mpb.SetFloat("_IsOrtho", camera.orthographic ? 1f : 0f);
                _mpb.SetInt("_NumSplats", _visibleCount);
                _mpb.SetInt("_SHBands", _useMobilePath ? 0 : _shBands);
                _mpb.SetInt("_SHCoeffsPerSplat", _useMobilePath ? 0 : _shCoeffsPerSplat);

                _mpb.SetBuffer("_SplatOrder", _orderBuffer);
                if (_useMobilePath)
                {
                    _mpb.SetBuffer("_SplatPosCovA", _glesPosScale);
                    _mpb.SetBuffer("_SplatCovB", _glesRotation);
                    _mpb.SetBuffer("_SplatCovCColor", _glesColor);
                }
                else
                {
                    _mpb.SetBuffer("_Centers", _centersBuffer);
                    _mpb.SetBuffer("_Rotations", _rotationsBuffer);
                    _mpb.SetBuffer("_Scales", _scalesBuffer);
                    _mpb.SetBuffer("_Colors", _colorsBuffer);
                    if (_shCoeffsBuffer != null) _mpb.SetBuffer("_SHCoeffs", _shCoeffsBuffer);
                }

                _mpb.SetMatrix("_SplatObjectToWorld", transform.localToWorldMatrix);
                _mpb.SetMatrix("_SplatWorldToObject", transform.worldToLocalMatrix);
            }
            else
            {
                _activeMaterial.SetVector("_ViewportSize", viewportSize);
                _activeMaterial.SetFloat("_IsOrtho", camera.orthographic ? 1f : 0f);
                _activeMaterial.SetInt("_NumSplats", _visibleCount);
                _activeMaterial.SetInt("_SHBands", _useMobilePath ? 0 : _shBands);
                _activeMaterial.SetInt("_SHCoeffsPerSplat", _useMobilePath ? 0 : _shCoeffsPerSplat);

                _activeMaterial.SetMatrix("_SplatObjectToWorld", transform.localToWorldMatrix);
                _activeMaterial.SetMatrix("_SplatWorldToObject", transform.worldToLocalMatrix);
            }

            var bounds = _localBounds.size.sqrMagnitude > 0
                ? TransformBounds(_localBounds, transform.localToWorldMatrix)
                : new Bounds(transform.position, Vector3.one * 100000f);

            _mpb.SetFloat("_CamProjM00", camera.projectionMatrix[0, 0]);
            _mpb.SetFloat("_CamProjM11", camera.projectionMatrix[1, 1]);

            if (cmd != null)
            {
                cmd.DrawProcedural(Matrix4x4.identity, _activeMaterial, 0, MeshTopology.Triangles, _visibleCount * 6, 1, _mpb);
            }
            else if (GraphicsSettings.currentRenderPipeline == null)
            {
                _activeMaterial.SetPass(0);
                Graphics.DrawProceduralNow(MeshTopology.Triangles, _visibleCount * 6, 1);
            }
            else
            {
                Graphics.DrawProcedural(_activeMaterial, bounds, MeshTopology.Triangles, _visibleCount * 6, 1, camera);
            }
        }

        private void RenderForCamera(Camera camera, RasterCommandBuffer cmd)
        {
            if (!_firstRenderLogged)
            {
                _firstRenderLogged = true;
                Debug.Log($"[GS-PERF] First render call (RasterCB)! Camera={camera.name}, UseRenderFeature={UseRenderFeature}");
            }

            float currentTime = Time.realtimeSinceStartup;
            float frameTime = (currentTime - _lastFrameTime) * 1000f;
            _lastFrameTime = currentTime;
            _avgFrameTimeMs = Mathf.Lerp(_avgFrameTimeMs, frameTime, 0.1f);

            _frameCounter++;
            bool shouldSort = (_frameCounter % SortEveryNFrames) == 0;

            if (shouldSort)
            {
                var camPosOS = transform.InverseTransformPoint(camera.transform.position);
                var camDirOS = transform.InverseTransformDirection(camera.transform.forward).normalized;

                if (GLESSortAlgorithm == SortAlgorithm.None && _useMobilePath)
                {
                    _visibleCount = _count;
                }
                else if (_gpuSorterBitonic != null)
                {
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;

                    if (EnableFrustumCulling)
                    {
                        Matrix4x4 viewProjMatrix = camera.projectionMatrix * sortViewMatrix * sortModelMatrix;
                        if (_gpuSorterBitonic.Sort(_glesPosScale, _orderBuffer, modelViewMatrix, viewProjMatrix, camPosOS, camDirOS, FrustumCullMargin, out int visible))
                        {
                            _visibleCount = visible;
                        }
                    }
                    else
                    {
                        _gpuSorterBitonic.Sort(_glesPosScale, _orderBuffer, modelViewMatrix, camPosOS, camDirOS);
                        _visibleCount = _count;
                    }
                }
                else if (_gpuSorterGLES != null)
                {
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;

                    if (EnableFrustumCulling)
                    {
                        Matrix4x4 viewProjMatrix = camera.projectionMatrix * sortViewMatrix * sortModelMatrix;
                        if (_gpuSorterGLES.Sort(_glesPosScale, _orderBuffer, modelViewMatrix, viewProjMatrix, camPosOS, camDirOS, FrustumCullMargin, out int visible))
                        {
                            _visibleCount = visible;
                        }
                    }
                    else
                    {
                        _gpuSorterGLES.Sort(_glesPosScale, _orderBuffer, modelViewMatrix, camPosOS, camDirOS);
                        _visibleCount = _count;
                    }
                }
            }

            float w = camera.pixelWidth;
            float h = camera.pixelHeight;
            var viewportSize = new Vector4(w, h, 1f / Mathf.Max(1f, w), 1f / Mathf.Max(1f, h));

            _activeMaterial.SetBuffer("_SplatOrder", _orderBuffer);
            if (_useMobilePath)
            {
                _activeMaterial.SetBuffer("_SplatPosCovA", _glesPosScale);
                _activeMaterial.SetBuffer("_SplatCovB", _glesRotation);
                _activeMaterial.SetBuffer("_SplatCovCColor", _glesColor);
            }
            else
            {
                _activeMaterial.SetBuffer("_Centers", _centersBuffer);
                _activeMaterial.SetBuffer("_Rotations", _rotationsBuffer);
                _activeMaterial.SetBuffer("_Scales", _scalesBuffer);
                _activeMaterial.SetBuffer("_Colors", _colorsBuffer);
                if (_shCoeffsBuffer != null)
                    _activeMaterial.SetBuffer("_SHCoeffs", _shCoeffsBuffer);
            }

            _mpb.Clear();
            _mpb.SetVector("_ViewportSize", viewportSize);
            _mpb.SetFloat("_IsOrtho", camera.orthographic ? 1f : 0f);
            _mpb.SetInt("_NumSplats", _visibleCount);
            _mpb.SetInt("_SHBands", _useMobilePath ? 0 : _shBands);
            _mpb.SetInt("_SHCoeffsPerSplat", _useMobilePath ? 0 : _shCoeffsPerSplat);

            _mpb.SetBuffer("_SplatOrder", _orderBuffer);
            if (_useMobilePath)
            {
                _mpb.SetBuffer("_SplatPosCovA", _glesPosScale);
                _mpb.SetBuffer("_SplatCovB", _glesRotation);
                _mpb.SetBuffer("_SplatCovCColor", _glesColor);
            }

            _mpb.SetMatrix("_SplatObjectToWorld", transform.localToWorldMatrix);
            _mpb.SetMatrix("_SplatWorldToObject", transform.worldToLocalMatrix);

            var viewMatrix = camera.worldToCameraMatrix;
            var projMatrix = camera.projectionMatrix;
            cmd.SetViewProjectionMatrices(viewMatrix, projMatrix);

            _mpb.SetFloat("_CamProjM00", projMatrix[0, 0]);
            _mpb.SetFloat("_CamProjM11", projMatrix[1, 1]);

            cmd.DrawProcedural(Matrix4x4.identity, _activeMaterial, 0, MeshTopology.Triangles, _visibleCount * 6, 1, _mpb);

            if (LogPerformanceEveryNFrames > 0)
            {
                _perfLogCounter++;
                if (_perfLogCounter >= LogPerformanceEveryNFrames)
                {
                    _perfLogCounter = 0;
                    float fps = _avgFrameTimeMs > 0 ? 1000f / _avgFrameTimeMs : 0;
                    float cullPercent = _count > 0 ? (1f - (float)_visibleCount / _count) * 100f : 0;
                    Debug.Log($"[GS-PERF] FPS: {fps:F1} | Frame: {_avgFrameTimeMs:F1}ms | Visible: {_visibleCount}/{_count} ({cullPercent:F0}% culled)");
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
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

        private void SortBackToFront(Vector3 camPosOS, Vector3 camDirOS)
        {
            int n = _count;
            if (_distances.Length != n) _distances = new uint[n];

            float minDist = float.PositiveInfinity;
            float maxDist = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var c = _centersCpu[i];
                float d = c.x * camDirOS.x + c.y * camDirOS.y + c.z * camDirOS.z;
                if (d < minDist) minDist = d;
                if (d > maxDist) maxDist = d;
            }

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

            for (int i = 1; i < bucketCount; i++)
                _countBuffer[i] += _countBuffer[i - 1];

            for (int i = 0; i < n; i++)
            {
                uint key = _distances[i];
                int destIndex = n - 1 - (int)(--_countBuffer[key]);
                _orderCpu[destIndex] = (uint)i;
            }

            float cameraDist = camPosOS.x * camDirOS.x + camPosOS.y * camDirOS.y + camPosOS.z * camDirOS.z;

            float DistAtOrderIndex(int orderIndex)
            {
                var c = _centersCpu[_orderCpu[orderIndex]];
                return c.x * camDirOS.x + c.y * camDirOS.y + c.z * camDirOS.z - cameraDist;
            }

            int lo = 0, hi = n - 1, lastNonNeg = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (DistAtOrderIndex(mid) >= 0f)
                {
                    lastNonNeg = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            _visibleCount = Mathf.Clamp(lastNonNeg + 1, 0, n);
        }

        public void OnBeforeSerialize()
        {
        }

        public void OnAfterDeserialize()
        {
            _needsReload = true;
        }
    }
}
