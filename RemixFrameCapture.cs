using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

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
        private float maxRenderDistanceSqr;
        
        // Cached baked meshes for skinned renderers
        private Dictionary<int, Mesh> bakedMeshes = new Dictionary<int, Mesh>();
        private Dictionary<int, Matrix4x4> lastSkinnedTransforms = new Dictionary<int, Matrix4x4>();
        
        // Mesh creation queue with materials
        private struct MeshToCreate
        {
            public Mesh mesh;
            public Material material;
        }
        private Queue<MeshToCreate> meshesToCreate = new Queue<MeshToCreate>();
        private HashSet<int> meshesInQueue = new HashSet<int>(); // Track which meshes are already queued
        
        private int skinnedCaptureCount = 0;
        
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
            
            this.maxRenderDistanceSqr = maxRenderDistance.Value * maxRenderDistance.Value;
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
            logger.LogInfo("Renderer caches invalidated");
        }
        
        /// <summary>
        /// Refresh renderer cache
        /// </summary>
        private void RefreshRendererCache(int frameCount)
        {
            cachedRenderers.Clear();
            cachedSkinnedRenderers.Clear();
            
            cachedRenderers.AddRange(UnityEngine.Object.FindObjectsOfType<MeshRenderer>());
            cachedSkinnedRenderers.AddRange(UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>());
            
            rendererCacheFrame = frameCount;
            logger.LogInfo($"Renderer cache refreshed: {cachedRenderers.Count} static, {cachedSkinnedRenderers.Count} skinned");
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
                
                // Optional visibility culling (can cause issues in some games)
                if (configUseVisibilityCulling.Value && !renderer.isVisible)
                    continue;
                
                // Optional distance culling
                if (configUseDistanceCulling.Value)
                {
                    float sqrDistance = (renderer.bounds.center - camPos).sqrMagnitude;
                    if (sqrDistance > maxRenderDistanceSqr)
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
        /// Capture skinned meshes with baking
        /// </summary>
        public void CaptureSkinnedMeshes(FrameState state, int frameCount)
        {
            // Rebuild cache if stale
            if (frameCount - rendererCacheFrame > configRendererCacheDuration.Value || rendererCacheFrame < 0)
            {
                RefreshRendererCache(frameCount);
            }
            
            // Debug toggle check
            if (!configCaptureSkinnedMeshes.Value)
                return;
            
            skinnedCaptureCount++;
            bool doLog = configDebugLogInterval.Value > 0 && (skinnedCaptureCount % configDebugLogInterval.Value == 1);
            
            int baked = 0;
            int skipped = 0;
            
            // Frame time budget for skinned mesh baking: max 5ms
            var startTime = System.Diagnostics.Stopwatch.StartNew();
            const float maxMilliseconds = 5.0f;
            int maxSkinnedPerFrame = 20; // Limit skinned meshes per frame
            
            foreach (var skinned in cachedSkinnedRenderers)
            {
                if (skinned == null || !skinned.enabled || !skinned.gameObject.activeInHierarchy)
                {
                    skipped++;
                    continue;
                }
                
                // Optional visibility culling (can cause issues in some games)
                if (configUseVisibilityCulling.Value && !skinned.isVisible)
                {
                    skipped++;
                    continue;
                }
                
                if (skinned.sharedMesh == null)
                {
                    skipped++;
                    continue;
                }
                
                try
                {
                    int skinnedId = skinned.GetInstanceID();
                    Matrix4x4 currentTransform = skinned.transform.localToWorldMatrix;
                    
                    lastSkinnedTransforms[skinnedId] = currentTransform;
                    
                    // Calculate unscaled matrix (preserve scale sign for mirroring)
                    Vector3 lossyScale = skinned.transform.lossyScale;
                    Vector3 signScale = new Vector3(
                        Mathf.Sign(lossyScale.x),
                        Mathf.Sign(lossyScale.y),
                        Mathf.Sign(lossyScale.z)
                    );
                    
                    Matrix4x4 unscaledMatrix = Matrix4x4.TRS(
                        skinned.transform.position,
                        skinned.transform.rotation,
                        signScale
                    );
                    
                    // Get or create baked mesh
                    if (!bakedMeshes.TryGetValue(skinnedId, out Mesh bakedMesh) || bakedMesh == null)
                    {
                        bakedMesh = new Mesh();
                        bakedMesh.name = $"Baked_{skinned.name}";
                        bakedMeshes[skinnedId] = bakedMesh;
                    }
                    
                    // Bake current pose
                    skinned.BakeMesh(bakedMesh);
                    
                    // Copy data
                    var verts = bakedMesh.vertices;
                    var norms = bakedMesh.normals;
                    var uvCoords = bakedMesh.uv;
                    
                    // Combine submeshes
                    var allTris = new List<int>();
                    for (int i = 0; i < bakedMesh.subMeshCount; i++)
                    {
                        var topology = bakedMesh.GetTopology(i);
                        if (topology != MeshTopology.Triangles)
                        {
                            if (doLog)
                                logger.LogWarning($"Skipping submesh {i} of '{skinned.name}' with topology: {topology}");
                            continue;
                        }
                        
                        var subTris = bakedMesh.GetTriangles(i);
                        if (subTris != null && subTris.Length > 0)
                        {
                            allTris.AddRange(subTris);
                        }
                    }
                    var tris = allTris.ToArray();
                    
                    if (verts == null || verts.Length == 0 || tris == null || tris.Length == 0)
                    {
                        if (doLog)
                            logger.LogWarning($"Baked mesh {skinned.name} has no data");
                        continue;
                    }
                    
                    if (tris.Length % 3 != 0)
                    {
                        logger.LogError($"Baked mesh {skinned.name} has invalid triangle count: {tris.Length}");
                        continue;
                    }
                    
                    // Capture material for skinned mesh
                    // Try to find a material with textures (check all materials)
                    int matId = 0;
                    Material bestMaterial = null;
                    
                    var materials = skinned.sharedMaterials;
                    if (materials != null && materials.Length > 0)
                    {
                        // First pass: try to find a material with an albedo texture
                        // Check common texture property names
                        string[] textureProps = { "_MainTex", "_BaseMap", "_BaseColorMap", "_AlbedoTex" };
                        
                        foreach (var mat in materials)
                        {
                            if (mat != null)
                            {
                                bool hasTexture = mat.mainTexture != null;
                                
                                // Also check common texture properties
                                if (!hasTexture)
                                {
                                    foreach (var prop in textureProps)
                                    {
                                        if (mat.HasProperty(prop) && mat.GetTexture(prop) != null)
                                        {
                                            hasTexture = true;
                                            break;
                                        }
                                    }
                                }
                                
                                if (hasTexture)
                                {
                                    bestMaterial = mat;
                                    break;
                                }
                            }
                        }
                        
                        // Fallback: use first non-null material
                        if (bestMaterial == null)
                        {
                            foreach (var mat in materials)
                            {
                                if (mat != null)
                                {
                                    bestMaterial = mat;
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (bestMaterial != null)
                    {
                        matId = bestMaterial.GetInstanceID();
                        // Capture material textures if not already done
                        materialManager.CaptureMaterialTextures(bestMaterial, matId);
                        
                        // Only log unique mesh+material combinations when doLog is true
                        if (doLog)
                        {
                            string logKey = $"{skinned.name}:{bestMaterial.name}:{matId}";
                            if (!loggedSkinnedMaterials.Contains(logKey))
                            {
                                loggedSkinnedMaterials.Add(logKey);
                                logger.LogInfo($"  Skinned mesh '{skinned.name}' using material '{bestMaterial.name}' (ID: {matId}, hasTexture: {bestMaterial.mainTexture != null})");
                            }
                        }
                    }
                    else if (materials != null && materials.Length > 0)
                    {
                        // Always log warnings for missing materials (but only once)
                        string logKey = $"{skinned.name}:NO_USABLE:{materials.Length}";
                        if (!loggedSkinnedMaterials.Contains(logKey))
                        {
                            loggedSkinnedMaterials.Add(logKey);
                            logger.LogWarning($"  Skinned mesh '{skinned.name}' has {materials.Length} materials but none are usable!");
                        }
                    }
                    else
                    {
                        // Always log warnings for missing materials (but only once)
                        string logKey = $"{skinned.name}:NO_MATERIALS";
                        if (!loggedSkinnedMaterials.Contains(logKey))
                        {
                            loggedSkinnedMaterials.Add(logKey);
                            logger.LogWarning($"  Skinned mesh '{skinned.name}' has NO materials at all!");
                        }
                    }
                    
                    state.skinned.Add(new SkinnedMeshData
                    {
                        meshId = skinnedId,
                        materialId = matId,
                        vertices = verts,
                        normals = norms,
                        uvs = uvCoords,
                        triangles = tris,
                        localToWorld = unscaledMatrix
                    });
                    baked++;
                    
                    // Check budget - break if we've baked enough or exceeded time
                    if (baked >= maxSkinnedPerFrame || startTime.Elapsed.TotalMilliseconds > maxMilliseconds)
                        break;
                }
                catch (Exception ex)
                {
                    if (doLog)
                        logger.LogError($"Failed to bake {skinned.name}: {ex.Message}");
                }
            }
            
            // Log results periodically (controlled by debug log interval)
            if (doLog && cachedSkinnedRenderers.Count > 0)
            {
                logger.LogInfo($"CaptureSkinnedMeshes: baked={baked}, skipped={skipped}, total={cachedSkinnedRenderers.Count}");
            }
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
        }
    }
}
