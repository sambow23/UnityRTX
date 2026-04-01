using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Manages materials and textures for Remix - handles upload, caching, and material creation
    /// </summary>
    public class RemixMaterialManager
    {
        private readonly ManualLogSource logger;
        private readonly TextureCategoryManager textureCategoryManager;
        private readonly BepInEx.Configuration.ConfigEntry<bool> captureTextures;
        private readonly BepInEx.Configuration.ConfigEntry<bool> captureMaterials;
        private readonly object apiLock;
        
        // Cached delegates
        private RemixAPI.PFN_remixapi_CreateTexture createTextureFunc;
        private RemixAPI.PFN_remixapi_DestroyTexture destroyTextureFunc;
        private RemixAPI.PFN_remixapi_CreateMaterial createMaterialFunc;
        private RemixAPI.PFN_remixapi_DestroyMaterial destroyMaterialFunc;
        private RemixAPI.PFN_remixapi_AddTextureHash addTextureHashFunc;
        private RemixAPI.PFN_remixapi_RemoveTextureHash removeTextureHashFunc;
        
        // Cache for uploaded textures - maps Unity texture instance ID to Remix handle
        private Dictionary<int, IntPtr> textureCache = new Dictionary<int, IntPtr>();

        // Debug texture for meshes with no albedo — visible in Remix so it can be categorized/hidden
        private IntPtr debugTextureHandle = IntPtr.Zero;
        private ulong debugTextureHash;
        private const string DebugTextureHashPath = "0xDEBB0600DEBB0600"; // stable sentinel
        
        // Cache for texture hashes - maps Unity texture instance ID to XXH64 hash
        private Dictionary<int, ulong> textureHashCache = new Dictionary<int, ulong>();
        
        // Track which textures have meaningful alpha (not all-opaque)
        private HashSet<int> texturesWithAlpha = new HashSet<int>();
        
        // Cache for materials - maps Unity material instance ID to Remix material handle
        private Dictionary<int, IntPtr> materialCache = new Dictionary<int, IntPtr>();
        
        // Alpha handling modes detected from Unity materials
        public enum AlphaMode
        {
            Opaque,     // No alpha
            Cutout,     // Alpha test (discard below threshold)
            Blend       // Alpha blending (fade/transparent)
        }

        // Material data captured from Unity materials
        public struct MaterialTextureData
        {
            public IntPtr albedoHandle;
            public IntPtr normalHandle;
            public ulong albedoTextureHash;
            public ulong normalTextureHash;
            public Color albedoColor;
            public string materialName;
            public IntPtr remixMaterialHandle;
            public AlphaMode alphaMode;
            public float alphaCutoff; // 0-1, used for Cutout mode
            public byte wrapModeU;    // MDL WrapMode: 0=Clamp, 1=Repeat, 2=MirroredRepeat, 3=Clip
            public byte wrapModeV;
            public byte filterMode;   // MDL Filter: 0=Nearest, 1=Linear
        }
        private Dictionary<int, MaterialTextureData> materialTextureData = new Dictionary<int, MaterialTextureData>();
        
        // Queue for async material creation
        private Queue<int> pendingMaterialCreation = new Queue<int>();
        private HashSet<int> pendingMaterialSet = new HashSet<int>(); // Track what's pending
        private bool materialCreationThreadRunning = false;
        private Thread materialCreationThread = null;
        
        public RemixMaterialManager(
            ManualLogSource logger,
            TextureCategoryManager categoryManager,
            BepInEx.Configuration.ConfigEntry<bool> captureTextures,
            BepInEx.Configuration.ConfigEntry<bool> captureMaterials,
            RemixAPI.remixapi_Interface remixInterface,
            object apiLock)
        {
            this.logger = logger;
            this.textureCategoryManager = categoryManager;
            this.captureTextures = captureTextures;
            this.captureMaterials = captureMaterials;
            this.apiLock = apiLock;
            
            // Cache delegates
            if (remixInterface.CreateTexture != IntPtr.Zero)
                createTextureFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateTexture>(remixInterface.CreateTexture);
            
            if (remixInterface.DestroyTexture != IntPtr.Zero)
                destroyTextureFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyTexture>(remixInterface.DestroyTexture);
            
            if (remixInterface.CreateMaterial != IntPtr.Zero)
                createMaterialFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateMaterial>(remixInterface.CreateMaterial);
            
            if (remixInterface.DestroyMaterial != IntPtr.Zero)
                destroyMaterialFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyMaterial>(remixInterface.DestroyMaterial);
            
            if (remixInterface.AddTextureHash != IntPtr.Zero)
                addTextureHashFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_AddTextureHash>(remixInterface.AddTextureHash);
            
            if (remixInterface.RemoveTextureHash != IntPtr.Zero)
                removeTextureHashFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_RemoveTextureHash>(remixInterface.RemoveTextureHash);
        }
        
        /// <summary>
        /// Get material texture data for a material ID
        /// </summary>
        public bool TryGetMaterialData(int materialId, out MaterialTextureData data)
        {
            return materialTextureData.TryGetValue(materialId, out data);
        }
        
        /// <summary>
        /// Get or create Remix material handle for a material ID (on-demand creation)
        /// </summary>
        public IntPtr GetOrCreateMaterial(int materialId)
        {
            if (!materialTextureData.TryGetValue(materialId, out var matData))
                return IntPtr.Zero;
            
            // If material already created, return it
            if (matData.remixMaterialHandle != IntPtr.Zero)
                return matData.remixMaterialHandle;
            
            // Create material on-demand (called from render thread)
            IntPtr handle = CreateRemixMaterialSimple(ref matData, materialId);
            
            if (handle != IntPtr.Zero)
            {
                // Store the handle back
                matData.remixMaterialHandle = handle;
                materialTextureData[materialId] = matData;
            }
            
            return handle;
        }
        
        /// <summary>
        /// Register a texture to a Remix category
        /// </summary>
        public void RegisterTextureCategory(ulong textureHash, string categoryName)
        {
            if (addTextureHashFunc == null) return;
            
            string hashString = textureHash.ToString("X");
            RemixAPI.remixapi_ErrorCode result;
            lock (apiLock)
            {
                result = addTextureHashFunc(categoryName, hashString);
            }
            
            if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                textureCategoryManager.SetTextureCategory(textureHash, categoryName);
                logger.LogInfo($"Registered texture 0x{textureHash:X16} to category '{categoryName}'");
            }
            else
            {
                logger.LogError($"Failed to register texture to category: {result}");
            }
        }
        
        /// <summary>
        /// Capture textures from a Unity material
        /// </summary>
        public void CaptureMaterialTextures(Material material, int materialId)
        {
            if (material == null)
                return;

            // Check debug toggle
            if (!captureMaterials.Value)
                return;
            
            // Check if material already exists and has been created or is pending
            if (materialTextureData.ContainsKey(materialId))
            {
                // Material data already captured, don't overwrite it
                return;
            }
            
            // Also check if already pending to avoid race conditions
            lock (pendingMaterialCreation)
            {
                if (pendingMaterialSet.Contains(materialId))
                {
                    return;
                }
            }
                
            var matData = new MaterialTextureData
            {
                materialName = material.name,
                albedoColor = Color.white,
                albedoHandle = IntPtr.Zero,
                normalHandle = IntPtr.Zero,
                albedoTextureHash = 0,
                normalTextureHash = 0,
                remixMaterialHandle = IntPtr.Zero,
                alphaMode = AlphaMode.Opaque,
                alphaCutoff = 0.5f,
                wrapModeU = 1, // MDL Repeat
                wrapModeV = 1,
                filterMode = 1 // MDL Linear
            };
            
            // Get albedo color
            if (material.HasProperty("_Color"))
            {
                matData.albedoColor = material.GetColor("_Color");
            }
            
            // Detect alpha mode from shader keywords, _Mode property, and render queue
            var (detectedMode, detectionReason) = DetectAlphaModeWithReason(material);
            matData.alphaMode = detectedMode;
            if (material.HasProperty("_Cutoff"))
            {
                matData.alphaCutoff = material.GetFloat("_Cutoff");
            }
            
            // Detailed diagnostic log for every material
            logger.LogInfo($"[MaterialDiag] '{material.name}' shader='{material.shader?.name}' queue={material.renderQueue} " +
                $"alphaMode={matData.alphaMode} reason={detectionReason} " +
                $"color=({matData.albedoColor.r:F3},{matData.albedoColor.g:F3},{matData.albedoColor.b:F3},{matData.albedoColor.a:F3}) " +
                $"cutoff={matData.alphaCutoff:F3}");
            
            // Upload albedo texture
            if (captureTextures.Value && material.HasProperty("_MainTex"))
            {
                var tex = material.GetTexture("_MainTex") as Texture2D;
                if (tex != null)
                {
                    // Read Unity wrap/filter modes from texture and convert to MDL values
                    matData.wrapModeU = UnityWrapToMdl(tex.wrapModeU);
                    matData.wrapModeV = UnityWrapToMdl(tex.wrapModeV);
                    matData.filterMode = (byte)(tex.filterMode == FilterMode.Point ? 0 : 1);
                    
                    matData.albedoHandle = UploadUnityTexture(tex, srgb: true);
                    if (matData.albedoHandle != IntPtr.Zero)
                    {
                        int texId = tex.GetInstanceID();
                        if (textureHashCache.TryGetValue(texId, out ulong hash))
                        {
                            matData.albedoTextureHash = hash;
                        }
                        
                        // Fallback: if shader metadata says Opaque but the texture has alpha, upgrade to Cutout
                        if (matData.alphaMode == AlphaMode.Opaque && texturesWithAlpha.Contains(texId))
                        {
                            matData.alphaMode = AlphaMode.Cutout;
                            if (!material.HasProperty("_Cutoff"))
                                matData.alphaCutoff = 0.5f;
                            logger.LogInfo($"[AlphaFallback] '{material.name}': texture has alpha content, upgrading Opaque -> Cutout (cutoff={matData.alphaCutoff:F2})");
                        }
                    }
                }
            }
            
            // Upload normal map
            if (captureTextures.Value && material.HasProperty("_BumpMap"))
            {
                var tex = material.GetTexture("_BumpMap") as Texture2D;
                if (tex != null)
                {
                    matData.normalHandle = UploadUnityTexture(tex, srgb: false);
                    if (matData.normalHandle != IntPtr.Zero)
                    {
                        int texId = tex.GetInstanceID();
                        if (textureHashCache.TryGetValue(texId, out ulong hash))
                        {
                            matData.normalTextureHash = hash;
                        }
                        logger.LogInfo($"Captured normal texture for material '{material.name}' (hash: 0x{matData.normalTextureHash:X16})");
                    }
                }
            }
            
            // Store for later use
            materialTextureData[materialId] = matData;
            
            // DISABLED: Async material creation causes deadlocks with Remix API
            // Materials are now created on-demand on the render thread during mesh creation
            // This prevents deadlocks between material thread and render thread competing for Remix device lock
            
            string albedoPath = GetTexturePathFromHandle(matData.albedoHandle);
            string normalPath = GetTexturePathFromHandle(matData.normalHandle);
            //logger.LogInfo($"Textures ready for material '{material.name}': albedo={albedoPath ?? "none"}, normal={normalPath ?? "none"}");
        }
        
        /// <summary>
        /// Upload Unity texture to Remix
        /// </summary>
        public IntPtr UploadUnityTexture(Texture2D unityTexture, bool srgb = true)
        {
            if (unityTexture == null || createTextureFunc == null)
                return IntPtr.Zero;
                
            int texId = unityTexture.GetInstanceID();
            
            // Check cache first
            if (textureCache.TryGetValue(texId, out IntPtr cachedHandle))
            {
                return cachedHandle;
            }
            
            try
            {
                // Get raw texture data
                byte[] pixelData;
                byte[] hashSourceData;
                RemixAPI.remixapi_Format format;
                uint actualMipLevels = (uint)unityTexture.mipmapCount;
                
                // Handle non-readable textures via GPU readback
                if (!unityTexture.isReadable)
                {
                    logger.LogInfo($"Texture '{unityTexture.name}' is not readable - forcing GPU readback");
                    
                    RenderTextureReadWrite colorSpace = srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
                    RenderTexture tmp = RenderTexture.GetTemporary(
                        unityTexture.width, unityTexture.height, 0,
                        RenderTextureFormat.ARGB32, colorSpace);
                    
                    RenderTexture previous = RenderTexture.active;
                    Graphics.Blit(unityTexture, tmp);
                    RenderTexture.active = tmp;
                    
                    Texture2D readableTexture = new Texture2D(unityTexture.width, unityTexture.height, TextureFormat.RGBA32, false, !srgb);
                    readableTexture.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                    readableTexture.Apply();
                    
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(tmp);
                    
                    pixelData = readableTexture.GetRawTextureData();
                    hashSourceData = pixelData;
                    format = srgb ? RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_SRGB 
                                  : RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                    
                    actualMipLevels = 1; // GPU readback only gives top mip
                    UnityEngine.Object.Destroy(readableTexture);
                }
                else if (unityTexture.format == TextureFormat.RGB24)
                {
                    // Convert RGB24 to RGBA32
                    Color32[] pixels = unityTexture.GetPixels32();
                    pixelData = new byte[pixels.Length * 4];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixelData[i * 4 + 0] = pixels[i].r;
                        pixelData[i * 4 + 1] = pixels[i].g;
                        pixelData[i * 4 + 2] = pixels[i].b;
                        pixelData[i * 4 + 3] = 255;
                    }
                    hashSourceData = pixelData;
                    format = srgb ? RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_SRGB 
                                  : RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                }
                else
                {
                    // Use raw data for supported formats
                    pixelData = unityTexture.GetRawTextureData();
                    hashSourceData = pixelData;
                    
                    switch (unityTexture.format)
                    {
                        case TextureFormat.RGBA32:
                            format = srgb ? RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_SRGB 
                                          : RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                            break;
                        case TextureFormat.BGRA32:
                            format = srgb ? RemixAPI.remixapi_Format.REMIXAPI_FORMAT_B8G8R8A8_SRGB 
                                          : RemixAPI.remixapi_Format.REMIXAPI_FORMAT_B8G8R8A8_UNORM;
                            break;
                        case TextureFormat.DXT1:
                            format = srgb ? RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC1_RGB_SRGB 
                                          : RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC1_RGB_UNORM;
                            break;
                        case TextureFormat.DXT5:
                            format = srgb ? RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC3_SRGB 
                                          : RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC3_UNORM;
                            break;
                        default:
                            logger.LogWarning($"Unsupported texture format: {unityTexture.format}");
                            return IntPtr.Zero;
                    }
                }
                
                // Check alpha channel content for uncompressed RGBA formats
                bool hasAlpha = false;
                if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_SRGB ||
                    format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM)
                {
                    hasAlpha = SampleAlphaRGBA(pixelData, alphaOffset: 3, stride: 4);
                }
                else if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_B8G8R8A8_SRGB ||
                         format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_B8G8R8A8_UNORM)
                {
                    hasAlpha = SampleAlphaRGBA(pixelData, alphaOffset: 3, stride: 4);
                }
                else if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC3_SRGB ||
                         format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC3_UNORM)
                {
                    // DXT5/BC3 has a full alpha channel — assume it's used
                    hasAlpha = true;
                }
                // BC1 (DXT1) has only 1-bit punch-through alpha, treat as opaque
                
                if (hasAlpha)
                    texturesWithAlpha.Add(texId);
                
                // Compute XXH64 hash
                ulong textureHash = XXHash64.ComputeHash(hashSourceData, 0, hashSourceData.Length);
                if (textureHash == 0) textureHash = 1;
                
                textureHashCache[texId] = textureHash;
                logger.LogInfo($"Computed XXH64 hash for '{unityTexture.name}': 0x{textureHash:X16} hasAlpha={hasAlpha} fmt={unityTexture.format}");
                
                // Pin and upload
                GCHandle pixelHandle = GCHandle.Alloc(pixelData, GCHandleType.Pinned);
                
                try
                {
                    var textureInfo = new RemixAPI.remixapi_TextureInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_TEXTURE_INFO,
                        pNext = IntPtr.Zero,
                        hash = textureHash,
                        width = (uint)unityTexture.width,
                        height = (uint)unityTexture.height,
                        depth = 1,
                        mipLevels = actualMipLevels,
                        format = format,
                        data = pixelHandle.AddrOfPinnedObject(),
                        dataSize = (ulong)pixelData.Length
                    };
                    
                    IntPtr textureHandle;
                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock)
                    {
                        result = createTextureFunc(ref textureInfo, out textureHandle);
                    }
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogError($"Failed to create texture '{unityTexture.name}': {result}");
                        return IntPtr.Zero;
                    }
                    
                    textureCache[texId] = textureHandle;
                    logger.LogInfo($"Successfully uploaded texture '{unityTexture.name}' with handle: 0x{textureHandle.ToInt64():X}");
                    
                    return textureHandle;
                }
                finally
                {
                    pixelHandle.Free();
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception uploading texture '{unityTexture.name}': {ex.Message}");
                return IntPtr.Zero;
            }
        }
        
        /// <summary>
        /// Get texture hash string from handle (for material creation)
        /// Remix expects the hash that was used when uploading the texture
        /// </summary>
        public string GetTexturePathFromHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return null;
            
            // The handle IS the texture hash
            ulong handleValue = (ulong)handle.ToInt64();
            return $"0x{handleValue:X16}";
        }
        
        /// <summary>
        /// Generate material hash
        /// </summary>
        public ulong GenerateMaterialHash(string materialName, int materialId)
        {
            string input = $"{materialName}_{materialId}";
            
            ulong hash = 14695981039346656037UL;
            foreach (char c in input)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            
            if (hash == 0) hash = 1;
            return hash;
        }
        
        /// <summary>
        /// Start async material creation thread
        /// </summary>
        private void StartMaterialCreationThread()
        {
            if (materialCreationThreadRunning)
                return;
                
            materialCreationThreadRunning = true;
            materialCreationThread = new Thread(MaterialCreationThreadFunc);
            materialCreationThread.IsBackground = true;
            materialCreationThread.Start();
            logger.LogInfo("Started async material creation thread");
        }
        
        private void MaterialCreationThreadFunc()
        {
            logger.LogInfo("Material creation thread running");
            
            while (materialCreationThreadRunning)
            {
                int materialId = -1;
                
                lock (pendingMaterialCreation)
                {
                    if (pendingMaterialCreation.Count > 0)
                    {
                        materialId = pendingMaterialCreation.Dequeue();
                        pendingMaterialSet.Remove(materialId);
                    }
                }
                
                if (materialId != -1 && materialTextureData.ContainsKey(materialId))
                {
                    Thread.Sleep(200); // Wait for texture upload to complete
                    
                    var matData = materialTextureData[materialId];
                    IntPtr handle = CreateRemixMaterialSimple(ref matData, materialId);
                    
                    if (handle != IntPtr.Zero)
                    {
                        matData.remixMaterialHandle = handle;
                        materialTextureData[materialId] = matData;
                    }
                }
                else
                {
                    Thread.Sleep(100);
                }
            }
            
            logger.LogInfo("Material creation thread stopped");
        }
        
        /// <summary>
        /// Convert Unity TextureWrapMode to MDL WrapMode (0=Clamp, 1=Repeat, 2=MirroredRepeat, 3=Clip).
        /// </summary>
        private static byte UnityWrapToMdl(TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Repeat: return 1;
                case TextureWrapMode.Clamp:  return 0;
                case TextureWrapMode.Mirror: return 2;
                default:                     return 1; // MirrorOnce and others → Repeat
            }
        }
        
        /// <summary>
        /// Sample an uncompressed RGBA pixel buffer to check if the alpha channel has non-trivial content.
        /// Checks a sparse grid of pixels for performance (avoids scanning entire textures).
        /// </summary>
        private static bool SampleAlphaRGBA(byte[] pixelData, int alphaOffset, int stride)
        {
            int pixelCount = pixelData.Length / stride;
            if (pixelCount == 0) return false;
            
            // Sample up to 256 evenly-spaced pixels
            int step = Math.Max(1, pixelCount / 256);
            for (int i = 0; i < pixelCount; i += step)
            {
                int idx = i * stride + alphaOffset;
                if (idx < pixelData.Length && pixelData[idx] < 250)
                    return true;
            }
            return false;
        }
        
        /// <summary>
        /// Detect alpha mode from Unity material properties, shader keywords, and render queue.
        /// Covers Standard shader (_Mode), URP/HDRP (_Surface), and keyword-based detection.
        /// Returns (mode, reason) for diagnostic logging.
        /// </summary>
        private static (AlphaMode mode, string reason) DetectAlphaModeWithReason(Material material)
        {
            // 1. Shader keywords (most reliable, works across Standard/URP/HDRP/custom)
            if (material.IsKeywordEnabled("_ALPHATEST_ON"))
                return (AlphaMode.Cutout, "keyword:_ALPHATEST_ON");
            if (material.IsKeywordEnabled("_ALPHABLEND_ON"))
                return (AlphaMode.Blend, "keyword:_ALPHABLEND_ON");
            if (material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                return (AlphaMode.Blend, "keyword:_ALPHAPREMULTIPLY_ON");
            
            // 2. Standard shader _Mode property (0=Opaque, 1=Cutout, 2=Fade, 3=Transparent)
            if (material.HasProperty("_Mode"))
            {
                int mode = (int)material.GetFloat("_Mode");
                if (mode == 1) return (AlphaMode.Cutout, $"_Mode={mode}");
                if (mode >= 2) return (AlphaMode.Blend, $"_Mode={mode}");
            }
            
            // 3. URP/HDRP _Surface property (0=Opaque, 1=Transparent)
            if (material.HasProperty("_Surface"))
            {
                int surface = (int)material.GetFloat("_Surface");
                if (surface == 1)
                {
                    if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f)
                        return (AlphaMode.Cutout, $"_Surface=1,_AlphaClip=1");
                    return (AlphaMode.Blend, $"_Surface=1");
                }
            }
            
            // 4. Render queue heuristic (fallback)
            int queue = material.renderQueue;
            if (queue >= 2450 && queue < 3000) return (AlphaMode.Cutout, $"renderQueue={queue}");
            if (queue >= 3000) return (AlphaMode.Blend, $"renderQueue={queue}");
            
            return (AlphaMode.Opaque, "default");
        }
        
        private IntPtr CreateRemixMaterialSimple(ref MaterialTextureData matData, int materialId)
        {
            if (createMaterialFunc == null)
                return IntPtr.Zero;
                
            try
            {
                string albedoPath = GetTexturePathFromHandle(matData.albedoHandle);
                string normalPath = GetTexturePathFromHandle(matData.normalHandle);
                ulong matHash = GenerateMaterialHash(matData.materialName, materialId);
                
                // Use debug placeholder for materials with no albedo texture
                if (albedoPath == null)
                {
                    EnsureDebugTexture();
                    if (debugTextureHandle != IntPtr.Zero)
                        albedoPath = DebugTextureHashPath;
                }

                logger.LogInfo($"[MaterialCreate] '{matData.materialName}': albedo={albedoPath ?? "none"}, normal={normalPath ?? "none"}, alphaMode={matData.alphaMode}");
                
                // Build the OpaqueEXT using the blittable Raw struct for safe pNext chaining
                var opaqueExt = new RemixAPI.remixapi_MaterialInfoOpaqueEXT_Raw
                {
                    sType = (int)RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_OPAQUE_EXT,
                    pNext = IntPtr.Zero,
                    roughnessTexture = IntPtr.Zero,
                    metallicTexture = IntPtr.Zero,
                    anisotropy = 0.0f,
                    albedoConstant_x = matData.albedoColor.r,
                    albedoConstant_y = matData.albedoColor.g,
                    albedoConstant_z = matData.albedoColor.b,
                    opacityConstant = matData.albedoColor.a,
                    roughnessConstant = 0.5f,
                    metallicConstant = 0.0f,
                    thinFilmThickness_hasvalue = 0,
                    thinFilmThickness_value = 0.0f,
                    alphaIsThinFilmThickness = 0,
                    heightTexture = IntPtr.Zero,
                    displaceIn = 0.0f,
                    useDrawCallAlphaState = 0,
                    blendType_hasvalue = 0,
                    blendType_value = 0,
                    invertedBlend = 0,
                    // VkCompareOp: 0=NEVER, 6=GREATER_OR_EQUAL, 7=ALWAYS
                    alphaTestType = 7, // kAlways — default: pass all pixels (no alpha test)
                    alphaReferenceValue = 0,
                    displaceOut = 0.0f
                };
                
                // Configure alpha based on detected mode
                switch (matData.alphaMode)
                {
                    case AlphaMode.Cutout:
                        // VkCompareOp 6 = GREATER_OR_EQUAL: pass pixels with alpha >= reference
                        opaqueExt.alphaTestType = 6;
                        opaqueExt.alphaReferenceValue = (byte)(Mathf.Clamp01(matData.alphaCutoff) * 255f);
                        break;
                    case AlphaMode.Blend:
                        // Enable alpha blending (kAlpha = 0)
                        opaqueExt.blendType_hasvalue = 1; // remixapi_Bool.True
                        opaqueExt.blendType_value = 0;    // kAlpha
                        break;
                }
                
                // Log all OpaqueEXT values being sent to Remix
                logger.LogInfo($"[OpaqueEXT] '{matData.materialName}': " +
                    $"sType={opaqueExt.sType} " +
                    $"albedo=({opaqueExt.albedoConstant_x:F3},{opaqueExt.albedoConstant_y:F3},{opaqueExt.albedoConstant_z:F3}) " +
                    $"opacity={opaqueExt.opacityConstant:F3} " +
                    $"roughness={opaqueExt.roughnessConstant:F3} metallic={opaqueExt.metallicConstant:F3} " +
                    $"alphaTestType={opaqueExt.alphaTestType} alphaRef={opaqueExt.alphaReferenceValue} " +
                    $"blendHasValue={opaqueExt.blendType_hasvalue} blendValue={opaqueExt.blendType_value} " +
                    $"invertedBlend={opaqueExt.invertedBlend} useDrawCallAlpha={opaqueExt.useDrawCallAlphaState} " +
                    $"structSize={System.Runtime.InteropServices.Marshal.SizeOf<RemixAPI.remixapi_MaterialInfoOpaqueEXT_Raw>()}");
                
                GCHandle opaqueHandle = GCHandle.Alloc(opaqueExt, GCHandleType.Pinned);
                
                try
                {
                    var materialInfo = new RemixAPI.remixapi_MaterialInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO,
                        pNext = opaqueHandle.AddrOfPinnedObject(),
                        hash = matHash,
                        albedoTexture = albedoPath,
                        normalTexture = normalPath,
                        tangentTexture = null,
                        emissiveTexture = null,
                        emissiveIntensity = 0.0f,
                        emissiveColorConstant = new RemixAPI.remixapi_Float3D { x = 0, y = 0, z = 0 },
                        spriteSheetRow = 1,
                        spriteSheetCol = 1,
                        spriteSheetFps = 0,
                        filterMode = matData.filterMode,
                        wrapModeU = matData.wrapModeU,
                        wrapModeV = matData.wrapModeV
                    };
                    
                    IntPtr materialHandle;
                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock)
                    {
                        result = createMaterialFunc(ref materialInfo, out materialHandle);
                    }
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogError($"Failed to create Remix material for '{matData.materialName}': {result}");
                        return IntPtr.Zero;
                    }
                    
                    logger.LogInfo($"Created Remix material '{matData.materialName}' with hash 0x{matHash:X}, handle: 0x{materialHandle.ToInt64():X}");
                    return materialHandle;
                }
                finally
                {
                    opaqueHandle.Free();
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception creating material: {ex.Message}");
                return IntPtr.Zero;
            }
        }
        
        /// <summary>
        /// Lazily creates the debug texture on first use (must be called from render thread
        /// after the Remix device is registered).
        /// </summary>
        private void EnsureDebugTexture()
        {
            if (debugTextureHandle != IntPtr.Zero)
                return;
            CreateDebugTexture();
        }

        private void CreateDebugTexture()
        {
            if (createTextureFunc == null)
                return;

            const int size = 8;
            byte[] pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    bool checker = ((x + y) & 1) == 0;
                    pixels[i + 0] = checker ? (byte)255 : (byte)0; // R
                    pixels[i + 1] = 0;                             // G
                    pixels[i + 2] = checker ? (byte)255 : (byte)0; // B
                    pixels[i + 3] = 255;                           // A
                }
            }

            debugTextureHash = 0xDEBB0600DEBB0600UL;

            GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var info = new RemixAPI.remixapi_TextureInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_TEXTURE_INFO,
                    pNext = IntPtr.Zero,
                    hash = debugTextureHash,
                    width = size,
                    height = size,
                    depth = 1,
                    mipLevels = 1,
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_SRGB,
                    data = pinned.AddrOfPinnedObject(),
                    dataSize = (ulong)pixels.Length
                };

                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createTextureFunc(ref info, out debugTextureHandle);
                }

                if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    logger.LogInfo($"Created debug placeholder texture (hash: 0x{debugTextureHash:X16})");
                else
                    logger.LogWarning($"Failed to create debug texture: {result}");
            }
            finally
            {
                pinned.Free();
            }
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public void Cleanup()
        {
            materialCreationThreadRunning = false;
            if (materialCreationThread != null && materialCreationThread.IsAlive)
            {
                materialCreationThread.Join(1000);
            }
            
            if (destroyTextureFunc != null)
            {
                foreach (var handle in textureCache.Values)
                {
                    if (handle != IntPtr.Zero)
                    {
                        destroyTextureFunc(handle);
                    }
                }
            }
            textureCache.Clear();
        }
    }
}
