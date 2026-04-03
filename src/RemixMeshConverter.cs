using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Converts Unity meshes (static and skinned) to Remix format
    /// </summary>
    public class RemixMeshConverter
    {
        /// <summary>
        /// Pack a Unity Color32 (RGBA) into D3DCOLOR/BGRA uint32 for Remix's B8G8R8A8_UNORM vertex color.
        /// </summary>
        public static uint Color32ToBGRA(Color32 c)
        {
            return (uint)(c.b | (c.g << 8) | (c.r << 16) | (c.a << 24));
        }
        private readonly ManualLogSource logger;
        private readonly RemixMaterialManager materialManager;
        private readonly ConfigEntry<int> configDebugLogInterval;
        private readonly object apiLock;
        
        // Cached delegates
        private RemixAPI.PFN_remixapi_CreateMesh createMeshFunc;
        private RemixAPI.PFN_remixapi_DestroyMesh destroyMeshFunc;
        private RemixAPI.PFN_remixapi_DrawInstance drawInstanceFunc;
        
        // Cache for game meshes - maps Unity mesh instance ID to Remix handle
        private ConcurrentDictionary<int, IntPtr> meshCache = new ConcurrentDictionary<int, IntPtr>();
        
        // Cache for skinned mesh Remix handles - keyed by skinned renderer ID
        private Dictionary<int, IntPtr> skinnedMeshHandles = new Dictionary<int, IntPtr>();
        
        // Deferred destruction queue to prevent flickering (destroy handles after they're no longer in use)
        private Queue<IntPtr> deferredDestroyQueue = new Queue<IntPtr>();
        private const int DEFERRED_DESTROY_FRAMES = 3; // Keep handles alive for 3 frames
        
        // Track which material each mesh uses (mesh ID -> material ID)
        private Dictionary<int, int> meshToMaterialMap = new Dictionary<int, int>();
        
        // GCHandle pooling for skinned meshes to reduce allocations
        private struct PinnedMeshData
        {
            public RemixAPI.remixapi_HardcodedVertex[] vertices;
            public uint[] indices;
            public GCHandle vertexHandle;
            public GCHandle indexHandle;
            public bool isPinned;
            public int vertexCapacity;
            public int indexCapacity;
        }
        private Dictionary<int, PinnedMeshData> pinnedMeshPool = new Dictionary<int, PinnedMeshData>();
        
        // Track logged material warnings to avoid spam
        private HashSet<string> loggedMaterialWarnings = new HashSet<string>();
        
        private int skinnedRenderCount = 0;
        
        public RemixMeshConverter(
            ManualLogSource logger,
            RemixMaterialManager materialManager,
            ConfigEntry<int> debugLogInterval,
            RemixAPI.remixapi_Interface remixInterface,
            object apiLock)
        {
            this.logger = logger;
            this.materialManager = materialManager;
            this.configDebugLogInterval = debugLogInterval;
            this.apiLock = apiLock;
            
            // Cache delegates
            if (remixInterface.CreateMesh != IntPtr.Zero)
                createMeshFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateMesh>(remixInterface.CreateMesh);
            
            if (remixInterface.DestroyMesh != IntPtr.Zero)
                destroyMeshFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyMesh>(remixInterface.DestroyMesh);
            
            if (remixInterface.DrawInstance != IntPtr.Zero)
                drawInstanceFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DrawInstance>(remixInterface.DrawInstance);
        }
        
        /// <summary>
        /// Check if mesh is already cached
        /// </summary>
        public bool IsMeshCached(int meshId)
        {
            return meshCache.ContainsKey(meshId);
        }
        
        /// <summary>
        /// Get cached mesh handle
        /// </summary>
        public bool TryGetMeshHandle(int meshId, out IntPtr handle)
        {
            return meshCache.TryGetValue(meshId, out handle);
        }
        
        /// <summary>
        /// Get cached skinned mesh handle
        /// </summary>
        public bool TryGetSkinnedMeshHandle(int meshId, out IntPtr handle)
        {
            return skinnedMeshHandles.TryGetValue(meshId, out handle);
        }
        
        /// <summary>
        /// Create and cache a Unity mesh in Remix format
        /// </summary>
        public IntPtr CreateRemixMeshFromUnity(Mesh mesh, Material material)
        {
            if (mesh == null || createMeshFunc == null)
                return IntPtr.Zero;
            
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uvs = mesh.uv;
            Color32[] colors = mesh.colors32;
            
            if (vertices == null || vertices.Length == 0)
                return IntPtr.Zero;
            
            // Extract triangles from all submeshes
            var allTris = new List<int>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var topology = mesh.GetTopology(i);
                if (topology != MeshTopology.Triangles)
                {
                    if (configDebugLogInterval.Value > 0)
                        logger.LogWarning($"Skipping submesh {i} of '{mesh.name}' with topology: {topology}");
                    continue;
                }
                
                var subTris = mesh.GetTriangles(i);
                if (subTris != null && subTris.Length > 0)
                {
                    allTris.AddRange(subTris);
                }
            }
            
            int[] triangles = allTris.ToArray();
            
            // Validate
            if (triangles == null || triangles.Length == 0)
                return IntPtr.Zero;
            
            if (triangles.Length % 3 != 0)
            {
                logger.LogError($"Mesh '{mesh.name}' has invalid triangle count: {triangles.Length}");
                return IntPtr.Zero;
            }
            
            // Validate indices
            for (int i = 0; i < triangles.Length; i++)
            {
                if (triangles[i] < 0 || triangles[i] >= vertices.Length)
                {
                    logger.LogError($"Mesh '{mesh.name}' has out-of-bounds index {triangles[i]}");
                    return IntPtr.Zero;
                }
            }
            
            // Ensure normals
            if (normals == null || normals.Length != vertices.Length)
            {
                normals = new Vector3[vertices.Length];
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = Vector3.up;
            }
            
            // Ensure UVs
            if (uvs == null || uvs.Length != vertices.Length)
            {
                uvs = new Vector2[vertices.Length];
            }
            
            // Convert to Remix vertices (Y-up to Z-up), applying _MainTex_ST tiling/offset
            Vector4 st = new Vector4(1, 1, 0, 0);
            if (material != null)
                st = materialManager.GetMainTexST(material.GetInstanceID());
            
            bool hasColors = colors != null && colors.Length == vertices.Length;
            var remixVerts = new RemixAPI.remixapi_HardcodedVertex[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                float u = uvs[i].x * st.x + st.z;
                float v = uvs[i].y * st.y + st.w;
                uint col = hasColors ? Color32ToBGRA(colors[i]) : 0xFFFFFFFF;
                remixVerts[i] = RemixAPI.MakeVertex(
                    vertices[i].x, vertices[i].z, vertices[i].y,
                    normals[i].x, normals[i].z, normals[i].y,
                    u, v,
                    col
                );
            }
            
            // Convert indices
            uint[] indices = new uint[triangles.Length];
            for (int i = 0; i < triangles.Length; i++)
                indices[i] = (uint)triangles[i];
            
            // Pin arrays
            GCHandle vertexHandle = GCHandle.Alloc(remixVerts, GCHandleType.Pinned);
            GCHandle indexHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
            
            try
            {
                // Get material handle
                int meshId = mesh.GetInstanceID();
                IntPtr materialHandle = IntPtr.Zero;
                
                if (material != null)
                {
                    int matId = material.GetInstanceID();
                    meshToMaterialMap[meshId] = matId;
                    
                    // Get or create material on-demand (called from render thread to avoid deadlocks)
                    materialHandle = materialManager.GetOrCreateMaterial(matId);
                }
                
                var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                {
                    vertices_values = vertexHandle.AddrOfPinnedObject(),
                    vertices_count = (ulong)remixVerts.Length,
                    indices_values = indexHandle.AddrOfPinnedObject(),
                    indices_count = (ulong)indices.Length,
                    skinning_hasvalue = 0,
                    skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                    material = materialHandle
                };
                
                GCHandle surfaceHandle = GCHandle.Alloc(surface, GCHandleType.Pinned);
                
                try
                {
                    ulong meshHash = GenerateMeshHash(mesh);
                    var meshInfo = new RemixAPI.remixapi_MeshInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                        pNext = IntPtr.Zero,
                        hash = meshHash,
                        surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                        surfaces_count = 1
                    };
                    
                    IntPtr handle;
                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock)
                    {
                        result = createMeshFunc(ref meshInfo, out handle);
                    }
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogError($"Failed to create mesh '{mesh.name}': {result}");
                        return IntPtr.Zero;
                    }
                    
                    meshCache[meshId] = handle;
                    logger.LogInfo($"Created mesh '{mesh.name}' with hash: 0x{meshHash:X16}");
                    
                    return handle;
                }
                finally
                {
                    surfaceHandle.Free();
                }
            }
            finally
            {
                vertexHandle.Free();
                indexHandle.Free();
            }
        }
        
        /// <summary>
        /// Create Remix mesh from skinned mesh data (for animated meshes)
        /// </summary>
        public IntPtr CreateRemixMeshFromData(
            int meshId, 
            Vector3[] vertices, 
            Vector3[] normals, 
            Vector2[] uvs, 
            int[] triangles, 
            int frameHash,
            int materialId = 0,
            Color32[] colors = null)
        {
            if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length == 0)
                return IntPtr.Zero;
            
            if (triangles.Length % 3 != 0)
            {
                if (skinnedRenderCount % 300 == 1)
                    logger.LogError($"Skinned mesh {meshId} has invalid triangle count: {triangles.Length}");
                return IntPtr.Zero;
            }
            
            // Validate indices
            for (int i = 0; i < triangles.Length; i++)
            {
                if (triangles[i] < 0 || triangles[i] >= vertices.Length)
                {
                    if (skinnedRenderCount % 300 == 1)
                        logger.LogError($"Skinned mesh {meshId} has out-of-bounds index");
                    return IntPtr.Zero;
                }
            }
            
            // Stable hash per skinned renderer — lets Remix track the mesh across frames
            ulong dynamicHash = (ulong)unchecked((uint)meshId);
            
            // Ensure normals
            if (normals == null || normals.Length != vertices.Length)
            {
                normals = new Vector3[vertices.Length];
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = Vector3.up;
            }
            
            // Ensure UVs
            if (uvs == null || uvs.Length != vertices.Length)
            {
                uvs = new Vector2[vertices.Length];
            }
            
            // Use pooled GCHandles
            if (!pinnedMeshPool.TryGetValue(meshId, out PinnedMeshData poolData))
            {
                poolData = new PinnedMeshData
                {
                    vertices = new RemixAPI.remixapi_HardcodedVertex[vertices.Length],
                    indices = new uint[triangles.Length],
                    isPinned = false,
                    vertexCapacity = vertices.Length,
                    indexCapacity = triangles.Length
                };
            }
            
            // Resize if needed
            if (poolData.vertexCapacity < vertices.Length)
            {
                if (poolData.isPinned)
                {
                    poolData.vertexHandle.Free();
                    if (poolData.indexCapacity > 0)
                        poolData.indexHandle.Free();
                    poolData.isPinned = false;
                }
                poolData.vertices = new RemixAPI.remixapi_HardcodedVertex[vertices.Length];
                poolData.vertexCapacity = vertices.Length;
            }
            
            if (poolData.indexCapacity < triangles.Length)
            {
                if (poolData.isPinned && poolData.indexCapacity > 0)
                {
                    poolData.indexHandle.Free();
                }
                poolData.indices = new uint[triangles.Length];
                poolData.indexCapacity = triangles.Length;
            }
            
            // Fill data (Y-up to Z-up conversion), applying _MainTex_ST tiling/offset
            Vector4 st = materialManager.GetMainTexST(materialId);
            bool hasColors = colors != null && colors.Length == vertices.Length;
            for (int i = 0; i < vertices.Length; i++)
            {
                float u = uvs[i].x * st.x + st.z;
                float v = uvs[i].y * st.y + st.w;
                uint col = hasColors ? Color32ToBGRA(colors[i]) : 0xFFFFFFFF;
                poolData.vertices[i] = RemixAPI.MakeVertex(
                    vertices[i].x, vertices[i].z, vertices[i].y,
                    normals[i].x, normals[i].z, normals[i].y,
                    u, v,
                    col
                );
            }
            
            for (int i = 0; i < triangles.Length; i++)
                poolData.indices[i] = (uint)triangles[i];
            
            // Pin once if not already pinned
            if (!poolData.isPinned)
            {
                poolData.vertexHandle = GCHandle.Alloc(poolData.vertices, GCHandleType.Pinned);
                poolData.indexHandle = GCHandle.Alloc(poolData.indices, GCHandleType.Pinned);
                poolData.isPinned = true;
                pinnedMeshPool[meshId] = poolData;
            }
            
            // Get or create material handle for skinned mesh (on render thread)
            IntPtr materialHandle = IntPtr.Zero;
            if (materialId != 0)
            {
                materialHandle = materialManager.GetOrCreateMaterial(materialId);
            }
            
            // Create mesh
            //logger.LogInfo($"Creating skinned mesh {meshId} with material: 0x{materialHandle.ToInt64():X}");
            
            var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
            {
                vertices_values = poolData.vertexHandle.AddrOfPinnedObject(),
                vertices_count = (ulong)vertices.Length,
                indices_values = poolData.indexHandle.AddrOfPinnedObject(),
                indices_count = (ulong)triangles.Length,
                skinning_hasvalue = 0,
                skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                material = materialHandle
            };
            
            GCHandle surfaceHandle = GCHandle.Alloc(surface, GCHandleType.Pinned);
            
            try
            {
                var meshInfo = new RemixAPI.remixapi_MeshInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                    pNext = IntPtr.Zero,
                    hash = dynamicHash,
                    surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                    surfaces_count = 1
                };
                
                IntPtr handle;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createMeshFunc(ref meshInfo, out handle);
                }
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    return IntPtr.Zero;
                }
                
                return handle;
            }
            finally
            {
                surfaceHandle.Free();
            }
        }
        
        /// <summary>
        /// Draw mesh instance with transform
        /// </summary>
        public void DrawMeshInstance(IntPtr meshHandle, Matrix4x4 localToWorld, uint objectPickingValue)
        {
            if (drawInstanceFunc == null || meshHandle == IntPtr.Zero)
                return;
            
            // Convert Unity Matrix4x4 to Remix transform (Y-up to Z-up)
            var m = localToWorld;
            var transform = RemixAPI.remixapi_Transform.FromMatrix(
                m.m00, m.m02, m.m01, m.m03,
                m.m20, m.m22, m.m21, m.m23,
                m.m10, m.m12, m.m11, m.m13
            );
            
            // Create ObjectPicking extension
            var objectPickingExt = new RemixAPI.remixapi_InstanceInfoObjectPickingEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_OBJECT_PICKING_EXT,
                pNext = IntPtr.Zero,
                objectPickingValue = objectPickingValue
            };
            
            GCHandle pickingHandle = GCHandle.Alloc(objectPickingExt, GCHandleType.Pinned);
            
            try
            {
                var instanceInfo = new RemixAPI.remixapi_InstanceInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                    pNext = pickingHandle.AddrOfPinnedObject(),
                    categoryFlags = 0,
                    mesh = meshHandle,
                    transform = transform,
                    doubleSided = 1
                };
                
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = drawInstanceFunc(ref instanceInfo);
                }
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogWarning($"DrawInstance failed for mesh 0x{meshHandle.ToInt64():X}: {result}");
                }
            }
            finally
            {
                pickingHandle.Free();
            }
        }
        
        /// <summary>
        /// Create a Remix mesh with GPU skinning data (bind-pose vertices + bone weights).
        /// Called once per unique sharedMesh. Returns mesh handle or IntPtr.Zero on failure.
        /// </summary>
        public IntPtr CreateSkinnedMeshWithBones(
            int meshId,
            Vector3[] vertices,
            Vector3[] normals,
            Vector2[] uvs,
            int[] triangles,
            float[] blendWeights,
            uint[] blendIndices,
            int bonesPerVertex,
            int materialId,
            Color32[] colors = null)
        {
            if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length == 0)
                return IntPtr.Zero;
            
            ulong meshHash = (ulong)unchecked((uint)meshId);
            
            if (normals == null || normals.Length != vertices.Length)
            {
                normals = new Vector3[vertices.Length];
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = Vector3.up;
            }
            if (uvs == null || uvs.Length != vertices.Length)
                uvs = new Vector2[vertices.Length];
            
            // Build vertex array (Y-up to Z-up), applying _MainTex_ST tiling/offset
            Vector4 stSkin = materialManager.GetMainTexST(materialId);
            bool hasColors = colors != null && colors.Length == vertices.Length;
            var remixVerts = new RemixAPI.remixapi_HardcodedVertex[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                float u = uvs[i].x * stSkin.x + stSkin.z;
                float v = uvs[i].y * stSkin.y + stSkin.w;
                uint col = hasColors ? Color32ToBGRA(colors[i]) : 0xFFFFFFFF;
                remixVerts[i] = RemixAPI.MakeVertex(
                    vertices[i].x, vertices[i].z, vertices[i].y,
                    normals[i].x, normals[i].z, normals[i].y,
                    u, v,
                    col
                );
            }
            
            var indices = new uint[triangles.Length];
            for (int i = 0; i < triangles.Length; i++)
                indices[i] = (uint)triangles[i];
            
            // Pin all arrays
            GCHandle vertHandle = GCHandle.Alloc(remixVerts, GCHandleType.Pinned);
            GCHandle idxHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
            GCHandle weightsHandle = GCHandle.Alloc(blendWeights, GCHandleType.Pinned);
            GCHandle indicesHandle = GCHandle.Alloc(blendIndices, GCHandleType.Pinned);
            
            try
            {
                IntPtr materialHandle = IntPtr.Zero;
                if (materialId != 0)
                    materialHandle = materialManager.GetOrCreateMaterial(materialId);
                
                var skinning = new RemixAPI.remixapi_MeshInfoSkinning
                {
                    bonesPerVertex = (uint)bonesPerVertex,
                    blendWeights_values = weightsHandle.AddrOfPinnedObject(),
                    blendWeights_count = (uint)blendWeights.Length,
                    blendIndices_values = indicesHandle.AddrOfPinnedObject(),
                    blendIndices_count = (uint)blendIndices.Length
                };
                
                var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                {
                    vertices_values = vertHandle.AddrOfPinnedObject(),
                    vertices_count = (ulong)vertices.Length,
                    indices_values = idxHandle.AddrOfPinnedObject(),
                    indices_count = (ulong)triangles.Length,
                    skinning_hasvalue = 1,
                    skinning_value = skinning,
                    material = materialHandle
                };
                
                GCHandle surfaceHandle = GCHandle.Alloc(surface, GCHandleType.Pinned);
                try
                {
                    var meshInfo = new RemixAPI.remixapi_MeshInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                        pNext = IntPtr.Zero,
                        hash = meshHash,
                        surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                        surfaces_count = 1
                    };
                    
                    IntPtr handle;
                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock)
                    {
                        result = createMeshFunc(ref meshInfo, out handle);
                    }
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogWarning($"CreateSkinnedMeshWithBones failed for {meshId}: {result}");
                        return IntPtr.Zero;
                    }
                    
                    return handle;
                }
                finally
                {
                    surfaceHandle.Free();
                }
            }
            finally
            {
                vertHandle.Free();
                idxHandle.Free();
                weightsHandle.Free();
                indicesHandle.Free();
            }
        }
        
        /// <summary>
        /// Draw a GPU-skinned mesh instance with bone transforms via pNext chain.
        /// </summary>
        public unsafe void DrawSkinnedInstance(IntPtr meshHandle, Matrix4x4 localToWorld, Matrix4x4[] boneTransforms, uint objectPickingValue)
        {
            if (drawInstanceFunc == null || meshHandle == IntPtr.Zero || boneTransforms == null)
                return;
            
            // Convert instance transform (Y-up to Z-up)
            var m = localToWorld;
            var transform = RemixAPI.remixapi_Transform.FromMatrix(
                m.m00, m.m02, m.m01, m.m03,
                m.m20, m.m22, m.m21, m.m23,
                m.m10, m.m12, m.m11, m.m13
            );
            
            // Convert bone transforms to Remix format (Y-up to Z-up, 3x4 matrices)
            int boneCount = boneTransforms.Length;
            var remixBones = new RemixAPI.remixapi_Transform[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var b = boneTransforms[i];
                remixBones[i] = RemixAPI.remixapi_Transform.FromMatrix(
                    b.m00, b.m02, b.m01, b.m03,
                    b.m20, b.m22, b.m21, b.m23,
                    b.m10, b.m12, b.m11, b.m13
                );
            }
            
            GCHandle bonesHandle = GCHandle.Alloc(remixBones, GCHandleType.Pinned);
            
            try
            {
                // Build pNext chain: InstanceInfo -> BoneTransforms -> ObjectPicking
                var objectPickingExt = new RemixAPI.remixapi_InstanceInfoObjectPickingEXT
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_OBJECT_PICKING_EXT,
                    pNext = IntPtr.Zero,
                    objectPickingValue = objectPickingValue
                };
                
                GCHandle pickingHandle = GCHandle.Alloc(objectPickingExt, GCHandleType.Pinned);
                
                try
                {
                    var boneExt = new RemixAPI.remixapi_InstanceInfoBoneTransformsEXT
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_BONE_TRANSFORMS_EXT,
                        pNext = pickingHandle.AddrOfPinnedObject(),
                        boneTransforms_values = bonesHandle.AddrOfPinnedObject(),
                        boneTransforms_count = (uint)boneCount
                    };
                    
                    GCHandle boneExtHandle = GCHandle.Alloc(boneExt, GCHandleType.Pinned);
                    
                    try
                    {
                        var instanceInfo = new RemixAPI.remixapi_InstanceInfo
                        {
                            sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                            pNext = boneExtHandle.AddrOfPinnedObject(),
                            categoryFlags = 0,
                            mesh = meshHandle,
                            transform = transform,
                            doubleSided = 1
                        };
                        
                        RemixAPI.remixapi_ErrorCode result;
                        lock (apiLock)
                        {
                            result = drawInstanceFunc(ref instanceInfo);
                        }
                        
                        if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                        {
                            logger.LogWarning($"DrawSkinnedInstance failed for mesh 0x{meshHandle.ToInt64():X}: {result}");
                        }
                    }
                    finally
                    {
                        boneExtHandle.Free();
                    }
                }
                finally
                {
                    pickingHandle.Free();
                }
            }
            finally
            {
                bonesHandle.Free();
            }
        }
        
        /// <summary>
        /// Manage skinned mesh handle lifecycle
        /// </summary>
        public void UpdateSkinnedMeshHandle(int meshId, IntPtr newHandle)
        {
            // Queue old handle for deferred destruction (prevents flickering)
            if (skinnedMeshHandles.TryGetValue(meshId, out IntPtr oldHandle) && oldHandle != IntPtr.Zero)
            {
                // Don't destroy immediately - queue it for later
                deferredDestroyQueue.Enqueue(oldHandle);
                
                // Process deferred destruction queue (destroy oldest handles)
                while (deferredDestroyQueue.Count > DEFERRED_DESTROY_FRAMES && destroyMeshFunc != null)
                {
                    IntPtr handleToDestroy = deferredDestroyQueue.Dequeue();
                    try
                    {
                        destroyMeshFunc(handleToDestroy);
                    }
                    catch { }
                }
            }
            
            skinnedMeshHandles[meshId] = newHandle;
            skinnedRenderCount++;
        }
        
        /// <summary>
        /// Clean up stale skinned mesh handles
        /// </summary>
        public void CleanupStaleSkinnedMeshes(HashSet<int> activeMeshIds)
        {
            if (destroyMeshFunc == null)
                return;
            
            List<int> toRemove = new List<int>();
            foreach (var kvp in skinnedMeshHandles)
            {
                if (!activeMeshIds.Contains(kvp.Key))
                {
                    destroyMeshFunc(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }
            
            foreach (int id in toRemove)
            {
                skinnedMeshHandles.Remove(id);
            }
        }
        
        /// <summary>
        /// Generate stable content-based hash for mesh
        /// </summary>
        public static ulong GenerateMeshHash(Mesh mesh)
        {
            ulong hash = 14695981039346656037UL; // FNV offset basis
            
            // Hash mesh name
            if (!string.IsNullOrEmpty(mesh.name))
            {
                foreach (char c in mesh.name)
                {
                    hash ^= c;
                    hash *= 1099511628211UL; // FNV prime
                }
            }
            
            // Hash vertex count and triangle count
            hash ^= (ulong)mesh.vertexCount;
            hash *= 1099511628211UL;
            hash ^= (ulong)mesh.triangles.Length;
            hash *= 1099511628211UL;
            
            // Hash first few vertices
            var vertices = mesh.vertices;
            int sampleCount = Math.Min(16, vertices.Length);
            for (int i = 0; i < sampleCount; i++)
            {
                hash ^= (ulong)BitConverter.DoubleToInt64Bits(vertices[i].x);
                hash *= 1099511628211UL;
                hash ^= (ulong)BitConverter.DoubleToInt64Bits(vertices[i].y);
                hash *= 1099511628211UL;
                hash ^= (ulong)BitConverter.DoubleToInt64Bits(vertices[i].z);
                hash *= 1099511628211UL;
            }
            
            return hash;
        }
        
        /// <summary>
        /// Create test triangle mesh
        /// </summary>
        public IntPtr CreateTestTriangle()
        {
            if (createMeshFunc == null)
                return IntPtr.Zero;
            
            RemixAPI.remixapi_HardcodedVertex[] vertices = new RemixAPI.remixapi_HardcodedVertex[3]
            {
                RemixAPI.MakeVertex( 5, -5, 10),
                RemixAPI.MakeVertex( 0,  5, 10),
                RemixAPI.MakeVertex(-5, -5, 10),
            };
            
            GCHandle vertexHandle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            
            try
            {
                var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                {
                    vertices_values = vertexHandle.AddrOfPinnedObject(),
                    vertices_count = (ulong)vertices.Length,
                    indices_values = IntPtr.Zero,
                    indices_count = 0,
                    skinning_hasvalue = 0,
                    skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                    material = IntPtr.Zero
                };
                
                GCHandle surfaceHandle = GCHandle.Alloc(surface, GCHandleType.Pinned);
                
                try
                {
                    var meshInfo = new RemixAPI.remixapi_MeshInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                        pNext = IntPtr.Zero,
                        hash = 0x1,
                        surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                        surfaces_count = 1
                    };
                    
                    IntPtr handle;
                    var result = createMeshFunc(ref meshInfo, out handle);
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogError($"Failed to create test mesh: {result}");
                        return IntPtr.Zero;
                    }
                    
                    logger.LogInfo($"Test triangle mesh created! Handle: {handle}");
                    return handle;
                }
                finally
                {
                    surfaceHandle.Free();
                }
            }
            finally
            {
                vertexHandle.Free();
            }
        }
        
        /// <summary>
        /// Expose CreateMesh delegate for external callers (e.g. SceneMeshScanner).
        /// </summary>
        public RemixAPI.PFN_remixapi_CreateMesh GetCreateMeshFunc() => createMeshFunc;
        
        /// <summary>
        /// Expose DrawInstance delegate for external callers (e.g. SceneMeshScanner).
        /// </summary>
        public RemixAPI.PFN_remixapi_DrawInstance GetDrawInstanceFunc() => drawInstanceFunc;
        
        /// <summary>
        /// Cleanup all resources
        /// </summary>
        public void Cleanup()
        {
            // Free pinned handles
            foreach (var poolData in pinnedMeshPool.Values)
            {
                if (poolData.isPinned)
                {
                    poolData.vertexHandle.Free();
                    if (poolData.indexCapacity > 0)
                        poolData.indexHandle.Free();
                }
            }
            pinnedMeshPool.Clear();
            loggedMaterialWarnings.Clear();
            
            // Destroy any remaining deferred handles
            if (destroyMeshFunc != null)
            {
                while (deferredDestroyQueue.Count > 0)
                {
                    IntPtr handle = deferredDestroyQueue.Dequeue();
                    try
                    {
                        destroyMeshFunc(handle);
                    }
                    catch { }
                }
            }
            
            // Note: Mesh cache handles are managed by Remix, don't destroy here
            meshCache.Clear();
            skinnedMeshHandles.Clear();
        }
    }
}
