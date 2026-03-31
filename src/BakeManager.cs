using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityRemix
{
    /// <summary>
    /// Manages bake tool subprocess execution and loading baked meshes into Remix.
    /// Runs --all on first launch to bake every scene, then uses a manifest to match
    /// Unity runtime scene names to the correct cache files.
    /// </summary>
    public class BakeManager
    {
        private readonly ManualLogSource logger;
        private readonly RemixMeshConverter meshConverter;
        private readonly object apiLock;
        private readonly ConfigEntry<bool> configEnabled;
        private readonly ConfigEntry<string> configBakeToolPath;
        private readonly ConfigEntry<bool> configForceRebake;
        
        private readonly string cacheDir;
        private readonly string gameDataPath;
        
        // Mesh dedup: hash → remix handle (persists across scene loads)
        private Dictionary<ulong, IntPtr> bakedMeshHandles = new Dictionary<ulong, IntPtr>();
        // Current scene's baked instances for drawing each frame
        private List<BakedInstanceData> currentBakedInstances = new List<BakedInstanceData>();
        private readonly object bakeLock = new object();
        
        // Streaming queue: background thread pushes parsed entries, render thread drains batches
        private readonly Queue<BakeCacheReader.BakedMeshEntry> streamingQueue = new Queue<BakeCacheReader.BakedMeshEntry>();
        private readonly object streamLock = new object();
        private volatile bool streamingActive;
        
        // How many meshes to create per render frame (balance responsiveness vs load speed)
        private const int MeshesPerFrame = 32;
        
        // Manifest loaded from the bake output
        private ManifestData manifest;
        
        // Background bake state
        private Thread bakeThread;
        private volatile bool bakeInProgress;
        private volatile bool bakeCompleted;
        // Scenes that arrived while bake was running — process after completion
        private readonly Queue<SceneInfo> pendingScenes = new Queue<SceneInfo>();
        private readonly object pendingLock = new object();
        
        public struct BakedInstanceData
        {
            public IntPtr MeshHandle;
            public RemixAPI.remixapi_Transform Transform;
        }
        
        private struct SceneInfo
        {
            public string Name;
            public string Path;
            public int BuildIndex;
        }
        
        public BakeManager(
            ManualLogSource logger,
            RemixMeshConverter meshConverter,
            object apiLock,
            ConfigEntry<bool> enabled,
            ConfigEntry<string> bakeToolPath,
            ConfigEntry<bool> forceRebake)
        {
            this.logger = logger;
            this.meshConverter = meshConverter;
            this.apiLock = apiLock;
            this.configEnabled = enabled;
            this.configBakeToolPath = bakeToolPath;
            this.configForceRebake = forceRebake;
            
            gameDataPath = Application.dataPath;
            string gameRoot = System.IO.Path.GetDirectoryName(gameDataPath);
            cacheDir = System.IO.Path.Combine(gameRoot, "BakeCache");
        }
        
        public bool IsEnabled => configEnabled.Value;
        public bool HasBakedData
        {
            get
            {
                lock (bakeLock) { return currentBakedInstances.Count > 0; }
            }
        }
        public bool IsStreaming => streamingActive;
        
        /// <summary>
        /// Called when a scene loads. On first call, triggers --all bake.
        /// Subsequent calls look up the scene in the manifest.
        /// </summary>
        public void OnSceneLoaded(Scene scene)
        {
            if (!configEnabled.Value)
                return;
            
            var info = new SceneInfo
            {
                Name = scene.name,
                Path = scene.path,
                BuildIndex = scene.buildIndex
            };
            
            string manifestPath = System.IO.Path.Combine(cacheDir, "manifest.json");
            
            // If manifest exists and we're not forcing rebake, try to load from cache
            if (File.Exists(manifestPath) && !configForceRebake.Value)
            {
                if (manifest == null)
                    LoadManifest(manifestPath);
                
                if (manifest != null)
                {
                    // Load on a background thread — reading large files + creating meshes freezes the game
                    var thread = new Thread(() => LoadSceneFromManifest(info));
                    thread.IsBackground = true;
                    thread.Start();
                    return;
                }
            }
            
            // Need to bake — enqueue this scene and start bake if not already running
            lock (pendingLock)
            {
                pendingScenes.Enqueue(info);
            }
            
            if (bakeInProgress)
            {
                logger.LogInfo($"Bake in progress, scene '{info.Name}' queued for loading after completion");
                return;
            }
            
            string toolPath = ResolveBakeToolPath();
            if (toolPath == null)
            {
                logger.LogWarning("Bake tool not found. Set BakeToolPath in config or place UnityRemix.Bake.exe next to the game.");
                return;
            }
            
            bakeInProgress = true;
            bakeThread = new Thread(() => RunBakeAll(toolPath));
            bakeThread.IsBackground = true;
            bakeThread.Start();
        }
        
        /// <summary>
        /// Called each render frame. Drains a batch from the streaming queue,
        /// creates meshes, and returns all instances loaded so far.
        /// </summary>
        public BakedInstanceData[] GetBakedInstances()
        {
            // If bake just completed, process pending scenes on a background thread
            if (bakeCompleted)
            {
                bakeCompleted = false;
                var thread = new Thread(ProcessPendingScenes);
                thread.IsBackground = true;
                thread.Start();
            }
            
            // Drain a batch from the streaming queue (mesh creation on render thread)
            DrainStreamingBatch();
            
            lock (bakeLock)
            {
                return currentBakedInstances.Count > 0 ? currentBakedInstances.ToArray() : null;
            }
        }
        
        public void ClearBakedData()
        {
            lock (bakeLock)
            {
                currentBakedInstances.Clear();
            }
            lock (streamLock)
            {
                streamingQueue.Clear();
            }
        }
        
        private void DrainStreamingBatch()
        {
            BakeCacheReader.BakedMeshEntry[] batch;
            lock (streamLock)
            {
                int count = Math.Min(MeshesPerFrame, streamingQueue.Count);
                if (count == 0)
                {
                    if (streamingActive)
                    {
                        streamingActive = false;
                        logger.LogInfo($"Streaming complete: {currentBakedInstances.Count} baked mesh instances loaded");
                    }
                    return;
                }
                batch = new BakeCacheReader.BakedMeshEntry[count];
                for (int i = 0; i < count; i++)
                    batch[i] = streamingQueue.Dequeue();
            }
            
            var newInstances = new List<BakedInstanceData>(batch.Length);
            foreach (var entry in batch)
            {
                IntPtr meshHandle = CreateRemixMeshFromBaked(entry);
                if (meshHandle == IntPtr.Zero)
                    continue;
                
                float[] m = entry.WorldMatrix;
                var transform = RemixAPI.remixapi_Transform.FromMatrix(
                    m[0], m[1], m[2], m[3],
                    m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11]
                );
                
                newInstances.Add(new BakedInstanceData
                {
                    MeshHandle = meshHandle,
                    Transform = transform
                });
            }
            
            if (newInstances.Count > 0)
            {
                lock (bakeLock)
                {
                    currentBakedInstances.AddRange(newInstances);
                }
            }
        }
        
        private void RunBakeAll(string toolPath)
        {
            try
            {
                logger.LogInfo("Starting bake (--all) for all scenes...");
                
                Directory.CreateDirectory(cacheDir);
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = toolPath,
                    Arguments = $"--data \"{gameDataPath}\" --all --out \"{cacheDir}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using (var process = Process.Start(startInfo))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    
                    if (process.ExitCode != 0)
                    {
                        logger.LogError($"Bake tool failed (exit {process.ExitCode}):\n{stderr}");
                        return;
                    }
                    
                    if (!string.IsNullOrEmpty(stdout))
                        logger.LogInfo($"Bake tool output:\n{stdout.TrimEnd()}");
                }
                
                // Load the manifest
                string manifestPath = System.IO.Path.Combine(cacheDir, "manifest.json");
                LoadManifest(manifestPath);
                
                // Signal that pending scenes should be processed
                bakeCompleted = true;
            }
            catch (Exception ex)
            {
                logger.LogError($"Bake failed: {ex.Message}");
            }
            finally
            {
                bakeInProgress = false;
            }
        }
        
        private void ProcessPendingScenes()
        {
            if (manifest == null)
                return;
            
            SceneInfo[] toProcess;
            lock (pendingLock)
            {
                toProcess = pendingScenes.ToArray();
                pendingScenes.Clear();
            }
            
            foreach (var info in toProcess)
            {
                LoadSceneFromManifest(info);
            }
        }
        
        private void LoadSceneFromManifest(SceneInfo info)
        {
            var entry = FindManifestEntry(info);
            if (entry == null)
            {
                logger.LogWarning($"No bake cache found for scene '{info.Name}' (path='{info.Path}', buildIndex={info.BuildIndex})");
                return;
            }
            
            if (entry.MeshCount == 0)
            {
                logger.LogInfo($"Scene '{info.Name}' matched manifest entry '{entry.Name}' but has no meshes, skipping");
                return;
            }
            
            string cachePath = System.IO.Path.Combine(cacheDir, entry.File);
            if (!File.Exists(cachePath))
            {
                logger.LogWarning($"Manifest references '{entry.File}' but file not found");
                return;
            }
            
            logger.LogInfo($"Loading bake cache for scene '{info.Name}' -> '{entry.Name}' ({entry.MeshCount} meshes)");
            LoadBakeCache(cachePath);
        }
        
        /// <summary>
        /// Multi-strategy matching: buildIndex, exact name, path filename, GUID, collection name.
        /// </summary>
        private ManifestEntry FindManifestEntry(SceneInfo info)
        {
            if (manifest?.Scenes == null)
                return null;
            
            // Strategy 1: Match by build index → level{N} collection
            if (info.BuildIndex >= 0)
            {
                string levelName = "level" + info.BuildIndex;
                foreach (var e in manifest.Scenes)
                {
                    if (e.CollectionNames != null)
                    {
                        foreach (var cn in e.CollectionNames)
                        {
                            if (string.Equals(cn, levelName, StringComparison.OrdinalIgnoreCase))
                                return e;
                        }
                    }
                }
            }
            
            // Strategy 2: Exact name match
            foreach (var e in manifest.Scenes)
                if (string.Equals(e.Name, info.Name, StringComparison.OrdinalIgnoreCase))
                    return e;
            
            // Strategy 3: Scene path filename match (e.g. "Assets/Scenes/0-S" -> "0-S")
            if (!string.IsNullOrEmpty(info.Path))
            {
                string runtimeFilename = System.IO.Path.GetFileNameWithoutExtension(info.Path);
                foreach (var e in manifest.Scenes)
                {
                    string manifestFilename = System.IO.Path.GetFileNameWithoutExtension(e.Path);
                    if (string.Equals(runtimeFilename, manifestFilename, StringComparison.OrdinalIgnoreCase))
                        return e;
                }
            }
            
            // Strategy 4: GUID match — Unity's addressable scenes may report name as a GUID hash
            string normalizedName = NormalizeGuid(info.Name);
            if (normalizedName.Length == 32)
            {
                foreach (var e in manifest.Scenes)
                {
                    if (!string.IsNullOrEmpty(e.Guid))
                    {
                        string normalizedManifest = NormalizeGuid(e.Guid);
                        if (string.Equals(normalizedName, normalizedManifest, StringComparison.OrdinalIgnoreCase))
                            return e;
                    }
                }
            }
            
            // Strategy 5: Partial name match against manifest path
            foreach (var e in manifest.Scenes)
                if (!string.IsNullOrEmpty(e.Path) && e.Path.IndexOf(info.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return e;
            
            return null;
        }
        
        private static string NormalizeGuid(string guid)
        {
            // Strip dashes, braces, and whitespace to get a raw 32-char hex string
            if (guid == null) return "";
            return guid.Replace("-", "").Replace("{", "").Replace("}", "").Trim();
        }
        
        private void LoadBakeCache(string cachePath)
        {
            var entries = BakeCacheReader.Read(cachePath, logger);
            if (entries == null || entries.Length == 0)
                return;
            
            // Push all entries into the streaming queue — the render thread drains them in batches
            lock (streamLock)
            {
                foreach (var entry in entries)
                    streamingQueue.Enqueue(entry);
            }
            streamingActive = true;
            logger.LogInfo($"Queued {entries.Length} baked meshes for streaming");
        }
        
        private IntPtr CreateRemixMeshFromBaked(BakeCacheReader.BakedMeshEntry entry)
        {
            if (bakedMeshHandles.TryGetValue(entry.NameHash, out IntPtr existing))
                return existing;
            
            if (entry.Vertices.Length == 0 || entry.Indices.Length == 0)
                return IntPtr.Zero;
            
            var remixVerts = new RemixAPI.remixapi_HardcodedVertex[entry.Vertices.Length];
            for (int i = 0; i < entry.Vertices.Length; i++)
            {
                var v = entry.Vertices[i];
                remixVerts[i] = RemixAPI.MakeVertex(
                    v.X, v.Y, v.Z,
                    v.NX, v.NY, v.NZ,
                    v.U, v.V,
                    0xFFFFFFFF
                );
            }
            
            GCHandle vertexHandle = GCHandle.Alloc(remixVerts, GCHandleType.Pinned);
            GCHandle indexHandle = GCHandle.Alloc(entry.Indices, GCHandleType.Pinned);
            
            try
            {
                var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                {
                    vertices_values = vertexHandle.AddrOfPinnedObject(),
                    vertices_count = (ulong)remixVerts.Length,
                    indices_values = indexHandle.AddrOfPinnedObject(),
                    indices_count = (ulong)entry.Indices.Length,
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
                        hash = entry.NameHash,
                        surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                        surfaces_count = 1
                    };
                    
                    IntPtr handle;
                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock)
                    {
                        var createMeshFunc = meshConverter.GetCreateMeshFunc();
                        if (createMeshFunc == null)
                            return IntPtr.Zero;
                        result = createMeshFunc(ref meshInfo, out handle);
                    }
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogError($"Failed to create baked mesh 0x{entry.NameHash:X16}: {result}");
                        return IntPtr.Zero;
                    }
                    
                    bakedMeshHandles[entry.NameHash] = handle;
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
        
        private void LoadManifest(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                manifest = SimpleJson.DeserializeManifest(json);
                logger.LogInfo($"Loaded bake manifest: {manifest.Scenes.Length} scenes");
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to load manifest: {ex.Message}");
            }
        }
        
        private string ResolveBakeToolPath()
        {
            string configured = configBakeToolPath.Value;
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                return configured;
            
            string gameRoot = System.IO.Path.GetDirectoryName(gameDataPath);
            string nextToGame = System.IO.Path.Combine(gameRoot, "UnityRemix.Bake.exe");
            if (File.Exists(nextToGame))
                return nextToGame;
            
            string pluginDir = System.IO.Path.GetDirectoryName(typeof(BakeManager).Assembly.Location);
            string nextToPlugin = System.IO.Path.Combine(pluginDir, "UnityRemix.Bake.exe");
            if (File.Exists(nextToPlugin))
                return nextToPlugin;
            
            return null;
        }
    }
    
    // Manifest data classes — netstandard2.1 compatible (no System.Text.Json)
    
    public class ManifestData
    {
        public int Version;
        public ManifestEntry[] Scenes = Array.Empty<ManifestEntry>();
    }
    
    public class ManifestEntry
    {
        public string Name = "";
        public string Path = "";
        public string Guid = "";
        public string File = "";
        public string[] CollectionNames = Array.Empty<string>();
        public int MeshCount;
        public int Index;
    }
    
    /// <summary>
    /// Minimal JSON parser for the bake manifest. Avoids dependency on System.Text.Json
    /// which is unavailable in netstandard2.1 without extra packages.
    /// </summary>
    internal static class SimpleJson
    {
        public static ManifestData DeserializeManifest(string json)
        {
            var result = new ManifestData();
            var scenes = new List<ManifestEntry>();
            
            // Find "version": N
            int vi = json.IndexOf("\"version\"", StringComparison.OrdinalIgnoreCase);
            if (vi >= 0)
            {
                int colon = json.IndexOf(':', vi);
                if (colon >= 0)
                    result.Version = ParseInt(json, colon + 1);
            }
            
            // Find "scenes": [ ... ]
            int si = json.IndexOf("\"scenes\"", StringComparison.OrdinalIgnoreCase);
            if (si < 0)
                return result;
            
            int arrStart = json.IndexOf('[', si);
            if (arrStart < 0)
                return result;
            
            // Parse each { } object in the array
            int pos = arrStart + 1;
            while (pos < json.Length)
            {
                int objStart = json.IndexOf('{', pos);
                if (objStart < 0)
                    break;
                int objEnd = FindMatchingBrace(json, objStart);
                if (objEnd < 0)
                    break;
                
                string obj = json.Substring(objStart, objEnd - objStart + 1);
                scenes.Add(ParseEntry(obj));
                pos = objEnd + 1;
                
                // Check if we've exited the scenes array
                int nextComma = json.IndexOf(',', pos);
                int arrEnd = json.IndexOf(']', pos);
                if (arrEnd >= 0 && (nextComma < 0 || arrEnd < nextComma))
                    break;
            }
            
            result.Scenes = scenes.ToArray();
            return result;
        }
        
        private static ManifestEntry ParseEntry(string obj)
        {
            var entry = new ManifestEntry();
            entry.Name = GetString(obj, "name");
            entry.Path = GetString(obj, "path");
            entry.Guid = GetString(obj, "guid");
            entry.File = GetString(obj, "file");
            entry.MeshCount = GetInt(obj, "meshCount");
            entry.Index = GetInt(obj, "index");
            
            // Parse collectionNames array
            int cn = obj.IndexOf("\"collectionNames\"", StringComparison.OrdinalIgnoreCase);
            if (cn >= 0)
            {
                int arrS = obj.IndexOf('[', cn);
                int arrE = obj.IndexOf(']', arrS);
                if (arrS >= 0 && arrE >= 0)
                {
                    string arrContent = obj.Substring(arrS + 1, arrE - arrS - 1);
                    var names = new List<string>();
                    int p = 0;
                    while (p < arrContent.Length)
                    {
                        int q1 = arrContent.IndexOf('"', p);
                        if (q1 < 0) break;
                        int q2 = arrContent.IndexOf('"', q1 + 1);
                        if (q2 < 0) break;
                        names.Add(arrContent.Substring(q1 + 1, q2 - q1 - 1));
                        p = q2 + 1;
                    }
                    entry.CollectionNames = names.ToArray();
                }
            }
            
            return entry;
        }
        
        private static string GetString(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int ki = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (ki < 0) return "";
            int colon = json.IndexOf(':', ki + pattern.Length);
            if (colon < 0) return "";
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return "";
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }
        
        private static int GetInt(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int ki = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (ki < 0) return 0;
            int colon = json.IndexOf(':', ki + pattern.Length);
            if (colon < 0) return 0;
            return ParseInt(json, colon + 1);
        }
        
        private static int ParseInt(string json, int startFrom)
        {
            int i = startFrom;
            while (i < json.Length && !char.IsDigit(json[i]) && json[i] != '-') i++;
            int end = i;
            if (end < json.Length && json[end] == '-') end++;
            while (end < json.Length && char.IsDigit(json[end])) end++;
            if (end > i && int.TryParse(json.Substring(i, end - i), out int val))
                return val;
            return 0;
        }
        
        private static int FindMatchingBrace(string json, int openPos)
        {
            int depth = 0;
            bool inString = false;
            for (int i = openPos; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
    }
}
