using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    [ExecuteAlways]
    public sealed class GaussianSplatRenderer : MonoBehaviour, ISerializationCallbackReceiver
    {
        [Header("Rendering")]
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
        
        [Header("Debug")]
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
        private MaterialPropertyBlock _mpb;
        private GaussianSplatData _loadedData;
        private Material _materialInstance;
        
        [System.NonSerialized]
        private bool _needsReload = false; // Set to true after deserialization to force buffer reload
        
        private int _frameCounter = 0; // tracks frames for sort frequency

        public bool IsLoaded => _count > 0 && _orderBuffer != null;
        public int SplatCount => _count;
        // public string Name => 

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
            // Release GPU buffers but keep CPU data
            _gpuSorter?.Dispose(); _gpuSorter = null;
            _orderBuffer?.Release(); _orderBuffer = null;
            _centersBuffer?.Release(); _centersBuffer = null;
            _rotationsBuffer?.Release(); _rotationsBuffer = null;
            _scalesBuffer?.Release(); _scalesBuffer = null;
            _colorsBuffer?.Release(); _colorsBuffer = null;
            _shCoeffsBuffer?.Release(); _shCoeffsBuffer = null;
            
            if (_materialInstance != null)
            {
                if (Application.isPlaying)
                    Destroy(_materialInstance);
                else
                    DestroyImmediate(_materialInstance);
                _materialInstance = null;
            }
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
            _orderBuffer?.Release(); _orderBuffer = null;
            _centersBuffer?.Release(); _centersBuffer = null;
            _rotationsBuffer?.Release(); _rotationsBuffer = null;
            _scalesBuffer?.Release(); _scalesBuffer = null;
            _colorsBuffer?.Release(); _colorsBuffer = null;
            _shCoeffsBuffer?.Release(); _shCoeffsBuffer = null;
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
            
            if (_materialInstance != null)
            {
                if (Application.isPlaying)
                    Destroy(_materialInstance);
                else
                    DestroyImmediate(_materialInstance);
                _materialInstance = null;
            }
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
            
            bool needsReload = _needsReload || _orderBuffer == null || _loadedData != _splatData || _count == 0;
            if (!needsReload)
                return;

            _needsReload = false;
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
            _centersBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 3);
            _rotationsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);
            _scalesBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 3);
            _colorsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);

            _orderBuffer.SetData(_orderCpu);
            _centersBuffer.SetData(centers);
            _rotationsBuffer.SetData(rotations);
            
            // Apply scale multiplier and minimum scale
            for (int i = 0; i < scales.Length; i++)
            {
                scales[i] = scales[i] * ScaleMultiplier;
            }
            _scalesBuffer.SetData(scales);
            _colorsBuffer.SetData(colors);

            if (_shBands > 0 && _shCoeffsPerSplat > 0 && shCoeffs != null && shCoeffs.Length == _count * _shCoeffsPerSplat)
            {
                _shCoeffsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, shCoeffs.Length, sizeof(float) * 3);
                _shCoeffsBuffer.SetData(shCoeffs);
            }
            else
            {
                _shCoeffsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
                _shCoeffsBuffer.SetData(new Vector3[] { Vector3.zero });
            }

            _materialInstance = new Material(Material);
            _materialInstance.name = Material.name + " (Instance)";
            _materialInstance.SetBuffer("_SplatOrder", _orderBuffer);
            _materialInstance.SetBuffer("_Centers", _centersBuffer);
            _materialInstance.SetBuffer("_Rotations", _rotationsBuffer);
            _materialInstance.SetBuffer("_Scales", _scalesBuffer);
            _materialInstance.SetBuffer("_Colors", _colorsBuffer);
            _materialInstance.SetBuffer("_SHCoeffs", _shCoeffsBuffer);

            _lastCamPos = new Vector3(float.NaN, 0, 0);
            _lastCamDir = new Vector3(float.NaN, 0, 0);

            _localBounds = ComputeLocalBounds(_centersCpu);
            _worldBounds = TransformBounds(_localBounds, transform.localToWorldMatrix);

            // Create GPU sorter - uses platform-appropriate shader (mobile or desktop)
            if (SystemInfo.supportsComputeShaders)
            {
                try
                {
                    // Use mobile shader on actual Android device, or if forced in editor
                    bool isActuallyOnMobile = Application.platform == RuntimePlatform.Android;
                    bool useMobile = isActuallyOnMobile || _forceMobileGPUSorting;
                    
                    _gpuSorter = GaussianSplatGPUSorter.Create(_count, useMobile);
                    if (_gpuSorter != null && useMobile)
                        Debug.Log("[GaussianSplatRenderer] Using mobile GPU sorting (shared memory atomics).");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[GaussianSplatRenderer] GPU sorter creation failed: {e.Message}. Falling back to CPU sorting.");
                    _gpuSorter = null;
                }
            }
            
            // Reset frame counter to ensure first frame sorts
            _frameCounter = 0;
        }

        private void BeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_splatData == null) return;
            
            // Skip if using render feature (it will call RenderWithCommandBuffer instead)
            if (UseRenderFeature && GraphicsSettings.currentRenderPipeline != null)
            {
                return;
            }
            
            // Lazy reload if data was released (e.g., after scene save in editor)
            if ((_needsReload || _orderBuffer == null) && _splatData != null && Material != null)
                LoadToGPU();
            
            if (!enabled || _materialInstance == null || _orderBuffer == null || _count == 0)
                return;
            
            // Scene view handling - always draw
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
            // Built-in RP fallback
            if (GraphicsSettings.currentRenderPipeline != null) return;
            if (!enabled || _materialInstance == null || _orderBuffer == null || _count == 0) return;

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
            if ((_needsReload || _orderBuffer == null) && _splatData != null && Material != null)
                LoadToGPU();
            
            if (!enabled || _materialInstance == null || _orderBuffer == null || _count == 0) return;
            
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
            if ((_needsReload || _orderBuffer == null) && _splatData != null && Material != null)
                LoadToGPU();
            
            if (!enabled || _materialInstance == null || _orderBuffer == null || _count == 0) return;
            
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
            // Sort based on frame frequency
            _frameCounter++;
            bool shouldSort = (_frameCounter % SortEveryNFrames) == 0;
            
            if (shouldSort)
            {
                // transform camera into object space (matches gsplat-instance.js sort path)
                var camPosOS = transform.InverseTransformPoint(camera.transform.position);
                var camDirOS = transform.InverseTransformDirection(camera.transform.forward).normalized;

                if (_gpuSorter != null)
                {
                    // GPU sorting (much faster)
                    // Calculate model-view matrix for sorting
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;
                    _gpuSorter.Sort(_centersBuffer, _orderBuffer, modelViewMatrix, camPosOS, camDirOS);
                }
                else
                {
                    // CPU sorting fallback (slow but works without compute shader)
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
            }

            float w = camera.pixelWidth;
            float h = camera.pixelHeight;
            var viewportSize = new Vector4(w, h, 1f / Mathf.Max(1f, w), 1f / Mathf.Max(1f, h));

            _materialInstance.SetBuffer("_SplatOrder", _orderBuffer);
            _materialInstance.SetBuffer("_Centers", _centersBuffer);
            _materialInstance.SetBuffer("_Rotations", _rotationsBuffer);
            _materialInstance.SetBuffer("_Scales", _scalesBuffer);
            _materialInstance.SetBuffer("_Colors", _colorsBuffer);
            _materialInstance.SetBuffer("_SHCoeffs", _shCoeffsBuffer);

            if (cmd != null)
            {
                // CommandBuffer path (URP Render Feature / RenderGraph):
                // Use MaterialPropertyBlock so per-object/per-camera data is captured per draw.
                _mpb.Clear();
                _mpb.SetVector("_ViewportSize", viewportSize);
                _mpb.SetFloat("_IsOrtho", camera.orthographic ? 1f : 0f);
                _mpb.SetInt("_NumSplats", _visibleCount);
                _mpb.SetInt("_SHBands", _shBands);
                _mpb.SetInt("_SHCoeffsPerSplat", _shCoeffsPerSplat);

                // Custom model matrices (UNITY_MATRIX_M can't be written in URP constant buffers)
                _mpb.SetMatrix("_SplatObjectToWorld", transform.localToWorldMatrix);
                _mpb.SetMatrix("_SplatWorldToObject", transform.worldToLocalMatrix);
            }
            else
            {
                // Immediate path (beginCameraRendering / built-in fallback)
                _materialInstance.SetVector("_ViewportSize", viewportSize);
                _materialInstance.SetFloat("_IsOrtho", camera.orthographic ? 1f : 0f);
                _materialInstance.SetInt("_NumSplats", _visibleCount);
                _materialInstance.SetInt("_SHBands", _shBands);
                _materialInstance.SetInt("_SHCoeffsPerSplat", _shCoeffsPerSplat);

                // Custom model matrices (UNITY_MATRIX_M can't be written in URP constant buffers)
                _materialInstance.SetMatrix("_SplatObjectToWorld", transform.localToWorldMatrix);
                _materialInstance.SetMatrix("_SplatWorldToObject", transform.worldToLocalMatrix);
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
                cmd.DrawProcedural(Matrix4x4.identity, _materialInstance, 0, MeshTopology.Triangles, _visibleCount * 6, 1, _mpb);
            }
            else if (GraphicsSettings.currentRenderPipeline == null)
            {
                // Built-in RP: DrawProceduralNow inside OnRenderObject is the most reliable path.
                _materialInstance.SetPass(0);
                Graphics.DrawProceduralNow(MeshTopology.Triangles, _visibleCount * 6, 1);
            }
            else
            {
                // SRP fallback (when not using render feature): bounds-based draw call
                Graphics.DrawProcedural(_materialInstance, bounds, MeshTopology.Triangles, _visibleCount * 6, 1, camera);
            }
        }

        private void RenderForCamera(Camera camera, RasterCommandBuffer cmd)
        {
            // Sort based on frame frequency (share counter with other render path)
            bool shouldSort = (_frameCounter % SortEveryNFrames) == 0;
            
            if (shouldSort)
            {
                // transform camera into object space (matches gsplat-instance.js sort path)
                var camPosOS = transform.InverseTransformPoint(camera.transform.position);
                var camDirOS = transform.InverseTransformDirection(camera.transform.forward).normalized;

                if (_gpuSorter != null && !_forceCPUSorting)
                {
                    // GPU sorting (much faster)
                    // Calculate model-view matrix for sorting
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;
                    _gpuSorter.Sort(_centersBuffer, _orderBuffer, modelViewMatrix, camPosOS, camDirOS);
                }
                else
                {
                    // CPU sorting fallback (slow but works without compute shader)
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
            }

            float w = camera.pixelWidth;
            float h = camera.pixelHeight;
            var viewportSize = new Vector4(w, h, 1f / Mathf.Max(1f, w), 1f / Mathf.Max(1f, h));

            _materialInstance.SetBuffer("_SplatOrder", _orderBuffer);
            _materialInstance.SetBuffer("_Centers", _centersBuffer);
            _materialInstance.SetBuffer("_Rotations", _rotationsBuffer);
            _materialInstance.SetBuffer("_Scales", _scalesBuffer);
            _materialInstance.SetBuffer("_Colors", _colorsBuffer);
            _materialInstance.SetBuffer("_SHCoeffs", _shCoeffsBuffer);

            _mpb.Clear();
            _mpb.SetVector("_ViewportSize", viewportSize);
            _mpb.SetFloat("_IsOrtho", camera.orthographic ? 1f : 0f);
            _mpb.SetInt("_NumSplats", _visibleCount);
            _mpb.SetInt("_SHBands", _shBands);
            _mpb.SetInt("_SHCoeffsPerSplat", _shCoeffsPerSplat);
            _mpb.SetMatrix("_SplatObjectToWorld", transform.localToWorldMatrix);
            _mpb.SetMatrix("_SplatWorldToObject", transform.worldToLocalMatrix);

            // Set view/projection matrices - use raw camera projection (no Y-flip)
            var viewMatrix = camera.worldToCameraMatrix;
            var projMatrix = camera.projectionMatrix;
            cmd.SetViewProjectionMatrices(viewMatrix, projMatrix);
            
            // Pass the projection matrix values for focal length calculation
            _mpb.SetFloat("_CamProjM00", projMatrix[0, 0]);
            _mpb.SetFloat("_CamProjM11", projMatrix[1, 1]);

            // Draw (URP/RenderGraph) - buffers are set on material instance, other properties in MPB
            cmd.DrawProcedural(Matrix4x4.identity, _materialInstance, 0, MeshTopology.Triangles, _visibleCount * 6, 1, _mpb);
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
