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

        private struct ScannedMeshData
        {
            public ulong MeshHash;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] UVs;
            public int[] Indices;
            public Matrix4x4 LocalToWorld;
            public int MaterialId;
        }

        public struct InstanceData
        {
            public IntPtr MeshHandle;
            public RemixAPI.remixapi_Transform Transform;
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

        // Track combined meshes (static batching) already queued — their vertices are pre-transformed
        // to world space, so we use identity transform and only queue one instance per combined mesh.
        private readonly HashSet<int> queuedCombinedMeshIds = new HashSet<int>();

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
            queuedCombinedMeshIds.Clear();
            activeScene = default;
        }

        private int ScanScene(Scene scene, bool logDiagnostics)
        {
            var filters = Resources.FindObjectsOfTypeAll<MeshFilter>();
            int queued = 0;
            int skippedAlreadyScanned = 0, skippedWrongScene = 0, skippedNoRenderer = 0;
            int skippedNoMesh = 0, skippedNoVerts = 0, skippedNoTris = 0, skippedReadError = 0;
            int gpuReadbackCount = 0;

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

                // Unity's static batching replaces individual meshes with a single "Combined Mesh"
                // whose vertices are already in world space. Only queue one instance (identity transform).
                bool isCombinedMesh = mesh.name != null && mesh.name.StartsWith("Combined Mesh");
                if (isCombinedMesh)
                {
                    int meshObjId = mesh.GetInstanceID();
                    if (queuedCombinedMeshIds.Contains(meshObjId))
                    {
                        scannedFilterIds.Add(filterId);
                        continue;
                    }
                    queuedCombinedMeshIds.Add(meshObjId);
                }

                // Mark scanned before extraction — even if geometry is empty we won't retry
                scannedFilterIds.Add(filterId);

                Vector3[] vertices = null;
                Vector3[] normals = null;
                Vector2[] uvs = null;
                int[] indices = null;

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
                    indices = ExtractTriangleIndices(mesh);
                }
                else if (mesh.vertexCount > 0)
                {
                    // GPU readback path: vertex data is GPU-only
                    try
                    {
                        if (ReadMeshFromGPU(mesh, out vertices, out normals, out uvs, out indices))
                            gpuReadbackCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"GPU readback failed for mesh '{mesh.name}': {ex.Message}");
                        skippedReadError++;
                        continue;
                    }
                }

                if (vertices == null || vertices.Length == 0)
                {
                    skippedNoVerts++;
                    continue;
                }

                if (indices == null || indices.Length == 0 || indices.Length % 3 != 0)
                {
                    skippedNoTris++;
                    continue;
                }

                // Validate indices
                bool valid = true;
                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] < 0 || indices[i] >= vertices.Length)
                    {
                        valid = false;
                        break;
                    }
                }
                if (!valid)
                    continue;

                // Capture material textures on main thread (accesses Unity textures/GPU)
                int materialId = 0;
                var material = renderer.sharedMaterial;
                if (material != null)
                {
                    materialId = material.GetInstanceID();
                    materialManager.CaptureMaterialTextures(material, materialId);
                }

                ulong meshHash = RemixMeshConverter.GenerateMeshHash(mesh);

                lock (streamLock)
                {
                    streamingQueue.Enqueue(new ScannedMeshData
                    {
                        MeshHash = meshHash,
                        Vertices = vertices,
                        Normals = normals,
                        UVs = uvs,
                        Indices = indices,
                        LocalToWorld = isCombinedMesh ? Matrix4x4.identity : filter.transform.localToWorldMatrix,
                        MaterialId = materialId
                    });
                }
                queued++;
            }

            if (logDiagnostics || queued > 0)
            {
                logger.LogInfo($"Scene scan '{scene.name}': {filters.Length} total MeshFilters, {queued} queued ({gpuReadbackCount} via GPU readback)" +
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
                    Transform = transform
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

            if (norms == null || norms.Length != verts.Length)
            {
                norms = new Vector3[verts.Length];
                for (int i = 0; i < norms.Length; i++)
                    norms[i] = Vector3.up;
            }

            if (uvs == null || uvs.Length != verts.Length)
                uvs = new Vector2[verts.Length];

            // Convert to Remix vertices (Y-up to Z-up: swap Y and Z)
            var remixVerts = new RemixAPI.remixapi_HardcodedVertex[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                remixVerts[i] = RemixAPI.MakeVertex(
                    verts[i].x, verts[i].z, verts[i].y,
                    norms[i].x, norms[i].z, norms[i].y,
                    uvs[i].x, uvs[i].y,
                    0xFFFFFFFF
                );
            }

            uint[] indices = new uint[data.Indices.Length];
            for (int i = 0; i < data.Indices.Length; i++)
                indices[i] = (uint)data.Indices[i];

            GCHandle vertexHandle = GCHandle.Alloc(remixVerts, GCHandleType.Pinned);
            GCHandle indexHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);

            try
            {
                IntPtr materialHandle = IntPtr.Zero;
                if (data.MaterialId != 0)
                    materialHandle = materialManager.GetOrCreateMaterial(data.MaterialId);

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
                    var meshInfo = new RemixAPI.remixapi_MeshInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                        pNext = IntPtr.Zero,
                        hash = data.MeshHash,
                        surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                        surfaces_count = 1
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
                    surfaceHandle.Free();
                }
            }
            finally
            {
                vertexHandle.Free();
                indexHandle.Free();
            }
        }

        private static int[] ExtractTriangleIndices(Mesh mesh)
        {
            var allIndices = new List<int>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                if (mesh.GetTopology(i) != MeshTopology.Triangles)
                    continue;
                var tris = mesh.GetTriangles(i);
                if (tris != null && tris.Length > 0)
                    allIndices.AddRange(tris);
            }
            return allIndices.ToArray();
        }

        /// <summary>
        /// Reads vertex data from GPU buffers for meshes where CPU data is unavailable.
        /// Uses Mesh.GetVertexBuffer/GetIndexBuffer which bypass the isReadable check.
        /// </summary>
        private bool ReadMeshFromGPU(Mesh mesh, out Vector3[] positions, out Vector3[] normals, out Vector2[] uvs, out int[] indices)
        {
            positions = null;
            normals = null;
            uvs = null;
            indices = null;

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

            // Read index buffer from GPU
            GraphicsBuffer indexBuffer = mesh.GetIndexBuffer();
            if (indexBuffer == null)
                return false;

            try
            {
                bool is32Bit = mesh.indexFormat == IndexFormat.UInt32;
                int indexCount = (int)mesh.GetIndexCount(0);

                // Collect indices from all triangle submeshes
                var allIndices = new List<int>();
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                        continue;
                    var desc = mesh.GetSubMesh(sub);
                    int start = desc.indexStart;
                    int count = desc.indexCount;

                    if (is32Bit)
                    {
                        var buf = new int[indexBuffer.count];
                        indexBuffer.GetData(buf);
                        for (int i = start; i < start + count; i++)
                            allIndices.Add(buf[i]);
                    }
                    else
                    {
                        var buf = new ushort[indexBuffer.count];
                        indexBuffer.GetData(buf);
                        for (int i = start; i < start + count; i++)
                            allIndices.Add(buf[i]);
                    }
                }
                indices = allIndices.ToArray();
            }
            finally
            {
                indexBuffer.Dispose();
            }

            logger.LogInfo($"GPU readback: mesh '{mesh.name}' — {vertexCount} verts, {indices.Length} indices");
            return positions.Length > 0 && indices.Length > 0;
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
