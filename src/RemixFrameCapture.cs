using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections.LowLevel.Unsafe;

namespace UnityRemix
{
    /// <summary>
    /// Captures frame state from Unity main thread for safe transfer to render thread
    /// </summary>
    public class RemixFrameCapture
    {
        private readonly ManualLogSource logger;
        private readonly RemixCameraHandler cameraHandler;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixMaterialManager materialManager;
        
        private readonly ConfigEntry<bool> configUseDistanceCulling;
        private readonly ConfigEntry<float> configMaxRenderDistance;
        private readonly ConfigEntry<bool> configUseVisibilityCulling;
        private readonly ConfigEntry<int> configRendererCacheDuration;
        private readonly ConfigEntry<int> configDebugLogInterval;
        private readonly ConfigEntry<bool> configCaptureStaticMeshes;
        private readonly ConfigEntry<bool> configCaptureSkinnedMeshes;
        
        // Renderer caching
        private List<MeshRenderer> cachedRenderers = new List<MeshRenderer>();
        private List<SkinnedMeshRenderer> cachedSkinnedRenderers = new List<SkinnedMeshRenderer>();
        private int rendererCacheFrame = -1;
        
        // Cached baked meshes for skinned renderers
        private Dictionary<int, Mesh> bakedMeshes = new Dictionary<int, Mesh>();
        private Dictionary<int, Matrix4x4> lastSkinnedTransforms = new Dictionary<int, Matrix4x4>();
        
        // Thread-safe renderer snapshots for UI
        private LayerSnapshot[] _layerSnapshots = Array.Empty<LayerSnapshot>();
        
        // User-disabled layers and individual renderers. Checked during capture.
        private readonly HashSet<int> _disabledLayers = new HashSet<int>();
        private readonly HashSet<int> _disabledRendererIds = new HashSet<int>();
        private readonly object _disabledLock = new object();
        
        /// <summary>
        /// Immutable snapshot of a Unity layer with its renderers.
        /// </summary>
        public struct LayerSnapshot
        {
            public int LayerIndex;
            public string LayerName;
            public int StaticCount;
            public int SkinnedCount;
            public bool UserDisabled;
        }
        
        /// <summary>
        /// Immutable snapshot of a single renderer.
        /// </summary>
        public struct RendererSnapshot
        {
            public int InstanceId;
            public string Name;
            public string Type; // "Static" or "Skinned"
            public int Layer;
        }
        
        // Full renderer list kept alongside layers for drill-down
        private RendererSnapshot[] _rendererSnapshots = Array.Empty<RendererSnapshot>();
        
        /// <summary>
        /// Current layer snapshots. Safe to read from any thread.
        /// </summary>
        public LayerSnapshot[] LayerSnapshots => Volatile.Read(ref _layerSnapshots);
        
        /// <summary>
        /// Current renderer snapshots. Safe to read from any thread.
        /// </summary>
        public RendererSnapshot[] RendererSnapshots => Volatile.Read(ref _rendererSnapshots);
        
        /// <summary>
        /// Set whether a layer is disabled by the user.
        /// </summary>
        public void SetLayerDisabled(int layerIndex, bool disabled)
        {
            lock (_disabledLock)
            {
                if (disabled)
                    _disabledLayers.Add(layerIndex);
                else
                    _disabledLayers.Remove(layerIndex);
            }
        }
        
        /// <summary>
        /// Check if a layer is user-disabled.
        /// </summary>
        public bool IsLayerDisabled(int layerIndex)
        {
            lock (_disabledLock)
            {
                return _disabledLayers.Contains(layerIndex);
            }
        }

        public void SetRendererDisabled(int instanceId, bool disabled)
        {
            lock (_disabledLock)
            {
                if (disabled)
                    _disabledRendererIds.Add(instanceId);
                else
                    _disabledRendererIds.Remove(instanceId);
            }
        }

        public bool IsRendererDisabled(int instanceId)
        {
            lock (_disabledLock)
            {
                return _disabledRendererIds.Contains(instanceId);
            }
        }

        /// <summary>
        /// Serializes disabled layers as a comma-separated string for config persistence.
        /// </summary>
        public string GetDisabledLayersString()
        {
            lock (_disabledLock)
            {
                if (_disabledLayers.Count == 0) return "";
                var sb = new System.Text.StringBuilder();
                bool first = true;
                foreach (int layer in _disabledLayers)
                {
                    if (!first) sb.Append(',');
                    sb.Append(layer);
                    first = false;
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Restores disabled layers from a comma-separated string.
        /// </summary>
        public void LoadDisabledLayersString(string csv)
        {
            lock (_disabledLock)
            {
                _disabledLayers.Clear();
                if (string.IsNullOrEmpty(csv)) return;
                foreach (var token in csv.Split(','))
                {
                    if (int.TryParse(token.Trim(), out int layer))
                        _disabledLayers.Add(layer);
                }
            }
        }
        
        // Mesh creation queue with materials
        private struct MeshToCreate
        {
            public Mesh mesh;
            public Material material;
        }
        private Queue<MeshToCreate> meshesToCreate = new Queue<MeshToCreate>();
        private HashSet<int> meshesInQueue = new HashSet<int>(); // Track which meshes are already queued
        
        private int skinnedCaptureCount = 0;
        private int skinnedRoundRobinIndex = 0; // rotates through cachedSkinnedRenderers each frame (BakeMesh fallback)
        
        // Persistent cache: last-baked data for every skinned mesh, so all are drawn every frame
        private Dictionary<int, SkinnedMeshData> persistentSkinnedData = new Dictionary<int, SkinnedMeshData>();
        
        // Per-mesh cached static data (UVs, triangles don't change with skinning)
        private struct CachedMeshTopology
        {
            public Vector2[] uvs;
            public int[] triangles;
            public int vertexCount;
            public int positionOffset;  // byte offset of Position in interleaved vertex buffer
            public int normalOffset;    // byte offset of Normal in interleaved vertex buffer
            public int stride;          // total bytes per vertex in GPU buffer
            public bool valid;
            public bool layoutValid;    // vertex buffer layout cached but triangles/UVs pending (mesh not readable)
        }
        private Dictionary<int, CachedMeshTopology> cachedTopology = new Dictionary<int, CachedMeshTopology>(); // keyed by sharedMesh instance ID
        
        // Cached GPU skinning data per sharedMesh (bind-pose vertices + bone weights)
        private Dictionary<int, CachedSkinningData> cachedSkinning = new Dictionary<int, CachedSkinningData>(); // keyed by sharedMesh instance ID
        
        // Pending GPU readback requests
        private struct PendingReadback
        {
            public int skinnedId;
            public int materialId;
            public int sharedMeshId;
            public Matrix4x4 localToWorld;
            public AsyncGPUReadbackRequest request;
        }
        private List<PendingReadback> pendingReadbacks = new List<PendingReadback>();
        
        // Track which renderers have had vertexBufferTarget configured
        private HashSet<int> configuredBufferTargets = new HashSet<int>();
        
        // Track logged skinned mesh materials to avoid spam
        private HashSet<string> loggedSkinnedMaterials = new HashSet<string>();
        
        // Frame state structures
        public struct CameraData
        {
            public Vector3 position;
            public Vector3 forward;
            public Vector3 up;
            public Vector3 right;
            public float fov;
            public float aspect;
            public float nearPlane;
            public float farPlane;
            public bool valid;
        }
        
        public struct MeshInstanceData
        {
            public int meshId;
            public Matrix4x4 localToWorld;
        }
        
        public struct SkinnedMeshData
        {
            public int meshId;
            public int materialId;  // Unity material instance ID
            public Vector3[] vertices;
            public Vector3[] normals;
            public Vector2[] uvs;
            public int[] triangles;
            public Matrix4x4 localToWorld;
            // GPU skinning: bone transforms per frame (null = software skinned / BakeMesh fallback)
            public Matrix4x4[] boneTransforms;
            // GPU skinning: bind-pose data + weights cached per sharedMesh (null = BakeMesh fallback)
            public CachedSkinningData skinningData;
        }

        /// <summary>
        /// One-time cached data per sharedMesh for GPU skinning: bind-pose geometry + bone weights.
        /// </summary>
        public class CachedSkinningData
        {
            public Vector3[] bindVertices;
            public Vector3[] bindNormals;
            public Vector2[] uvs;
            public int[] triangles;
            public float[] blendWeights;    // bonesPerVertex * vertexCount
            public uint[] blendIndices;     // bonesPerVertex * vertexCount
            public int bonesPerVertex;
            public int boneCount;
            public Matrix4x4[] bindPoses;
            public bool meshCreated;        // true after Remix mesh has been created on render thread
        }
        
        public class FrameState
        {
            public CameraData camera;
            public List<MeshInstanceData> instances = new List<MeshInstanceData>();
            public List<SkinnedMeshData> skinned = new List<SkinnedMeshData>();
            public int frameCount;
        }
        
        public RemixFrameCapture(
            ManualLogSource logger,
            RemixCameraHandler cameraHandler,
            RemixMeshConverter meshConverter,
            RemixMaterialManager materialManager,
            ConfigEntry<bool> useDistanceCulling,
            ConfigEntry<float> maxRenderDistance,
            ConfigEntry<bool> useVisibilityCulling,
            ConfigEntry<int> rendererCacheDuration,
            ConfigEntry<int> debugLogInterval,
            ConfigEntry<bool> captureStaticMeshes,
            ConfigEntry<bool> captureSkinnedMeshes)
        {
            this.logger = logger;
            this.cameraHandler = cameraHandler;
            this.meshConverter = meshConverter;
            this.materialManager = materialManager;
            this.configUseDistanceCulling = useDistanceCulling;
            this.configMaxRenderDistance = maxRenderDistance;
            this.configUseVisibilityCulling = useVisibilityCulling;
            this.configRendererCacheDuration = rendererCacheDuration;
            this.configDebugLogInterval = debugLogInterval;
            this.configCaptureStaticMeshes = captureStaticMeshes;
            this.configCaptureSkinnedMeshes = captureSkinnedMeshes;
        }
        
        /// <summary>
        /// Invalidate caches on scene change
        /// </summary>
        public void InvalidateCaches()
        {
            rendererCacheFrame = -1;
            cachedRenderers.Clear();
            cachedSkinnedRenderers.Clear();
            lastSkinnedTransforms.Clear();
            meshesToCreate.Clear();
            meshesInQueue.Clear();
            loggedSkinnedMaterials.Clear();
            skinnedRoundRobinIndex = 0;
            persistentSkinnedData.Clear();
            pendingReadbacks.Clear();
            configuredBufferTargets.Clear();
            cachedTopology.Clear();
            cachedSkinning.Clear();
            logger.LogInfo("Renderer caches invalidated");
        }
        
        /// <summary>
        /// Refresh renderer cache
        /// </summary>
        private void RefreshRendererCache(int frameCount)
        {
            cachedRenderers.Clear();
            cachedSkinnedRenderers.Clear();
            skinnedRoundRobinIndex = 0;
            // Don't clear configuredBufferTargets — the property persists on the component
            
            cachedRenderers.AddRange(UnityEngine.Object.FindObjectsOfType<MeshRenderer>());
            cachedSkinnedRenderers.AddRange(UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>());
            
            rendererCacheFrame = frameCount;
            logger.LogInfo($"Renderer cache refreshed: {cachedRenderers.Count} static, {cachedSkinnedRenderers.Count} skinned");
            
            RefreshRendererSnapshots();
        }
        
        /// <summary>
        /// Build thread-safe renderer snapshots from the current cache. Must be called from main thread.
        /// </summary>
        private void RefreshRendererSnapshots()
        {
            // Build per-renderer snapshots
            var renderers = new List<RendererSnapshot>();
            // Track counts per layer: [layer] -> (static, skinned)
            var layerCounts = new Dictionary<int, (int s, int k)>();
            
            for (int i = 0; i < cachedRenderers.Count; i++)
            {
                var r = cachedRenderers[i];
                if (r == null) continue;
                int layer = r.gameObject.layer;
                renderers.Add(new RendererSnapshot
                {
                    InstanceId = r.GetInstanceID(),
                    Name = r.gameObject.name ?? "(null)",
                    Type = "Static",
                    Layer = layer
                });
                if (!layerCounts.ContainsKey(layer)) layerCounts[layer] = (0, 0);
                var c = layerCounts[layer];
                layerCounts[layer] = (c.s + 1, c.k);
            }
            
            for (int i = 0; i < cachedSkinnedRenderers.Count; i++)
            {
                var r = cachedSkinnedRenderers[i];
                if (r == null) continue;
                int layer = r.gameObject.layer;
                renderers.Add(new RendererSnapshot
                {
                    InstanceId = r.GetInstanceID(),
                    Name = r.gameObject.name ?? "(null)",
                    Type = "Skinned",
                    Layer = layer
                });
                if (!layerCounts.ContainsKey(layer)) layerCounts[layer] = (0, 0);
                var c = layerCounts[layer];
                layerCounts[layer] = (c.s, c.k + 1);
            }
            
            // Build layer snapshots
            var layers = new List<LayerSnapshot>();
            lock (_disabledLock)
            {
                foreach (var kv in layerCounts)
                {
                    string name;
                    try { name = LayerMask.LayerToName(kv.Key); }
                    catch { name = ""; }
                    if (string.IsNullOrEmpty(name)) name = $"Layer {kv.Key}";
                    
                    layers.Add(new LayerSnapshot
                    {
                        LayerIndex = kv.Key,
                        LayerName = name,
                        StaticCount = kv.Value.s,
                        SkinnedCount = kv.Value.k,
                        UserDisabled = _disabledLayers.Contains(kv.Key)
                    });
                }
            }
            
            layers.Sort((a, b) => a.LayerIndex.CompareTo(b.LayerIndex));
            
            Volatile.Write(ref _layerSnapshots, layers.ToArray());
            Volatile.Write(ref _rendererSnapshots, renderers.ToArray());
        }
        
        /// <summary>
        /// Capture static meshes from scene
        /// </summary>
        public void CaptureStaticMeshes(FrameState state, int frameCount)
        {
            // Rebuild cache if stale
            if (frameCount - rendererCacheFrame > configRendererCacheDuration.Value || rendererCacheFrame < 0)
            {
                RefreshRendererCache(frameCount);
            }
            
            // Get camera
            Camera mainCam = cameraHandler.GetPreferredCamera();
            Vector3 camPos = mainCam != null ? mainCam.transform.position : Vector3.zero;
            
            // Capture camera
            if (mainCam != null)
            {
                var t = mainCam.transform;
                state.camera = new CameraData
                {
                    position = t.position,
                    forward = t.forward,
                    up = t.up,
                    right = t.right,
                    fov = mainCam.fieldOfView,
                    aspect = mainCam.aspect,
                    nearPlane = mainCam.nearClipPlane,
                    farPlane = mainCam.farClipPlane,
                    valid = true
                };
            }
            
            // Debug toggle check
            if (!configCaptureStaticMeshes.Value)
                return;
            
            // Capture static meshes
            foreach (var renderer in cachedRenderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                
                if (IsLayerDisabled(renderer.gameObject.layer))
                    continue;

                if (IsRendererDisabled(renderer.GetInstanceID()))
                    continue;
                
                // Optional visibility culling (can cause issues in some games)
                if (configUseVisibilityCulling.Value && !renderer.isVisible)
                    continue;
                
                // Optional distance culling
                if (configUseDistanceCulling.Value)
                {
                    float maxDist = configMaxRenderDistance.Value;
                    float sqrDistance = (renderer.bounds.center - camPos).sqrMagnitude;
                    if (sqrDistance > maxDist * maxDist)
                        continue;
                }
                
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;
                
                var mesh = meshFilter.sharedMesh;
                int meshId = mesh.GetInstanceID();
                
                // Queue mesh for creation if not cached and not already queued
                if (!meshConverter.IsMeshCached(meshId) && !meshesInQueue.Contains(meshId))
                {
                    // Capture material textures first
                    var material = renderer.sharedMaterial;
                    if (material != null)
                    {
                        int matId = material.GetInstanceID();
                        materialManager.CaptureMaterialTextures(material, matId);
                    }
                    
                    // Queue mesh with its material
                    meshesToCreate.Enqueue(new MeshToCreate
                    {
                        mesh = mesh,
                        material = material
                    });
                    meshesInQueue.Add(meshId);
                    
                    continue;
                }
                
                // Add instance
                state.instances.Add(new MeshInstanceData
                {
                    meshId = meshId,
                    localToWorld = renderer.transform.localToWorldMatrix
                });
            }
        }
        
        /// <summary>
        /// Process queued mesh creation (batched with frame time budget)
        /// </summary>
        public void ProcessMeshCreationBatch()
        {
            // Adaptive batch size based on queue length
            int batchSize = meshesToCreate.Count > 100 ? 3 : Math.Min(5, meshesToCreate.Count);
            
            // Frame time budget: max 2ms for mesh creation
            var startTime = System.Diagnostics.Stopwatch.StartNew();
            const float maxMilliseconds = 2.0f;
            
            for (int i = 0; i < batchSize; i++)
            {
                if (meshesToCreate.Count == 0) break;
                
                var meshData = meshesToCreate.Dequeue();
                if (meshData.mesh == null) continue;
                
                int meshId = meshData.mesh.GetInstanceID();
                
                // Remove from queue tracking
                meshesInQueue.Remove(meshId);
                
                if (meshConverter.IsMeshCached(meshId))
                    continue;
                
                try
                {
                    // Create mesh with its material!
                    IntPtr handle = meshConverter.CreateRemixMeshFromUnity(meshData.mesh, meshData.material);
                    
                    if (handle == IntPtr.Zero && configDebugLogInterval.Value > 0)
                    {
                        //logger.LogWarning($"Failed to create mesh '{meshData.mesh.name}' in batch");
                    }
                    
                    // Check time budget - break if exceeded
                    if (startTime.Elapsed.TotalMilliseconds > maxMilliseconds)
                        break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Exception creating mesh: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Capture skinned meshes — Remix GPU skinning primary path with BakeMesh fallback.
        /// GPU skinning: mesh created once (bind-pose + bone weights), bone transforms sent per-frame.
        /// BakeMesh fallback: when bone extraction fails (>256 bones, missing weights, etc.).
        /// </summary>
        public void CaptureSkinnedMeshes(FrameState state, int frameCount)
        {
            // Rebuild cache if stale
            if (frameCount - rendererCacheFrame > configRendererCacheDuration.Value || rendererCacheFrame < 0)
            {
                RefreshRendererCache(frameCount);
            }
            
            if (!configCaptureSkinnedMeshes.Value)
                return;
            
            skinnedCaptureCount++;
            bool doLog = configDebugLogInterval.Value > 0 && (skinnedCaptureCount % configDebugLogInterval.Value == 1);
            
            int total = cachedSkinnedRenderers.Count;
            if (total == 0) return;
            
            int gpuSkinned = 0;
            int baked = 0;
            int skipNull = 0, skipLayer = 0, skipVis = 0, skipNoMesh = 0;
            var validSkinnedIds = new HashSet<int>();
            
            // BakeMesh fallback budget
            var bakeSw = System.Diagnostics.Stopwatch.StartNew();
            const float bakeMaxMs = 5.0f;
            if (skinnedRoundRobinIndex >= total) skinnedRoundRobinIndex = 0;
            var bakeFallbackQueue = new List<(int idx, SkinnedMeshRenderer smr, int skinnedId, Matrix4x4 matrix, int matId)>();
            
            for (int i = 0; i < total; i++)
            {
                var skinned = cachedSkinnedRenderers[i];
                if (skinned == null || !skinned.enabled || !skinned.gameObject.activeInHierarchy)
                {
                    skipNull++;
                    continue;
                }
                
                if (IsLayerDisabled(skinned.gameObject.layer) || IsRendererDisabled(skinned.GetInstanceID()))
                {
                    skipLayer++;
                    continue;
                }
                
                if (configUseVisibilityCulling.Value && !skinned.isVisible)
                {
                    skipVis++;
                    continue;
                }
                
                if (skinned.sharedMesh == null)
                {
                    skipNoMesh++;
                    continue;
                }
                
                int skinnedId = skinned.GetInstanceID();
                validSkinnedIds.Add(skinnedId);
                
                // Compute unscaled transform (sign-only scale preserves winding)
                Vector3 ls = skinned.transform.lossyScale;
                Matrix4x4 unscaledMatrix = Matrix4x4.TRS(
                    skinned.transform.position,
                    skinned.transform.rotation,
                    new Vector3(Mathf.Sign(ls.x), Mathf.Sign(ls.y), Mathf.Sign(ls.z))
                );
                
                int matId = ResolveMaterial(skinned, doLog);
                int sharedMeshId = skinned.sharedMesh.GetInstanceID();
                
                // Try GPU skinning path: extract bone weights once, compute bone transforms each frame
                if (!cachedSkinning.ContainsKey(sharedMeshId))
                    CacheSkinningData(skinned, sharedMeshId);
                
                var skinData = cachedSkinning.TryGetValue(sharedMeshId, out var sd) ? sd : null;
                if (skinData != null && skinned.bones != null && skinned.bones.Length > 0)
                {
                    // Compute bone transforms for Remix GPU skinning
                    var bones = skinned.bones;
                    int boneCount = Mathf.Min(bones.Length, skinData.boneCount);
                    var boneMatrices = new Matrix4x4[boneCount];
                    
                    // Bone transforms relative to instance transform:
                    //   boneTransform[i] = instanceInverse * bones[i].l2w * bindPoses[i]
                    // Remix applies: v_final = instanceTransform * Σ(w * boneTransform) * v_bind
                    //   = unscaled * unscaled^-1 * Σ(w * bones.l2w * bindPoses) * v_bind = v_world
                    Matrix4x4 invInstance = unscaledMatrix.inverse;
                    for (int b = 0; b < boneCount; b++)
                    {
                        boneMatrices[b] = (bones[b] != null)
                            ? invInstance * bones[b].localToWorldMatrix * skinData.bindPoses[b]
                            : Matrix4x4.identity;
                    }
                    
                    persistentSkinnedData[skinnedId] = new SkinnedMeshData
                    {
                        meshId = skinnedId,
                        materialId = matId,
                        vertices = skinData.bindVertices,
                        normals = skinData.bindNormals,
                        uvs = skinData.uvs,
                        triangles = skinData.triangles,
                        localToWorld = unscaledMatrix,
                        boneTransforms = boneMatrices,
                        skinningData = skinData
                    };
                    gpuSkinned++;
                    continue;
                }
                
                // Update transform on existing persistent data
                if (persistentSkinnedData.TryGetValue(skinnedId, out var existing))
                {
                    existing.localToWorld = unscaledMatrix;
                    persistentSkinnedData[skinnedId] = existing;
                }
                
                // Queue for BakeMesh fallback
                bakeFallbackQueue.Add((i, skinned, skinnedId, unscaledMatrix, matId));
            }
            
            // Process BakeMesh fallback queue with round-robin + time budget
            if (bakeFallbackQueue.Count > 0)
            {
                int fbStart = 0;
                for (int f = 0; f < bakeFallbackQueue.Count; f++)
                {
                    if (bakeFallbackQueue[f].idx >= skinnedRoundRobinIndex) { fbStart = f; break; }
                }
                
                for (int f = 0; f < bakeFallbackQueue.Count; f++)
                {
                    int fi = (fbStart + f) % bakeFallbackQueue.Count;
                    var (idx, skinned, skinnedId, matrix, matId) = bakeFallbackQueue[fi];
                    
                    if (BakeSingleMesh(skinned, skinnedId, matId, matrix, doLog))
                        baked++;
                    
                    if (bakeSw.Elapsed.TotalMilliseconds > bakeMaxMs)
                    {
                        skinnedRoundRobinIndex = bakeFallbackQueue[(fi + 1) % bakeFallbackQueue.Count].idx;
                        break;
                    }
                }
            }
            
            // Prune stale persistent entries
            if (frameCount % 120 == 0)
            {
                var staleIds = new List<int>();
                foreach (var id in persistentSkinnedData.Keys)
                    if (!validSkinnedIds.Contains(id))
                        staleIds.Add(id);
                foreach (var id in staleIds)
                    persistentSkinnedData.Remove(id);
            }
            
            // Step 5: Submit ALL persistent entries to frame state
            foreach (var entry in persistentSkinnedData.Values)
                state.skinned.Add(entry);
            
            if (doLog && total > 0)
            {
                logger.LogInfo($"CaptureSkinnedMeshes: gpuSkinned={gpuSkinned}, baked={baked}, " +
                    $"skip(null={skipNull},layer={skipLayer},vis={skipVis},mesh={skipNoMesh}), " +
                    $"cached={persistentSkinnedData.Count}, total={total}");
            }
        }
        
        /// <summary>
        /// Cache UVs, triangles, and vertex buffer layout for a sharedMesh. Called once per unique mesh.
        /// </summary>
        private void CacheMeshTopology(Mesh sharedMesh, int meshId, bool doLog)
        {
            try
            {
                var uvCoords = sharedMesh.uv;
                
                // Combine all submesh triangles
                var allTris = new List<int>();
                for (int i = 0; i < sharedMesh.subMeshCount; i++)
                {
                    if (sharedMesh.GetTopology(i) != MeshTopology.Triangles)
                        continue;
                    var subTris = sharedMesh.GetTriangles(i);
                    if (subTris != null && subTris.Length > 0)
                        allTris.AddRange(subTris);
                }
                
                if (allTris.Count == 0 || allTris.Count % 3 != 0)
                {
                    if (!sharedMesh.isReadable)
                    {
                        // Mesh not readable — can't get triangles directly.
                        // Cache vertex buffer layout now; topology will be completed from first BakeMesh.
                        int posOff2 = -1, nrmOff2 = -1, stride2 = 0;
                        if (sharedMesh.HasVertexAttribute(VertexAttribute.Position))
                        {
                            int s = sharedMesh.GetVertexAttributeStream(VertexAttribute.Position);
                            if (s == 0)
                            {
                                posOff2 = sharedMesh.GetVertexAttributeOffset(VertexAttribute.Position);
                                stride2 = sharedMesh.GetVertexBufferStride(0);
                            }
                        }
                        if (sharedMesh.HasVertexAttribute(VertexAttribute.Normal))
                        {
                            int s = sharedMesh.GetVertexAttributeStream(VertexAttribute.Normal);
                            if (s == 0)
                                nrmOff2 = sharedMesh.GetVertexAttributeOffset(VertexAttribute.Normal);
                        }
                        bool layoutOk = posOff2 >= 0 && stride2 > 0;
                        cachedTopology[meshId] = new CachedMeshTopology
                        {
                            valid = false,
                            layoutValid = layoutOk,
                            vertexCount = sharedMesh.vertexCount,
                            positionOffset = posOff2,
                            normalOffset = nrmOff2,
                            stride = stride2
                        };
                        logger.LogInfo($"[MeshTopology] '{sharedMesh.name}' not readable — cached layout (layoutValid={layoutOk} stride={stride2} posOff={posOff2} nrmOff={nrmOff2} verts={sharedMesh.vertexCount}), awaiting BakeMesh for triangles/UVs");
                        return;
                    }

                    logger.LogInfo($"[MeshTopology] '{sharedMesh.name}' INVALID: subMeshCount={sharedMesh.subMeshCount} triCount={allTris.Count} isReadable={sharedMesh.isReadable}");
                    cachedTopology[meshId] = new CachedMeshTopology { valid = false };
                    return;
                }
                
                // Get vertex buffer layout for GPU readback
                int posOffset = -1;
                int nrmOffset = -1;
                int stride = 0;
                
                if (sharedMesh.HasVertexAttribute(VertexAttribute.Position))
                {
                    int stream = sharedMesh.GetVertexAttributeStream(VertexAttribute.Position);
                    if (stream == 0) // GPU skinned buffer is always stream 0
                    {
                        posOffset = sharedMesh.GetVertexAttributeOffset(VertexAttribute.Position);
                        stride = sharedMesh.GetVertexBufferStride(0);
                    }
                }
                
                if (sharedMesh.HasVertexAttribute(VertexAttribute.Normal))
                {
                    int stream = sharedMesh.GetVertexAttributeStream(VertexAttribute.Normal);
                    if (stream == 0)
                        nrmOffset = sharedMesh.GetVertexAttributeOffset(VertexAttribute.Normal);
                }
                
                bool topoValid = posOffset >= 0 && stride > 0;
                cachedTopology[meshId] = new CachedMeshTopology
                {
                    uvs = uvCoords ?? new Vector2[sharedMesh.vertexCount],
                    triangles = allTris.ToArray(),
                    vertexCount = sharedMesh.vertexCount,
                    positionOffset = posOffset,
                    normalOffset = nrmOffset,
                    stride = stride,
                    valid = topoValid
                };
                
                // Always log topology — only fires once per unique mesh
                logger.LogInfo($"[MeshTopology] '{sharedMesh.name}' verts={sharedMesh.vertexCount} tris={allTris.Count/3} stride={stride} posOff={posOffset} nrmOff={nrmOffset} valid={topoValid}");
            }
            catch (Exception ex)
            {
                cachedTopology[meshId] = new CachedMeshTopology { valid = false };
                logger.LogWarning($"[MeshTopology] Failed for '{sharedMesh.name}': {ex.Message}");
            }
        }
        
        private const int MAX_REMIX_BONES = 256;
        private const int BONES_PER_VERTEX = 4; // D3D9 standard, Remix expectation
        
        /// <summary>
        /// Extract bind-pose geometry and bone weights from a SkinnedMeshRenderer's sharedMesh.
        /// Stores result in cachedSkinning. Called once per unique sharedMesh. Returns null on failure.
        /// </summary>
        private void CacheSkinningData(SkinnedMeshRenderer skinned, int sharedMeshId)
        {
            var mesh = skinned.sharedMesh;
            try
            {
                // Bone data validation
                var bindPoses = mesh.bindposes;
                var bones = skinned.bones;
                if (bindPoses == null || bindPoses.Length == 0 || bones == null || bones.Length == 0)
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' has no bones/bindposes — BakeMesh fallback");
                    return;
                }
                
                int boneCount = Mathf.Min(bindPoses.Length, bones.Length);
                if (boneCount > MAX_REMIX_BONES)
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' has {boneCount} bones (>{MAX_REMIX_BONES}) — BakeMesh fallback");
                    return;
                }
                
                // Extract bone weights — try modern API first (Unity 2019.3+), then legacy
                int vertexCount = mesh.vertexCount;
                float[] blendWeights = new float[BONES_PER_VERTEX * vertexCount];
                uint[] blendIndices = new uint[BONES_PER_VERTEX * vertexCount];
                
                try
                {
                    var bonesPerVertex = mesh.GetBonesPerVertex();
                    var allBoneWeights = mesh.GetAllBoneWeights();
                    
                    if (bonesPerVertex.Length == 0 || allBoneWeights.Length == 0)
                        throw new InvalidOperationException("Empty bone weight data");
                    
                    int weightIdx = 0;
                    for (int v = 0; v < vertexCount; v++)
                    {
                        int count = bonesPerVertex[v];
                        int baseIdx = v * BONES_PER_VERTEX;
                        for (int w = 0; w < count && w < BONES_PER_VERTEX; w++)
                        {
                            var bw = allBoneWeights[weightIdx + w];
                            blendWeights[baseIdx + w] = bw.weight;
                            blendIndices[baseIdx + w] = (uint)bw.boneIndex;
                        }
                        weightIdx += count;
                    }
                }
                catch
                {
                    // Legacy fallback: BoneWeight per vertex (always exactly 4)
                    try
                    {
                        var legacyWeights = mesh.boneWeights;
                        if (legacyWeights == null || legacyWeights.Length == 0)
                        {
                            logger.LogInfo($"[Skinning] '{mesh.name}' bone weights not accessible — BakeMesh fallback");
                            return;
                        }
                        
                        for (int v = 0; v < vertexCount; v++)
                        {
                            var bw = legacyWeights[v];
                            int baseIdx = v * BONES_PER_VERTEX;
                            blendWeights[baseIdx + 0] = bw.weight0;
                            blendWeights[baseIdx + 1] = bw.weight1;
                            blendWeights[baseIdx + 2] = bw.weight2;
                            blendWeights[baseIdx + 3] = bw.weight3;
                            blendIndices[baseIdx + 0] = (uint)bw.boneIndex0;
                            blendIndices[baseIdx + 1] = (uint)bw.boneIndex1;
                            blendIndices[baseIdx + 2] = (uint)bw.boneIndex2;
                            blendIndices[baseIdx + 3] = (uint)bw.boneIndex3;
                        }
                    }
                    catch (Exception ex2)
                    {
                        logger.LogInfo($"[Skinning] '{mesh.name}' legacy bone weights failed: {ex2.Message} — BakeMesh fallback");
                        return;
                    }
                }
                
                // Extract bind-pose geometry
                Vector3[] bindVerts, bindNorms;
                Vector2[] uvs;
                int[] triangles;
                
                if (mesh.isReadable)
                {
                    bindVerts = mesh.vertices;
                    bindNorms = mesh.normals;
                    uvs = mesh.uv;
                    
                    var allTris = new List<int>();
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        if (mesh.GetTopology(s) != MeshTopology.Triangles)
                            continue;
                        var sub = mesh.GetTriangles(s);
                        if (sub != null && sub.Length > 0)
                            allTris.AddRange(sub);
                    }
                    triangles = allTris.ToArray();
                }
                else
                {
                    // Non-readable: bake once, then recover bind-pose vertices
                    // by inverting the per-vertex skinning transform.
                    var tempMesh = new Mesh();
                    skinned.BakeMesh(tempMesh);
                    uvs = tempMesh.uv;
                    
                    var allTris = new List<int>();
                    for (int s = 0; s < tempMesh.subMeshCount; s++)
                    {
                        if (tempMesh.GetTopology(s) != MeshTopology.Triangles)
                            continue;
                        var sub = tempMesh.GetTriangles(s);
                        if (sub != null && sub.Length > 0)
                            allTris.AddRange(sub);
                    }
                    triangles = allTris.ToArray();
                    
                    // Recover bind-pose vertices by inverting the per-vertex skinning transform.
                    // BakeMesh(false) returns vertices in SMR local space WITHOUT scale:
                    //   v_baked = noScale_w2l * Σ(w_i * bones[i].l2w * bindPoses[i]) * v_bindpose
                    // Per vertex, the no-scale local skinning matrix is:
                    //   localSkinMatrix = noScale_w2l * Σ(w_i * bones[i].l2w * bindPoses[i])
                    // So: v_bindpose = localSkinMatrix^{-1} * v_baked
                    
                    // BakeMesh(false) strips the SMR's scale, so use no-scale W2L to match.
                    Matrix4x4 noScaleW2L = Matrix4x4.TRS(
                        skinned.transform.position, skinned.transform.rotation, Vector3.one).inverse;
                    var localBoneMatrices = new Matrix4x4[boneCount];
                    for (int b = 0; b < boneCount; b++)
                    {
                        localBoneMatrices[b] = (bones[b] != null)
                            ? noScaleW2L * bones[b].localToWorldMatrix * bindPoses[b]
                            : Matrix4x4.identity;
                    }
                    
                    var bakedVerts = tempMesh.vertices;
                    var bakedNorms = tempMesh.normals ?? new Vector3[0];
                    UnityEngine.Object.Destroy(tempMesh);
                    
                    bindVerts = new Vector3[bakedVerts.Length];
                    bindNorms = new Vector3[bakedVerts.Length];
                    int degenerateCount = 0;
                    
                    for (int v = 0; v < bakedVerts.Length; v++)
                    {
                        // Build per-vertex skinning matrix (local space)
                        int baseIdx = v * BONES_PER_VERTEX;
                        Matrix4x4 skinMatrix = new Matrix4x4();
                        for (int w = 0; w < BONES_PER_VERTEX; w++)
                        {
                            float weight = blendWeights[baseIdx + w];
                            if (weight <= 0f) continue;
                            int boneIdx = (int)blendIndices[baseIdx + w];
                            if (boneIdx >= boneCount) continue;
                            Matrix4x4 bm = localBoneMatrices[boneIdx];
                            for (int r = 0; r < 4; r++)
                                for (int c = 0; c < 4; c++)
                                    skinMatrix[r, c] += weight * bm[r, c];
                        }
                        
                        // Invert and recover bind-pose vertex
                        Matrix4x4 inv = skinMatrix.inverse;
                        float det = skinMatrix.determinant;
                        if (Mathf.Abs(det) < 1e-6f)
                        {
                            // Singular matrix — keep baked vertex as-is (fallback)
                            bindVerts[v] = bakedVerts[v];
                            bindNorms[v] = (v < bakedNorms.Length) ? bakedNorms[v] : Vector3.up;
                            degenerateCount++;
                            continue;
                        }
                        
                        Vector4 bp = inv * new Vector4(bakedVerts[v].x, bakedVerts[v].y, bakedVerts[v].z, 1f);
                        bindVerts[v] = new Vector3(bp.x, bp.y, bp.z);
                        
                        if (v < bakedNorms.Length)
                        {
                            Vector4 bn = inv * new Vector4(bakedNorms[v].x, bakedNorms[v].y, bakedNorms[v].z, 0f);
                            Vector3 n = new Vector3(bn.x, bn.y, bn.z);
                            bindNorms[v] = (n.sqrMagnitude > 1e-8f) ? n.normalized : Vector3.up;
                        }
                        else
                        {
                            bindNorms[v] = Vector3.up;
                        }
                    }
                    
                    if (degenerateCount > 0)
                        logger.LogInfo($"[Skinning] '{mesh.name}' {degenerateCount}/{bakedVerts.Length} verts had degenerate skin matrices");
                }
                
                if (bindVerts == null || bindVerts.Length == 0 || triangles.Length == 0)
                {
                    logger.LogInfo($"[Skinning] '{mesh.name}' empty geometry — BakeMesh fallback");
                    return;
                }
                
                if (bindNorms == null || bindNorms.Length != bindVerts.Length)
                {
                    bindNorms = new Vector3[bindVerts.Length];
                    for (int n = 0; n < bindNorms.Length; n++)
                        bindNorms[n] = Vector3.up;
                }
                if (uvs == null || uvs.Length != bindVerts.Length)
                    uvs = new Vector2[bindVerts.Length];
                
                cachedSkinning[sharedMeshId] = new CachedSkinningData
                {
                    bindVertices = bindVerts,
                    bindNormals = bindNorms,
                    uvs = uvs,
                    triangles = triangles,
                    blendWeights = blendWeights,
                    blendIndices = blendIndices,
                    bonesPerVertex = BONES_PER_VERTEX,
                    boneCount = boneCount,
                    bindPoses = bindPoses,
                    meshCreated = false
                };
                
                logger.LogInfo($"[Skinning] '{mesh.name}' cached: {vertexCount} verts, {triangles.Length / 3} tris, {boneCount} bones, readable={mesh.isReadable}");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"[Skinning] '{mesh.name}' extraction failed: {ex.Message} — BakeMesh fallback");
            }
        }
        
        /// <summary>
        /// Try to issue an async GPU readback for a skinned mesh renderer's vertex buffer.
        /// Returns true if request was issued, false if GPU readback not possible for this renderer.
        /// </summary>
        private bool TryIssueGPUReadback(SkinnedMeshRenderer skinned, int skinnedId, int sharedMeshId, int matId, Matrix4x4 localToWorld, bool doLog)
        {
            try
            {
                // Ensure vertex buffer is readable — must be set before the GPU skins this renderer,
                // so we configure it and skip readback this frame (buffer won't exist yet).
                if (!configuredBufferTargets.Contains(skinnedId))
                {
                    skinned.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                    skinned.forceMatrixRecalculationPerRender = true;
                    configuredBufferTargets.Add(skinnedId);
                    // Always log — only fires once per renderer
                    logger.LogInfo($"  GPU readback: configured vertexBufferTarget for '{skinned.name}' (id={skinnedId}), deferring to next frame");
                    return false; // fall through to BakeMesh this frame; buffer available next frame
                }
                
                var buffer = skinned.GetVertexBuffer();
                if (buffer == null || !buffer.IsValid())
                {
                    // Always log — important diagnostic
                    logger.LogInfo($"  GPU readback: GetVertexBuffer() returned {(buffer == null ? "null" : "invalid")} for '{skinned.name}' (id={skinnedId})");
                    buffer?.Dispose();
                    return false;
                }
                
                var request = AsyncGPUReadback.Request(buffer);
                buffer.Dispose(); // we don't hold the buffer; the request keeps a ref internally
                
                pendingReadbacks.Add(new PendingReadback
                {
                    skinnedId = skinnedId,
                    materialId = matId,
                    sharedMeshId = sharedMeshId,
                    localToWorld = localToWorld,
                    request = request
                });
                
                return true;
            }
            catch (Exception ex)
            {
                // Always log — important diagnostic
                logger.LogWarning($"  GPU readback: exception for '{skinned.name}' (id={skinnedId}): {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// BakeMesh fallback: CPU re-skin a single skinned mesh.
        /// </summary>
        private bool BakeSingleMesh(SkinnedMeshRenderer skinned, int skinnedId, int matId, Matrix4x4 localToWorld, bool doLog)
        {
            try
            {
                if (!bakedMeshes.TryGetValue(skinnedId, out Mesh bakedMesh) || bakedMesh == null)
                {
                    bakedMesh = new Mesh();
                    bakedMesh.name = $"Baked_{skinned.name}";
                    bakedMeshes[skinnedId] = bakedMesh;
                }
                
                skinned.BakeMesh(bakedMesh);
                
                var verts = bakedMesh.vertices;
                var norms = bakedMesh.normals;
                
                // Reuse cached UVs and triangles — they never change between frames
                int sharedMeshId2 = skinned.sharedMesh.GetInstanceID();
                Vector2[] uvCoords;
                int[] tris;
                if (cachedTopology.TryGetValue(sharedMeshId2, out var topo) && topo.valid && topo.uvs != null && topo.triangles != null)
                {
                    uvCoords = topo.uvs;
                    tris = topo.triangles;
                }
                else
                {
                    uvCoords = bakedMesh.uv;
                    var allTris = new List<int>();
                    for (int i = 0; i < bakedMesh.subMeshCount; i++)
                    {
                        if (bakedMesh.GetTopology(i) != MeshTopology.Triangles)
                            continue;
                        var subTris = bakedMesh.GetTriangles(i);
                        if (subTris != null && subTris.Length > 0)
                            allTris.AddRange(subTris);
                    }
                    tris = allTris.ToArray();
                }
                
                if (verts == null || verts.Length == 0 || tris.Length == 0 || tris.Length % 3 != 0)
                    return false;
                
                persistentSkinnedData[skinnedId] = new SkinnedMeshData
                {
                    meshId = skinnedId,
                    materialId = matId,
                    vertices = verts,
                    normals = norms,
                    uvs = uvCoords,
                    triangles = tris,
                    localToWorld = localToWorld
                };

                // Complete topology from baked mesh if triangles were unavailable (non-readable mesh)
                if (cachedTopology.TryGetValue(sharedMeshId2, out var pendingTopo) && !pendingTopo.valid)
                {
                    pendingTopo.uvs = uvCoords;
                    pendingTopo.triangles = tris;
                    pendingTopo.valid = true;
                    cachedTopology[sharedMeshId2] = pendingTopo;
                    logger.LogInfo($"[MeshTopology] '{skinned.sharedMesh.name}' topology completed from BakeMesh: verts={pendingTopo.vertexCount} tris={tris.Length / 3} stride={pendingTopo.stride}");
                }

                return true;
            }
            catch (Exception ex)
            {
                if (doLog)
                    logger.LogError($"BakeMesh failed for '{skinned.name}': {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Resolve the best material for a skinned mesh renderer. Returns material instance ID.
        /// </summary>
        private int ResolveMaterial(SkinnedMeshRenderer skinned, bool doLog)
        {
            int matId = 0;
            Material bestMaterial = null;
            
            var materials = skinned.sharedMaterials;
            if (materials == null || materials.Length == 0)
                return 0;
            
            string[] textureProps = { "_MainTex", "_BaseMap", "_BaseColorMap", "_AlbedoTex" };
            
            foreach (var mat in materials)
            {
                if (mat == null) continue;
                bool hasTexture = mat.mainTexture != null;
                if (!hasTexture)
                {
                    foreach (var prop in textureProps)
                    {
                        if (mat.HasProperty(prop) && mat.GetTexture(prop) != null)
                        { hasTexture = true; break; }
                    }
                }
                if (hasTexture) { bestMaterial = mat; break; }
            }
            
            if (bestMaterial == null)
            {
                foreach (var mat in materials)
                {
                    if (mat != null) { bestMaterial = mat; break; }
                }
            }
            
            if (bestMaterial != null)
            {
                matId = bestMaterial.GetInstanceID();
                materialManager.CaptureMaterialTextures(bestMaterial, matId);
                
                if (doLog)
                {
                    string logKey = $"{skinned.name}:{bestMaterial.name}:{matId}";
                    if (!loggedSkinnedMaterials.Contains(logKey))
                    {
                        loggedSkinnedMaterials.Add(logKey);
                        logger.LogInfo($"  Skinned mesh '{skinned.name}' using material '{bestMaterial.name}' (ID: {matId})");
                    }
                }
            }
            
            return matId;
        }
        
        /// <summary>
        /// Cleanup resources
        /// </summary>
        public void Cleanup()
        {
            foreach (var mesh in bakedMeshes.Values)
            {
                if (mesh != null)
                {
                    UnityEngine.Object.Destroy(mesh);
                }
            }
            bakedMeshes.Clear();
            pendingReadbacks.Clear();
            configuredBufferTargets.Clear();
            cachedTopology.Clear();
        }
    }
}
