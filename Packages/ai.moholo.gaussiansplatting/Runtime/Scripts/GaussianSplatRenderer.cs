using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace GaussianSplatting
{
    [ExecuteAlways]
    public sealed class GaussianSplatRenderer : MonoBehaviour, ISerializationCallbackReceiver
    {
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

        private GraphicsBuffer _order;
        private GraphicsBuffer _centers;
        private GraphicsBuffer _rotations;
        private GraphicsBuffer _scales;
        private GraphicsBuffer _colors;
        private GraphicsBuffer _shCoeffs;
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
        private GaussianSplatAsset _loadedAsset; // Track which asset is currently loaded
        
        [System.NonSerialized]
        private bool _needsReload = false; // Set to true after deserialization to force buffer reload
        
        private int _frameCounter = 0; // Track frames for sort frequency

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
            _order?.Release(); _order = null;
            _centers?.Release(); _centers = null;
            _rotations?.Release(); _rotations = null;
            _scales?.Release(); _scales = null;
            _colors?.Release(); _colors = null;
            _shCoeffs?.Release(); _shCoeffs = null;
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
            _order?.Release(); _order = null;
            _centers?.Release(); _centers = null;
            _rotations?.Release(); _rotations = null;
            _scales?.Release(); _scales = null;
            _colors?.Release(); _colors = null;
            _shCoeffs?.Release(); _shCoeffs = null;
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
            _centers = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 3);
            _rotations = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);
            _scales = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 3);
            _colors = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 4);

            _order.SetData(_orderCpu);
            _centers.SetData(centers);
            _rotations.SetData(rotations);
            
            // Apply scale multiplier and minimum scale
            for (int i = 0; i < scales.Length; i++)
            {
                scales[i] = scales[i] * ScaleMultiplier;
            }
            _scales.SetData(scales);
            _colors.SetData(colors);

            if (_shBands > 0 && _shCoeffsPerSplat > 0 && shCoeffs != null && shCoeffs.Length == _count * _shCoeffsPerSplat)
            {
                _shCoeffs = new GraphicsBuffer(GraphicsBuffer.Target.Structured, shCoeffs.Length, sizeof(float) * 3);
                _shCoeffs.SetData(shCoeffs);
                Material.SetBuffer("_SHCoeffs", _shCoeffs);
            }

            Material.SetBuffer("_SplatOrder", _order);
            Material.SetBuffer("_Centers", _centers);
            Material.SetBuffer("_Rotations", _rotations);
            Material.SetBuffer("_Scales", _scales);
            Material.SetBuffer("_Colors", _colors);

            _lastCamPos = new Vector3(float.NaN, 0, 0);
            _lastCamDir = new Vector3(float.NaN, 0, 0);

            _localBounds = ComputeLocalBounds(_centersCpu);
            _worldBounds = TransformBounds(_localBounds, transform.localToWorldMatrix);

            // Create GPU sorter - auto-load compute shader from Resources
            var sortShader = Resources.Load<ComputeShader>("GaussianSplatSort");
            if (sortShader != null)
            {
                _gpuSorter = new GaussianSplatGPUSorter(sortShader, _count);
            }
            else
            {
                Debug.LogWarning("[GaussianSplatRenderer] Could not load GaussianSplatSort compute shader from Resources. Falling back to CPU sorting.");
            }
            
            // Reset frame counter to ensure first frame sorts
            _frameCounter = 0;
            
            Debug.Log($"[GaussianSplatRenderer] TryLoad: completed! GPU buffers created for {_count} splats. Material={Material.name}, _order valid={_order != null}");
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
            if (Material == null)
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
            if (!enabled || Material == null || _order == null || _count == 0) return;

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
            
            if (!enabled || Material == null || _order == null || _count == 0) return;
            
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
            
            if (!enabled || Material == null || _order == null || _count == 0) return;
            
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
                    _gpuSorter.Sort(_centers, _order, modelViewMatrix, camPosOS, camDirOS);
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
                        _order.SetData(_orderCpu);
                    }
                }
            }

            float w = camera.pixelWidth;
            float h = camera.pixelHeight;
            var viewportSize = new Vector4(w, h, 1f / Mathf.Max(1f, w), 1f / Mathf.Max(1f, h));


            // Ensure buffers are bound (may be lost after scene save/reload)
            Material.SetBuffer("_SplatOrder", _order);
            Material.SetBuffer("_Centers", _centers);
            Material.SetBuffer("_Rotations", _rotations);
            Material.SetBuffer("_Scales", _scales);
            Material.SetBuffer("_Colors", _colors);
            if (_shCoeffs != null)
                Material.SetBuffer("_SHCoeffs", _shCoeffs);

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
                Material.SetVector("_ViewportSize", viewportSize);
                Material.SetFloat("_IsOrtho", camera.orthographic ? 1f : 0f);
                Material.SetInt("_NumSplats", _visibleCount);
                Material.SetInt("_SHBands", _shBands);
                Material.SetInt("_SHCoeffsPerSplat", _shCoeffsPerSplat);

                // Custom model matrices (UNITY_MATRIX_M can't be written in URP constant buffers)
                Material.SetMatrix("_SplatObjectToWorld", transform.localToWorldMatrix);
                Material.SetMatrix("_SplatWorldToObject", transform.worldToLocalMatrix);
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
                cmd.DrawProcedural(Matrix4x4.identity, Material, 0, MeshTopology.Triangles, _visibleCount * 6, 1, _mpb);
            }
            else if (GraphicsSettings.currentRenderPipeline == null)
            {
                // Built-in RP: DrawProceduralNow inside OnRenderObject is the most reliable path.
                Material.SetPass(0);
                Graphics.DrawProceduralNow(MeshTopology.Triangles, _visibleCount * 6, 1);
            }
            else
            {
                // SRP fallback (when not using render feature): bounds-based draw call
                Graphics.DrawProcedural(Material, bounds, MeshTopology.Triangles, _visibleCount * 6, 1, camera);
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

                if (_gpuSorter != null)
                {
                    // GPU sorting (much faster)
                    // Calculate model-view matrix for sorting
                    Matrix4x4 sortModelMatrix = transform.localToWorldMatrix;
                    Matrix4x4 sortViewMatrix = camera.worldToCameraMatrix;
                    Matrix4x4 modelViewMatrix = sortViewMatrix * sortModelMatrix;
                    _gpuSorter.Sort(_centers, _order, modelViewMatrix, camPosOS, camDirOS);
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
                        _order.SetData(_orderCpu);
                    }
                }
            }

            float w = camera.pixelWidth;
            float h = camera.pixelHeight;
            var viewportSize = new Vector4(w, h, 1f / Mathf.Max(1f, w), 1f / Mathf.Max(1f, h));


            // RasterCommandBuffer path: use MPB so per-object/per-camera data is captured per draw.
            // Note: Buffers must be set on Material (not MaterialPropertyBlock) as MPB doesn't support buffers
            Material.SetBuffer("_SplatOrder", _order);
            Material.SetBuffer("_Centers", _centers);
            Material.SetBuffer("_Rotations", _rotations);
            Material.SetBuffer("_Scales", _scales);
            Material.SetBuffer("_Colors", _colors);
            if (_shCoeffs != null)
                Material.SetBuffer("_SHCoeffs", _shCoeffs);
            
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

            // Draw (URP/RenderGraph) - buffers are set on Material, other properties in MPB
            cmd.DrawProcedural(Matrix4x4.identity, Material, 0, MeshTopology.Triangles, _visibleCount * 6, 1, _mpb);
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


