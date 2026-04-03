using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityRemix
{
    /// <summary>
    /// Scans the Unity scene graph to find all static mesh geometry, including
    /// inactive GameObjects that the runtime capture misses. Rescans periodically
    /// after scene load to catch objects that arrive via async/Addressable loading.
    /// Streams mesh creation to the render thread to avoid frame hitches.
    /// </summary>
    public class SceneMeshScanner
    {
        private readonly ManualLogSource logger;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixMaterialManager materialManager;
        private readonly object apiLock;

        private struct SubMeshSurface
        {
            public int[] Indices;
            public int MaterialId;
        }

        private struct ScannedMeshData
        {
            public ulong MeshHash;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] UVs;
            public Color32[] Colors;
            public SubMeshSurface[] Surfaces;
            public Matrix4x4 LocalToWorld;
            public int Layer;
        }

        public struct InstanceData
        {
            public IntPtr MeshHandle;
            public RemixAPI.remixapi_Transform Transform;
            public int Layer;
        }

        // Streaming: main thread pushes extracted mesh data, render thread drains batches
        private readonly Queue<ScannedMeshData> streamingQueue = new Queue<ScannedMeshData>();
        private readonly object streamLock = new object();
        private volatile bool streamingActive;

        // Completed instances drawn every frame
        private readonly List<InstanceData> currentInstances = new List<InstanceData>();
        private readonly object instanceLock = new object();

        // Mesh handle dedup: same geometry → same Remix handle
        private readonly Dictionary<ulong, IntPtr> meshHandles = new Dictionary<ulong, IntPtr>();

        // Track which MeshFilter instance IDs we've already scanned (avoids duplicates across rescans)
        private readonly HashSet<int> scannedFilterIds = new HashSet<int>();



        // Rescan state: async-loaded objects appear after OnSceneLoaded
        private Scene activeScene;
        private float timeSinceSceneLoad;
        private float rescanTimer;
        private const float RescanInterval = 1.0f;
        private const float RescanDuration = 30.0f;

        private const int MeshesPerFrame = 32;

        public bool HasData
        {
            get { lock (instanceLock) { return currentInstances.Count > 0; } }
        }

        public bool IsStreaming => streamingActive;

        /// <summary>
        /// Returns the set of layer indices that have scanned instances.
        /// </summary>
        public Dictionary<int, int> GetLayerCounts()
        {
            var counts = new Dictionary<int, int>();
            lock (instanceLock)
            {
                foreach (var inst in currentInstances)
                {
                    if (!counts.ContainsKey(inst.Layer))
                        counts[inst.Layer] = 0;
                    counts[inst.Layer]++;
                }
            }
            return counts;
        }

        public SceneMeshScanner(
            ManualLogSource logger,
            RemixMeshConverter meshConverter,
            RemixMaterialManager materialManager,
            object apiLock)
        {
            this.logger = logger;
            this.meshConverter = meshConverter;
            this.materialManager = materialManager;
            this.apiLock = apiLock;
        }

        /// <summary>
        /// Must be called on the main thread. Performs the initial scan and starts
        /// periodic rescanning to catch async-loaded objects.
        /// </summary>
        public void OnSceneLoaded(Scene scene)
        {
            if (!scene.IsValid())
                return;

            scannedFilterIds.Clear();
            activeScene = scene;
            timeSinceSceneLoad = 0f;
            rescanTimer = 0f;

            int queued = ScanScene(scene, logDiagnostics: true);
            if (queued > 0)
            {
                streamingActive = true;
                logger.LogInfo($"Scene scan queued {queued} meshes for streaming (rescanning for {RescanDuration}s to catch async-loaded objects)");
            }
            else
            {
                logger.LogInfo($"Scene scan found no meshes yet in '{scene.name}' (will rescan for {RescanDuration}s)");
            }
        }

        /// <summary>
        /// Must be called on the main thread each frame (e.g. from plugin Update).
        /// Periodically rescans to pick up objects that loaded asynchronously.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!activeScene.IsValid())
                return;

            timeSinceSceneLoad += deltaTime;
            if (timeSinceSceneLoad > RescanDuration)
            {
                activeScene = default;
                return;
            }

            rescanTimer += deltaTime;
            if (rescanTimer < RescanInterval)
                return;
            rescanTimer = 0f;

            int queued = ScanScene(activeScene, logDiagnostics: false);
            if (queued > 0)
            {
                streamingActive = true;
                logger.LogInfo($"Rescan found {queued} new meshes ({timeSinceSceneLoad:F0}s after scene load)");
            }
        }

        /// <summary>
        /// Called on the render thread each frame. Drains a batch from the queue,
        /// creates Remix meshes, and returns all instances accumulated so far.
        /// </summary>
        public InstanceData[] GetInstances()
        {
            DrainStreamingBatch();

            lock (instanceLock)
            {
                return currentInstances.Count > 0 ? currentInstances.ToArray() : null;
            }
        }

        public void ClearData()
        {
            lock (instanceLock) { currentInstances.Clear(); }
            lock (streamLock) { streamingQueue.Clear(); }
            meshHandles.Clear();
            scannedFilterIds.Clear();
            activeScene = default;
        }

        private int ScanScene(Scene scene, bool logDiagnostics)
        {
            var filters = Resources.FindObjectsOfTypeAll<MeshFilter>();
            var combinedDataCache = new Dictionary<int, (Vector3[] verts, Vector3[] norms, Vector2[] uvs, Color32[] cols, int[][] subIndices)>();
            int queued = 0;
            int skippedAlreadyScanned = 0, skippedWrongScene = 0, skippedNoRenderer = 0;
            int skippedNoMesh = 0, skippedNoVerts = 0, skippedNoTris = 0, skippedReadError = 0;
            int gpuReadbackCount = 0;
            int vertexColorCount = 0;

            foreach (var filter in filters)
            {
                if (filter == null)
                    continue;

                int filterId = filter.GetInstanceID();
                if (scannedFilterIds.Contains(filterId))
                {
                    skippedAlreadyScanned++;
                    continue;
                }

                if (filter.gameObject.scene != scene)
                {
                    skippedWrongScene++;
                    continue;
                }

                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null)
                {
                    skippedNoRenderer++;
                    scannedFilterIds.Add(filterId);
                    continue;
                }

                var mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    skippedNoMesh++;
                    scannedFilterIds.Add(filterId);
                    continue;
                }

                // Unity's static batching merges renderers into a single "Combined Mesh" with
                // pre-transformed world-space vertices. Each renderer owns a slice of submeshes
                // at [subMeshStartIndex .. subMeshStartIndex + sharedMaterials.Length).
                bool isCombinedMesh = mesh.name != null && mesh.name.StartsWith("Combined Mesh");

                // Mark scanned before extraction — even if geometry is empty we won't retry
                scannedFilterIds.Add(filterId);

                Vector3[] vertices = null;
                Vector3[] normals = null;
                Vector2[] uvs = null;
                Color32[] colors = null;
                int[][] subMeshIndices = null;

                // For combined meshes, reuse cached vertex/index data across renderers
                if (isCombinedMesh)
                {
                    int meshId = mesh.GetInstanceID();
                    if (combinedDataCache.TryGetValue(meshId, out var cached))
                    {
                        vertices = cached.verts;
                        normals = cached.norms;
                        uvs = cached.uvs;
                        colors = cached.cols;
                        subMeshIndices = cached.subIndices;
                    }
                }

                if (vertices == null)
                {
                    try
                    {
                        vertices = mesh.vertices;
                    }
                    catch { }

                    if (vertices != null && vertices.Length > 0)
                    {
                        // Fast path: CPU mesh data available
                        normals = mesh.normals;
                        uvs = mesh.uv;
                        colors = mesh.colors32;
                        if (colors != null && colors.Length == 0) colors = null;
                    }
                    else if (mesh.vertexCount > 0)
                    {
                        // GPU readback path: vertex data is GPU-only
                        try
                        {
                            if (ReadMeshFromGPU(mesh, out vertices, out normals, out uvs, out subMeshIndices))
                                gpuReadbackCount++;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning($"GPU readback failed for mesh '{mesh.name}': {ex.Message}");
                            skippedReadError++;
                            continue;
                        }
                    }

                    if (isCombinedMesh && vertices != null && vertices.Length > 0)
                        combinedDataCache[mesh.GetInstanceID()] = (vertices, normals, uvs, colors, subMeshIndices);
                }

                if (vertices == null || vertices.Length == 0)
                {
                    skippedNoVerts++;
                    continue;
                }

                // Build per-submesh surfaces with separate materials
                var materials = renderer.sharedMaterials;
                var surfaces = new List<SubMeshSurface>();

                // For combined meshes, each renderer only owns a slice of submeshes
                int subStart = 0;
                int subEnd = mesh.subMeshCount;
                if (isCombinedMesh)
                {
                    subStart = renderer.subMeshStartIndex;
                    subEnd = Math.Min(subStart + (materials != null ? materials.Length : 0), mesh.subMeshCount);
                    if (subStart >= subEnd)
                        continue;
                }

                if (subMeshIndices == null)
                {
                    // CPU path — extract per-submesh indices for this renderer's range
                    var subList = new List<int[]>();
                    for (int sub = subStart; sub < subEnd; sub++)
                    {
                        if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                        {
                            subList.Add(null);
                            continue;
                        }
                        var tris = mesh.GetTriangles(sub);
                        subList.Add(tris != null && tris.Length > 0 ? tris : null);
                    }
                    subMeshIndices = subList.ToArray();
                }
                else if (isCombinedMesh)
                {
                    // GPU readback / cache returned all submeshes — extract only this renderer's range
                    int rangeCount = Math.Min(subEnd, subMeshIndices.Length) - subStart;
                    if (rangeCount > 0)
                    {
                        var subset = new int[rangeCount][];
                        Array.Copy(subMeshIndices, subStart, subset, 0, rangeCount);
                        subMeshIndices = subset;
                    }
                }

                for (int sub = 0; sub < subMeshIndices.Length; sub++)
                {
                    var tris = subMeshIndices[sub];
                    if (tris == null || tris.Length == 0)
                        continue;

                    int matId = 0;
                    if (materials != null && sub < materials.Length && materials[sub] != null)
                    {
                        matId = materials[sub].GetInstanceID();
                        materialManager.CaptureMaterialTextures(materials[sub], matId);
                    }
                    surfaces.Add(new SubMeshSurface { Indices = tris, MaterialId = matId });
                }

                // For combined meshes, compact vertex arrays so each Remix mesh only
                // includes the vertices referenced by this renderer's submeshes
                if (isCombinedMesh && surfaces.Count > 0)
                    CompactMeshData(ref vertices, ref normals, ref uvs, ref colors, surfaces);

                if (surfaces.Count == 0)
                {
                    skippedNoTris++;
                    continue;
                }

                // Validate indices across all surfaces
                bool valid = true;
                foreach (var surf in surfaces)
                {
                    if (surf.Indices.Length % 3 != 0) { valid = false; break; }
                    for (int i = 0; i < surf.Indices.Length; i++)
                    {
                        if (surf.Indices[i] < 0 || surf.Indices[i] >= vertices.Length)
                        { valid = false; break; }
                    }
                    if (!valid) break;
                }
                if (!valid)
                    continue;

                // Use filter instance ID in hash so each object gets its own Remix mesh + material binding
                ulong meshHash = GenerateInstanceMeshHash(mesh, filterId);

                if (colors != null && colors.Length > 0)
                    vertexColorCount++;

                lock (streamLock)
                {
                    streamingQueue.Enqueue(new ScannedMeshData
                    {
                        MeshHash = meshHash,
                        Vertices = vertices,
                        Normals = normals,
                        UVs = uvs,
                        Colors = colors,
                        Surfaces = surfaces.ToArray(),
                        LocalToWorld = isCombinedMesh ? Matrix4x4.identity : filter.transform.localToWorldMatrix,
                        Layer = filter.gameObject.layer,
                    });
                }
                queued++;
            }

            if (logDiagnostics || queued > 0)
            {
                logger.LogInfo($"Scene scan '{scene.name}': {filters.Length} total MeshFilters, {queued} queued ({gpuReadbackCount} via GPU readback, {vertexColorCount} with vertex colors)" +
                    $" | skipped: {skippedWrongScene} wrong scene, {skippedAlreadyScanned} already scanned," +
                    $" {skippedNoRenderer} no renderer, {skippedNoMesh} no mesh," +
                    $" {skippedReadError} read error, {skippedNoVerts} no verts, {skippedNoTris} no tris");
            }

            return queued;
        }

        private void DrainStreamingBatch()
        {
            ScannedMeshData[] batch;
            lock (streamLock)
            {
                int count = Math.Min(MeshesPerFrame, streamingQueue.Count);
                if (count == 0)
                {
                    if (streamingActive)
                    {
                        streamingActive = false;
                        logger.LogInfo($"Streaming complete: {currentInstances.Count} scene mesh instances loaded");
                    }
                    return;
                }
                batch = new ScannedMeshData[count];
                for (int i = 0; i < count; i++)
                    batch[i] = streamingQueue.Dequeue();
            }

            var newInstances = new List<InstanceData>(batch.Length);

            foreach (var entry in batch)
            {
                IntPtr meshHandle = CreateRemixMesh(entry);
                if (meshHandle == IntPtr.Zero)
                    continue;

                // Convert Unity Matrix4x4 to Remix transform (Y-up to Z-up)
                var m = entry.LocalToWorld;
                var transform = RemixAPI.remixapi_Transform.FromMatrix(
                    m.m00, m.m02, m.m01, m.m03,
                    m.m20, m.m22, m.m21, m.m23,
                    m.m10, m.m12, m.m11, m.m13
                );

                newInstances.Add(new InstanceData
                {
                    MeshHandle = meshHandle,
                    Transform = transform,
                    Layer = entry.Layer
                });
            }

            if (newInstances.Count > 0)
            {
                lock (instanceLock)
                {
                    currentInstances.AddRange(newInstances);
                }
            }
        }

        private IntPtr CreateRemixMesh(ScannedMeshData data)
        {
            if (meshHandles.TryGetValue(data.MeshHash, out IntPtr existing))
                return existing;

            var verts = data.Vertices;
            var norms = data.Normals;
            var uvs = data.UVs;
            var cols = data.Colors;
            bool hasColors = cols != null && cols.Length == verts.Length;

            if (norms == null || norms.Length != verts.Length)
            {
                norms = new Vector3[verts.Length];
                for (int i = 0; i < norms.Length; i++)
                    norms[i] = Vector3.up;
            }

            if (uvs == null || uvs.Length != verts.Length)
                uvs = new Vector2[verts.Length];

            // Check if any surface needs UV tiling/offset applied
            bool anyNonIdentityST = false;
            for (int s = 0; s < data.Surfaces.Length; s++)
            {
                var st = materialManager.GetMainTexST(data.Surfaces[s].MaterialId);
                if (st.x != 1f || st.y != 1f || st.z != 0f || st.w != 0f)
                { anyNonIdentityST = true; break; }
            }

            var vertexHandles = new List<GCHandle>();
            var indexHandles = new List<GCHandle>();
            var surfaceHandles = new List<GCHandle>();

            try
            {
                RemixAPI.remixapi_HardcodedVertex[] sharedRemixVerts = null;
                GCHandle sharedVertexHandle = default;

                // If no tiling needed, build shared vertex buffer once (common fast path)
                if (!anyNonIdentityST)
                {
                    sharedRemixVerts = new RemixAPI.remixapi_HardcodedVertex[verts.Length];
                    for (int i = 0; i < verts.Length; i++)
                    {
                        uint col = hasColors ? RemixMeshConverter.Color32ToBGRA(cols[i]) : 0xFFFFFFFF;
                        sharedRemixVerts[i] = RemixAPI.MakeVertex(
                            verts[i].x, verts[i].z, verts[i].y,
                            norms[i].x, norms[i].z, norms[i].y,
                            uvs[i].x, uvs[i].y,
                            col
                        );
                    }
                    sharedVertexHandle = GCHandle.Alloc(sharedRemixVerts, GCHandleType.Pinned);
                    vertexHandles.Add(sharedVertexHandle);
                }

                // Build one surface per submesh, each with its own material
                var surfaces = new RemixAPI.remixapi_MeshInfoSurfaceTriangles[data.Surfaces.Length];
                for (int s = 0; s < data.Surfaces.Length; s++)
                {
                    var surf = data.Surfaces[s];
                    uint[] surfIndices = new uint[surf.Indices.Length];
                    for (int i = 0; i < surf.Indices.Length; i++)
                        surfIndices[i] = (uint)surf.Indices[i];

                    GCHandle idxHandle = GCHandle.Alloc(surfIndices, GCHandleType.Pinned);
                    indexHandles.Add(idxHandle);

                    IntPtr materialHandle = IntPtr.Zero;
                    if (surf.MaterialId != 0)
                        materialHandle = materialManager.GetOrCreateMaterial(surf.MaterialId);

                    IntPtr vertsPtr;
                    ulong vertsCount;

                    if (anyNonIdentityST)
                    {
                        // Per-surface vertex buffer with _MainTex_ST applied
                        var st = materialManager.GetMainTexST(surf.MaterialId);
                        var surfVerts = new RemixAPI.remixapi_HardcodedVertex[verts.Length];
                        for (int i = 0; i < verts.Length; i++)
                        {
                            float u = uvs[i].x * st.x + st.z;
                            float v = uvs[i].y * st.y + st.w;
                            uint col = hasColors ? RemixMeshConverter.Color32ToBGRA(cols[i]) : 0xFFFFFFFF;
                            surfVerts[i] = RemixAPI.MakeVertex(
                                verts[i].x, verts[i].z, verts[i].y,
                                norms[i].x, norms[i].z, norms[i].y,
                                u, v,
                                col
                            );
                        }
                        var vHandle = GCHandle.Alloc(surfVerts, GCHandleType.Pinned);
                        vertexHandles.Add(vHandle);
                        vertsPtr = vHandle.AddrOfPinnedObject();
                        vertsCount = (ulong)surfVerts.Length;
                    }
                    else
                    {
                        vertsPtr = sharedVertexHandle.AddrOfPinnedObject();
                        vertsCount = (ulong)sharedRemixVerts.Length;
                    }

                    surfaces[s] = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                    {
                        vertices_values = vertsPtr,
                        vertices_count = vertsCount,
                        indices_values = idxHandle.AddrOfPinnedObject(),
                        indices_count = (ulong)surfIndices.Length,
                        skinning_hasvalue = 0,
                        skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                        material = materialHandle
                    };
                }

                GCHandle surfaceArrayHandle = GCHandle.Alloc(surfaces, GCHandleType.Pinned);
                surfaceHandles.Add(surfaceArrayHandle);

                var meshInfo = new RemixAPI.remixapi_MeshInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                    pNext = IntPtr.Zero,
                    hash = data.MeshHash,
                    surfaces_values = surfaceArrayHandle.AddrOfPinnedObject(),
                    surfaces_count = (uint)surfaces.Length
                };

                IntPtr handle;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    var createFunc = meshConverter.GetCreateMeshFunc();
                    if (createFunc == null)
                        return IntPtr.Zero;
                    result = createFunc(ref meshInfo, out handle);
                }

                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogError($"Failed to create scanned mesh 0x{data.MeshHash:X16}: {result}");
                    return IntPtr.Zero;
                }

                meshHandles[data.MeshHash] = handle;
                return handle;
            }
            finally
            {
                foreach (var h in vertexHandles) h.Free();
                foreach (var h in indexHandles) h.Free();
                foreach (var h in surfaceHandles) h.Free();
            }
        }

        private static ulong GenerateInstanceMeshHash(Mesh mesh, int instanceId)
        {
            ulong hash = 14695981039346656037UL; // FNV offset basis

            // Include instance ID so each object gets a unique Remix mesh
            hash ^= (ulong)(uint)instanceId;
            hash *= 1099511628211UL;

            if (!string.IsNullOrEmpty(mesh.name))
            {
                foreach (char c in mesh.name)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
            }

            hash ^= (ulong)mesh.vertexCount;
            hash *= 1099511628211UL;

            return hash;
        }

        /// <summary>
        /// Compacts vertex/normal/UV arrays to only include vertices referenced by the
        /// given surfaces, and remaps all surface indices accordingly. Used for combined
        /// meshes where each renderer uses a small slice of a large shared vertex buffer.
        /// </summary>
        private static void CompactMeshData(
            ref Vector3[] vertices, ref Vector3[] normals, ref Vector2[] uvs,
            ref Color32[] colors, List<SubMeshSurface> surfaces)
        {
            var usedSet = new HashSet<int>();
            foreach (var surf in surfaces)
                foreach (int idx in surf.Indices)
                    usedSet.Add(idx);

            if (usedSet.Count == vertices.Length)
                return;

            var sorted = new int[usedSet.Count];
            usedSet.CopyTo(sorted);
            Array.Sort(sorted);

            var remap = new Dictionary<int, int>(sorted.Length);
            for (int i = 0; i < sorted.Length; i++)
                remap[sorted[i]] = i;

            var newVerts = new Vector3[sorted.Length];
            var newNorms = normals != null && normals.Length == vertices.Length
                ? new Vector3[sorted.Length] : null;
            var newUvs = uvs != null && uvs.Length == vertices.Length
                ? new Vector2[sorted.Length] : null;
            var newCols = colors != null && colors.Length == vertices.Length
                ? new Color32[sorted.Length] : null;

            for (int i = 0; i < sorted.Length; i++)
            {
                int old = sorted[i];
                newVerts[i] = vertices[old];
                if (newNorms != null) newNorms[i] = normals[old];
                if (newUvs != null) newUvs[i] = uvs[old];
                if (newCols != null) newCols[i] = colors[old];
            }

            foreach (var surf in surfaces)
                for (int i = 0; i < surf.Indices.Length; i++)
                    surf.Indices[i] = remap[surf.Indices[i]];

            vertices = newVerts;
            normals = newNorms;
            uvs = newUvs;
            colors = newCols;
        }

        /// <summary>
        /// Reads vertex data from GPU buffers for meshes where CPU data is unavailable.
        /// Uses Mesh.GetVertexBuffer/GetIndexBuffer which bypass the isReadable check.
        /// </summary>
        private bool ReadMeshFromGPU(Mesh mesh, out Vector3[] positions, out Vector3[] normals, out Vector2[] uvs, out int[][] subMeshIndices)
        {
            positions = null;
            normals = null;
            uvs = null;
            subMeshIndices = null;

            int vertexCount = mesh.vertexCount;
            if (vertexCount == 0)
                return false;

            // Get vertex layout
            var attributes = mesh.GetVertexAttributes();
            int stride = mesh.GetVertexBufferStride(0);

            int posOffset = -1, posStream = -1;
            int normOffset = -1, normStream = -1;
            int uvOffset = -1, uvStream = -1;
            VertexAttributeFormat posFormat = VertexAttributeFormat.Float32;
            VertexAttributeFormat normFormat = VertexAttributeFormat.Float32;
            VertexAttributeFormat uvFormat = VertexAttributeFormat.Float32;

            foreach (var attr in attributes)
            {
                switch (attr.attribute)
                {
                    case VertexAttribute.Position:
                        posOffset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
                        posStream = attr.stream;
                        posFormat = attr.format;
                        break;
                    case VertexAttribute.Normal:
                        normOffset = mesh.GetVertexAttributeOffset(VertexAttribute.Normal);
                        normStream = attr.stream;
                        normFormat = attr.format;
                        break;
                    case VertexAttribute.TexCoord0:
                        uvOffset = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
                        uvStream = attr.stream;
                        uvFormat = attr.format;
                        break;
                }
            }

            if (posOffset < 0 || posStream != 0)
                return false;

            // Read vertex buffer from GPU
            GraphicsBuffer vertexBuffer = mesh.GetVertexBuffer(0);
            if (vertexBuffer == null)
                return false;

            byte[] rawVerts;
            try
            {
                rawVerts = new byte[vertexBuffer.count * vertexBuffer.stride];
                vertexBuffer.GetData(rawVerts);
            }
            finally
            {
                vertexBuffer.Dispose();
            }

            // Parse positions
            positions = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int baseOff = i * stride + posOffset;
                positions[i] = ReadVector3(rawVerts, baseOff, posFormat);
            }

            // Parse normals
            if (normOffset >= 0 && normStream == 0)
            {
                normals = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    int baseOff = i * stride + normOffset;
                    normals[i] = ReadVector3(rawVerts, baseOff, normFormat);
                }
            }

            // Parse UVs
            if (uvOffset >= 0 && uvStream == 0)
            {
                uvs = new Vector2[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    int baseOff = i * stride + uvOffset;
                    uvs[i] = ReadVector2(rawVerts, baseOff, uvFormat);
                }
            }

            // Read index buffer from GPU, split by submesh
            GraphicsBuffer indexBuffer = mesh.GetIndexBuffer();
            if (indexBuffer == null)
                return false;

            int totalIndices = 0;
            try
            {
                bool is32Bit = mesh.indexFormat == IndexFormat.UInt32;

                // Read the full index buffer once
                int[] intBuf = null;
                ushort[] shortBuf = null;
                if (is32Bit)
                {
                    intBuf = new int[indexBuffer.count];
                    indexBuffer.GetData(intBuf);
                }
                else
                {
                    shortBuf = new ushort[indexBuffer.count];
                    indexBuffer.GetData(shortBuf);
                }

                // Split into per-submesh arrays
                var subList = new List<int[]>();
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                    {
                        subList.Add(null);
                        continue;
                    }
                    var desc = mesh.GetSubMesh(sub);
                    int start = desc.indexStart;
                    int count = desc.indexCount;
                    var tris = new int[count];

                    if (is32Bit)
                    {
                        for (int i = 0; i < count; i++)
                            tris[i] = intBuf[start + i];
                    }
                    else
                    {
                        for (int i = 0; i < count; i++)
                            tris[i] = shortBuf[start + i];
                    }
                    subList.Add(tris);
                    totalIndices += count;
                }
                subMeshIndices = subList.ToArray();
            }
            finally
            {
                indexBuffer.Dispose();
            }

            logger.LogInfo($"GPU readback: mesh '{mesh.name}' — {vertexCount} verts, {totalIndices} indices, {mesh.subMeshCount} submeshes");
            return positions.Length > 0 && totalIndices > 0;
        }

        private static Vector3 ReadVector3(byte[] data, int offset, VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                    return new Vector3(
                        BitConverter.ToSingle(data, offset),
                        BitConverter.ToSingle(data, offset + 4),
                        BitConverter.ToSingle(data, offset + 8));
                case VertexAttributeFormat.Float16:
                    return new Vector3(
                        HalfToFloat(BitConverter.ToUInt16(data, offset)),
                        HalfToFloat(BitConverter.ToUInt16(data, offset + 2)),
                        HalfToFloat(BitConverter.ToUInt16(data, offset + 4)));
                default:
                    return Vector3.zero;
            }
        }

        private static Vector2 ReadVector2(byte[] data, int offset, VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                    return new Vector2(
                        BitConverter.ToSingle(data, offset),
                        BitConverter.ToSingle(data, offset + 4));
                case VertexAttributeFormat.Float16:
                    return new Vector2(
                        HalfToFloat(BitConverter.ToUInt16(data, offset)),
                        HalfToFloat(BitConverter.ToUInt16(data, offset + 2)));
                default:
                    return Vector2.zero;
            }
        }

        private static float HalfToFloat(ushort half)
        {
            int sign = (half >> 15) & 1;
            int exp = (half >> 10) & 0x1F;
            int mantissa = half & 0x3FF;

            if (exp == 0)
            {
                if (mantissa == 0) return sign == 1 ? -0f : 0f;
                // Denormalized
                float val = mantissa / 1024f * (1f / 16384f);
                return sign == 1 ? -val : val;
            }
            if (exp == 31)
            {
                return mantissa == 0 ? (sign == 1 ? float.NegativeInfinity : float.PositiveInfinity) : float.NaN;
            }

            float result = (float)Math.Pow(2, exp - 15) * (1f + mantissa / 1024f);
            return sign == 1 ? -result : result;
        }
    }
}
