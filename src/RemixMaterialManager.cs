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
        
        // Cache for texture hashes - maps Unity texture instance ID to XXH64 hash
        private Dictionary<int, ulong> textureHashCache = new Dictionary<int, ulong>();
        
        // Cache for materials - maps Unity material instance ID to Remix material handle
        private Dictionary<int, IntPtr> materialCache = new Dictionary<int, IntPtr>();
        
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
                remixMaterialHandle = IntPtr.Zero
            };
            
            // Get albedo color
            if (material.HasProperty("_Color"))
            {
                matData.albedoColor = material.GetColor("_Color");
            }
            
            // Upload albedo texture
            if (captureTextures.Value && material.HasProperty("_MainTex"))
            {
                var tex = material.GetTexture("_MainTex") as Texture2D;
                if (tex != null)
                {
                    matData.albedoHandle = UploadUnityTexture(tex, srgb: true);
                    if (matData.albedoHandle != IntPtr.Zero)
                    {
                        int texId = tex.GetInstanceID();
                        if (textureHashCache.TryGetValue(texId, out ulong hash))
                        {
                            matData.albedoTextureHash = hash;
                        }
                        //logger.LogInfo($"Captured albedo texture for material '{material.name}' (hash: 0x{matData.albedoTextureHash:X16})");
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
                
                // Compute XXH64 hash
                ulong textureHash = XXHash64.ComputeHash(hashSourceData, 0, hashSourceData.Length);
                if (textureHash == 0) textureHash = 1;
                
                textureHashCache[texId] = textureHash;
                logger.LogInfo($"Computed XXH64 hash for '{unityTexture.name}': 0x{textureHash:X16}");
                
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
        
        private IntPtr CreateRemixMaterialSimple(ref MaterialTextureData matData, int materialId)
        {
            if (createMaterialFunc == null)
                return IntPtr.Zero;
                
            try
            {
                string albedoPath = GetTexturePathFromHandle(matData.albedoHandle);
                string normalPath = GetTexturePathFromHandle(matData.normalHandle);
                ulong matHash = GenerateMaterialHash(matData.materialName, materialId);
                
                // Log texture paths for debugging
                logger.LogInfo($"Creating material '{matData.materialName}': albedo={albedoPath ?? "none"}, normal={normalPath ?? "none"}");
                
                var materialInfo = new RemixAPI.remixapi_MaterialInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO,
                    pNext = IntPtr.Zero,
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
                    filterMode = 0,
                    wrapModeU = 0,
                    wrapModeV = 0
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
            catch (Exception ex)
            {
                logger.LogError($"Exception creating material: {ex.Message}");
                return IntPtr.Zero;
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
