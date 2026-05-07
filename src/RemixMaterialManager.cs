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
        private readonly BepInEx.Configuration.ConfigEntry<bool> verboseTextureLogging;
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
        
        // DXT5nm Z reconstruction: zLookup[x * 256 + y] = byte Z given X,Y normal components.
        // Precomputed once to avoid per-pixel sqrt during normal map unpacking.
        private static readonly byte[] zLookup = BuildZLookup();
        private static byte[] BuildZLookup()
        {
            var t = new byte[256 * 256];
            for (int x = 0; x < 256; x++)
            {
                float nx = x / 127.5f - 1f;
                float nx2 = nx * nx;
                for (int y = 0; y < 256; y++)
                {
                    float ny = y / 127.5f - 1f;
                    float nz2 = 1f - nx2 - ny * ny;
                    float nz = nz2 > 0f ? (float)Math.Sqrt(nz2) : 0f;
                    t[x * 256 + y] = (byte)(nz * 127.5f + 127.5f);
                }
            }
            return t;
        }
        
        // Cache for texture hashes - maps Unity texture instance ID to XXH64 hash
        private Dictionary<int, ulong> textureHashCache = new Dictionary<int, ulong>();
        
        // Track which textures have meaningful alpha (not all-opaque)
        private HashSet<int> texturesWithAlpha = new HashSet<int>();
        
        // Track textures with genuine cutout transparency (large regions of near-zero alpha).
        // Distinguished from smoothness-as-alpha (gradual values) used in Standard shader Opaque mode.
        private HashSet<int> texturesWithCutoutAlpha = new HashSet<int>();
        
        // Cache for materials - maps Unity material instance ID to Remix material handle
        private Dictionary<int, IntPtr> materialCache = new Dictionary<int, IntPtr>();

        // Track materials that fell back to the debug placeholder texture (no albedo)
        private readonly HashSet<string> placeholderMaterialNames = new HashSet<string>();
        
        // Cache for solid-color 1x1 textures — maps RGBA32 key to (provisional handle = hash as IntPtr)
        private Dictionary<uint, IntPtr> solidColorTextureCache = new Dictionary<uint, IntPtr>();
        
        // Cache for tinted emissive textures - maps (texId ^ tintColorKey) to (handle, hash)
        private Dictionary<long, (IntPtr handle, ulong hash)> tintedTextureCache = new Dictionary<long, (IntPtr, ulong)>();
        
        // Deferred texture upload queue — main thread prepares pixel data, render thread calls Remix API.
        // Prevents cross-thread DXVK lock contention between CreateTexture (s_mutex→devLock) and Present (devLock→submission).
        private struct PendingTextureUpload
        {
            public int texId;            // Unity texture instance ID (-1 for tinted/SDF entries)
            public long tintedCacheKey;  // Tinted texture cache key (0 for regular textures)
            public byte[] pixelData;
            public ulong hash;
            public uint width, height, mipLevels;
            public RemixAPI.remixapi_Format format;
        }
        private readonly Queue<PendingTextureUpload> pendingTextureUploads = new Queue<PendingTextureUpload>();
        private readonly HashSet<int> pendingTextureIds = new HashSet<int>();
        private readonly HashSet<long> pendingTintedKeys = new HashSet<long>();
        private readonly object pendingTextureLock = new object();
        
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
            public Vector4 mainTexST; // _MainTex_ST: (scaleU, scaleV, offsetU, offsetV)
            public IntPtr emissiveHandle;
            public ulong emissiveTextureHash;
            public Color emissiveColor;    // HDR emission color (can exceed 1.0)
            public float emissiveIntensity;
            public bool useEmissiveBlend;  // Use kAlphaEmissive blend instead of kAlpha
        }
        private System.Collections.Concurrent.ConcurrentDictionary<int, MaterialTextureData> materialTextureData = new System.Collections.Concurrent.ConcurrentDictionary<int, MaterialTextureData>();
        private HashSet<string> loggedShaderProperties = new HashSet<string>();
        
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
            BepInEx.Configuration.ConfigEntry<bool> verboseTextureLogging,
            RemixAPI.remixapi_Interface remixInterface,
            object apiLock)
        {
            this.logger = logger;
            this.textureCategoryManager = categoryManager;
            this.captureTextures = captureTextures;
            this.captureMaterials = captureMaterials;
            this.verboseTextureLogging = verboseTextureLogging;
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
        /// Get _MainTex_ST (tiling/offset) for a material. Returns (1,1,0,0) if unknown.
        /// </summary>
        public Vector4 GetMainTexST(int materialId)
        {
            if (materialTextureData.TryGetValue(materialId, out var data))
                return data.mainTexST;
            return new Vector4(1, 1, 0, 0);
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
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Registered texture 0x{textureHash:X16} to category '{categoryName}'");
            }
            else
            {
                logger.LogError($"Failed to register texture to category: {result}");
            }
        }
        
        /// <summary>
        /// Capture textures from a Unity material, with optional per-renderer property overrides
        /// </summary>
        public void CaptureMaterialTextures(Material material, int materialId, Color? mpbEmissiveColor = null, float? mpbEmissiveIntensity = null)
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
                filterMode = 1, // MDL Linear
                mainTexST = new Vector4(1, 1, 0, 0),
                emissiveHandle = IntPtr.Zero,
                emissiveTextureHash = 0,
                emissiveColor = Color.black,
                emissiveIntensity = 0f
            };
            
            // Get albedo color
            if (material.HasProperty("_Color"))
            {
                matData.albedoColor = material.GetColor("_Color");
            }
            
            // Capture texture tiling/offset
            if (material.HasProperty("_MainTex"))
            {
                matData.mainTexST = new Vector4(
                    material.mainTextureScale.x,
                    material.mainTextureScale.y,
                    material.mainTextureOffset.x,
                    material.mainTextureOffset.y);
            }
            
            // Detect alpha mode from shader keywords, _Mode property, and render queue
            var (detectedMode, detectionReason) = DetectAlphaModeWithReason(material);
            matData.alphaMode = detectedMode;
            if (material.HasProperty("_Cutoff"))
            {
                matData.alphaCutoff = material.GetFloat("_Cutoff");
            }
            
            // Detailed diagnostic log for every material
            if (verboseTextureLogging.Value)
                logger.LogInfo($"[MaterialDiag] '{material.name}' shader='{material.shader?.name}' queue={material.renderQueue} " +
                    $"alphaMode={matData.alphaMode} reason={detectionReason} " +
                    $"color=({matData.albedoColor.r:F3},{matData.albedoColor.g:F3},{matData.albedoColor.b:F3},{matData.albedoColor.a:F3}) " +
                    $"cutoff={matData.alphaCutoff:F3}");
            
            // Upload albedo texture
            Texture2D albedoTex = null;
            string shaderName = material.shader != null ? material.shader.name : "null";
            if (captureTextures.Value && material.HasProperty("_MainTex"))
            {
                var tex = material.GetTexture("_MainTex") as Texture2D;
                albedoTex = tex;
                if (tex != null)
                {
                    // Read Unity wrap/filter modes from texture and convert to MDL values
                    matData.wrapModeU = UnityWrapToMdl(tex.wrapModeU);
                    matData.wrapModeV = UnityWrapToMdl(tex.wrapModeV);
                    matData.filterMode = (byte)(tex.filterMode == FilterMode.Point ? 0 : 1);
                    
                    matData.albedoHandle = UploadUnityTexture(tex);
                    if (matData.albedoHandle != IntPtr.Zero)
                    {
                        int texId = tex.GetInstanceID();
                        if (textureHashCache.TryGetValue(texId, out ulong hash))
                        {
                            matData.albedoTextureHash = hash;
                        }
                        
                        // Fallback: if shader metadata says Opaque but the texture has genuine cutout
                        // transparency (large near-zero alpha regions), upgrade to Cutout.
                        // Uses texturesWithCutoutAlpha (strict: >=10% pixels at alpha<16) instead of
                        // texturesWithAlpha (loose: any pixel<250) to avoid false positives from
                        // smoothness-as-alpha in Standard shader Opaque mode.
                        if (matData.alphaMode == AlphaMode.Opaque && texturesWithCutoutAlpha.Contains(texId)
                            && (shaderName == null || shaderName.IndexOf("Opaque", StringComparison.OrdinalIgnoreCase) < 0))
                        {
                            matData.alphaMode = AlphaMode.Cutout;
                            if (!material.HasProperty("_Cutoff"))
                                matData.alphaCutoff = 0.5f;
                            if (verboseTextureLogging.Value)
                                logger.LogInfo($"[AlphaFallback] '{material.name}': texture has alpha content, upgrading Opaque -> Cutout (cutoff={matData.alphaCutoff:F2})");
                        }
                    }
                }
            }
            
            // Fallback: no albedo texture but material has a color — create a 1x1 solid-color texture
            // so Remix renders the surface with the correct color instead of the debug checkerboard.
            if (matData.albedoHandle == IntPtr.Zero && material.HasProperty("_Color"))
            {
                matData.albedoHandle = GetOrCreateSolidColorTexture(matData.albedoColor);
                if (matData.albedoHandle != IntPtr.Zero)
                {
                    ulong h = (ulong)matData.albedoHandle.ToInt64();
                    matData.albedoTextureHash = h;
                }
            }
            
            // Dump shader properties once per shader to discover emission/color property names
            if (material.shader != null && !loggedShaderProperties.Contains(shaderName))
            {
                loggedShaderProperties.Add(shaderName);
                var sb = new System.Text.StringBuilder();
                sb.Append($"[ShaderProps] '{shaderName}': ");
                int propCount = material.shader.GetPropertyCount();
                for (int p = 0; p < propCount; p++)
                {
                    var pName = material.shader.GetPropertyName(p);
                    var pType = material.shader.GetPropertyType(p);
                    sb.Append($"{pName}({pType}) ");
                }
                if (verboseTextureLogging.Value)
                    logger.LogInfo(sb.ToString());
            }
            
            // Upload emission — three shader paths:
            // 1. Standard/URP: _EMISSION keyword gates emission; _EmissionColor + _EmissionMap
            // 2. ULTRAKILL/Master: _EmissiveColor + _EmissiveTex + _EmissiveIntensity + EMISSIVE toggle
            // 3. Generic custom: _EmissionColor + _EmissionMultiplier (e.g. Dark Machine/SHDR_Base)
            bool hasEmission = false;
            if (captureTextures.Value)
            {
                // Standard shader path — require _EMISSION keyword or an actual _EmissionMap texture.
                // HasProperty("_EmissionColor") alone is NOT sufficient: most shaders define it
                // even when emission is disabled, often with a default white value that would
                // cause every surface to glow.
                bool hasActiveEmissionMap = material.HasProperty("_EmissionMap") && material.GetTexture("_EmissionMap") != null;
                if (material.IsKeywordEnabled("_EMISSION") || hasActiveEmissionMap)
                {
                    if (material.HasProperty("_EmissionColor"))
                    {
                        matData.emissiveColor = material.GetColor("_EmissionColor");
                        float maxChannel = Mathf.Max(matData.emissiveColor.r, Mathf.Max(matData.emissiveColor.g, matData.emissiveColor.b));
                        // Store 1.0 as intensity — CreateRemixMaterialSimple already folds maxChannel into
                        // emCombinedIntensity (= maxCh * emissiveIntensity), so storing maxChannel here
                        // would double it. The HDR brightness lives in the color, not the intensity.
                        matData.emissiveIntensity = maxChannel > 0f ? 1.0f : 0f;
                    }
                    if (hasActiveEmissionMap)
                    {
                        var emTex = material.GetTexture("_EmissionMap") as Texture2D;
                        if (emTex != null)
                        {
                            var (emHandle, emHash) = UploadTintedEmissiveTexture(emTex, matData.emissiveColor);
                            matData.emissiveHandle = emHandle;
                            matData.emissiveTextureHash = emHash;
                        }
                    }
                    hasEmission = matData.emissiveIntensity > 0f || matData.emissiveHandle != IntPtr.Zero;
                }
                
                // ULTRAKILL/custom shader path: _EmissiveColor, _EmissiveTex, _EmissiveIntensity
                // Only emit if: toggle is on, OR a dedicated emission texture is assigned, OR MPB override is present
                if (!hasEmission && material.HasProperty("_EmissiveColor"))
                {
                    bool emissiveToggle = true;
                    if (material.HasProperty("EMISSIVE"))
                        emissiveToggle = material.GetFloat("EMISSIVE") > 0.5f;
                    
                    // A dedicated _EmissiveTex overrides the toggle (artist assigned a glow map)
                    bool hasEmissiveTex = false;
                    if (material.HasProperty("_EmissiveTex"))
                        hasEmissiveTex = material.GetTexture("_EmissiveTex") != null;
                    
                    // MPB color override also triggers emission
                    bool hasMpbOverride = mpbEmissiveColor.HasValue;
                    
                    if (emissiveToggle || hasEmissiveTex || hasMpbOverride)
                    {
                        matData.emissiveColor = mpbEmissiveColor ?? material.GetColor("_EmissiveColor");
                        
                        if (mpbEmissiveIntensity.HasValue)
                            matData.emissiveIntensity = mpbEmissiveIntensity.Value;
                        else if (material.HasProperty("_EmissiveIntensity"))
                            matData.emissiveIntensity = material.GetFloat("_EmissiveIntensity");
                        else
                        {
                            float maxCh = Mathf.Max(matData.emissiveColor.r, Mathf.Max(matData.emissiveColor.g, matData.emissiveColor.b));
                            matData.emissiveIntensity = maxCh;
                        }
                        
                        // Upload _EmissiveTex if present, pre-tinted by emission color
                        if (hasEmissiveTex)
                        {
                            var emTex = material.GetTexture("_EmissiveTex") as Texture2D;
                            if (emTex != null)
                            {
                                var (emHandle, emHash) = UploadTintedEmissiveTexture(emTex, matData.emissiveColor);
                                matData.emissiveHandle = emHandle;
                                matData.emissiveTextureHash = emHash;
                            }
                        }
                        
                        // _UseAlbedoAsEmissive only when toggle is on (not just default property value)
                        if (emissiveToggle && matData.emissiveHandle == IntPtr.Zero
                            && material.HasProperty("_UseAlbedoAsEmissive")
                            && material.GetFloat("_UseAlbedoAsEmissive") > 0.5f
                            && matData.albedoHandle != IntPtr.Zero)
                        {
                            if (albedoTex != null)
                            {
                                var (emHandle, emHash) = UploadTintedEmissiveTexture(albedoTex, matData.emissiveColor);
                                matData.emissiveHandle = emHandle;
                                matData.emissiveTextureHash = emHash;
                            }
                            else
                            {
                                matData.emissiveHandle = matData.albedoHandle;
                                matData.emissiveTextureHash = matData.albedoTextureHash;
                            }
                        }
                        
                        hasEmission = matData.emissiveIntensity > 0f && matData.emissiveHandle != IntPtr.Zero;
                    }
                }
                
                // Generic custom shader path: _EmissionMultiplier + _Emission texture (e.g. Dark Machine/SHDR_Base)
                // Requires an actual texture — the multiplier alone with default _EmissionColor is not
                // a reliable gate since many shaders default _EmissionMultiplier to 1.0.
                if (!hasEmission && material.HasProperty("_EmissionMultiplier") && material.HasProperty("_Emission"))
                {
                    var emTex = material.GetTexture("_Emission") as Texture2D;
                    float multiplier = material.GetFloat("_EmissionMultiplier");
                    if (emTex != null && multiplier > 0f)
                    {
                        matData.emissiveColor = material.HasProperty("_EmissionColor")
                            ? material.GetColor("_EmissionColor")
                            : Color.white;
                        matData.emissiveIntensity = multiplier;
                        
                        var (emHandle, emHash) = UploadTintedEmissiveTexture(emTex, matData.emissiveColor);
                        matData.emissiveHandle = emHandle;
                        matData.emissiveTextureHash = emHash;
                        
                        hasEmission = true;
                    }
                }
                
                // Stanley Parable custom emissive shaders: emission texture in _TextureSample1 or _TextureSample2,
                // intensity in _Emission (Range), with optional _Useemissionmaponly toggle.
                // These shaders have "Emissive" in the name but use non-standard property names.
                if (!hasEmission && shaderName.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Find emission texture: _TextureSample1, _TextureSample2, or fall back to albedo
                    Texture2D stanleyEmTex = null;
                    string emTexProp = null;
                    foreach (var prop in new[] { "_TextureSample1", "_TextureSample2" })
                    {
                        if (material.HasProperty(prop))
                        {
                            stanleyEmTex = material.GetTexture(prop) as Texture2D;
                            if (stanleyEmTex != null) { emTexProp = prop; break; }
                        }
                    }
                    
                    // Read intensity from _Emission (Range) if present, default to 1.0
                    float emIntensity = 1.0f;
                    if (material.HasProperty("_Emission"))
                        emIntensity = material.GetFloat("_Emission");
                    
                    if (emIntensity > 0f)
                    {
                        matData.emissiveColor = Color.white;
                        matData.emissiveIntensity = emIntensity;
                        
                        if (stanleyEmTex != null)
                        {
                            matData.emissiveHandle = UploadUnityTexture(stanleyEmTex);
                            if (matData.emissiveHandle != IntPtr.Zero)
                            {
                                int emTexId = stanleyEmTex.GetInstanceID();
                                if (textureHashCache.TryGetValue(emTexId, out ulong emHash))
                                    matData.emissiveTextureHash = emHash;
                            }
                        }
                        else if (albedoTex != null)
                        {
                            // No dedicated emission texture — use albedo as emission source
                            matData.emissiveHandle = matData.albedoHandle;
                            matData.emissiveTextureHash = matData.albedoTextureHash;
                        }
                        
                        hasEmission = matData.emissiveHandle != IntPtr.Zero;
                        if (hasEmission && verboseTextureLogging.Value)
                            logger.LogInfo($"[StanleyEmission] '{material.name}': tex={emTexProp ?? "albedo"} intensity={emIntensity:F3}");
                    }
                }
                
                // TextMeshPro Distance Field: self-lit UI text — use _FaceColor as emissive tint.
                // SDF atlases are typically Alpha8 format: after GPU readback the glyph shapes
                // live in the alpha channel while RGB is black. We must convert alpha → RGB
                // so Remix sees bright glyph pixels in the emissive texture.
                // The albedo from Alpha8 readback is (0,0,0,A), so reflected light contributes
                // nothing — emission is the only light source for these glyphs. Use a high
                // intensity so text appears clearly self-lit in the path-traced scene.
                if (!hasEmission && shaderName.Contains("TextMeshPro/Distance Field"))
                {
                    Color faceColor = material.HasProperty("_FaceColor")
                        ? material.GetColor("_FaceColor")
                        : Color.white;
                    float maxCh = Mathf.Max(faceColor.r, Mathf.Max(faceColor.g, faceColor.b));
                    if (maxCh > 0f && albedoTex != null)
                    {
                        var (emHandle, emHash) = UploadSDFEmissiveTexture(albedoTex, faceColor);
                        if (emHandle != IntPtr.Zero)
                        {
                            matData.emissiveColor = faceColor;
                            matData.emissiveIntensity = 20.0f;
                            matData.emissiveHandle = emHandle;
                            matData.emissiveTextureHash = emHash;
                            hasEmission = true;
                            
                            // Also replace albedo with the alpha→RGB texture so glyphs
                            // are directly visible, not just in reflections. The original
                            // Alpha8 readback produces (0,0,0,A) which renders as
                            // transparent black in direct view.
                            matData.albedoHandle = emHandle;
                            matData.albedoTextureHash = emHash;
                            matData.useEmissiveBlend = true;
                        }
                    }
                }
                
                // Safety net: if no path confirmed real emission, ensure emissive state is clean.
                // Prevents stale values from a path that read properties but decided not to emit.
                if (!hasEmission)
                {
                    matData.emissiveColor = Color.black;
                    matData.emissiveIntensity = 0f;
                    matData.emissiveHandle = IntPtr.Zero;
                    matData.emissiveTextureHash = 0;
                }
                else if (verboseTextureLogging.Value)
                {
                    logger.LogInfo($"[Emission] '{material.name}': color=({matData.emissiveColor.r:F3},{matData.emissiveColor.g:F3},{matData.emissiveColor.b:F3}) " +
                        $"intensity={matData.emissiveIntensity:F3} texHash=0x{matData.emissiveTextureHash:X16}");
                }
            }
            
            // Upload normal map
            if (captureTextures.Value && material.HasProperty("_BumpMap"))
            {
                var tex = material.GetTexture("_BumpMap") as Texture2D;
                if (tex != null)
                {
                    matData.normalHandle = UploadUnityTexture(tex, isNormalMap: true);
                    if (matData.normalHandle != IntPtr.Zero)
                    {
                        int texId = tex.GetInstanceID();
                        if (textureHashCache.TryGetValue(texId, out ulong hash))
                        {
                            matData.normalTextureHash = hash;
                        }
                        if (verboseTextureLogging.Value)
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
            logger.LogInfo($"[MatCapture] '{material.name}' shader='{material.shader?.name}' albedo={albedoPath ?? "NONE"} normal={normalPath ?? "none"}");
        }
        
        /// <summary>
        /// Get or create a 1x1 solid-color texture for materials that only use _Color with no _MainTex.
        /// Defers the actual upload to the render thread like regular textures.
        /// Returns the provisional handle (hash as IntPtr).
        /// </summary>
        private IntPtr GetOrCreateSolidColorTexture(Color color)
        {
            byte r = (byte)(Mathf.Clamp01(color.r) * 255f);
            byte g = (byte)(Mathf.Clamp01(color.g) * 255f);
            byte b = (byte)(Mathf.Clamp01(color.b) * 255f);
            byte a = (byte)(Mathf.Clamp01(color.a) * 255f);
            uint colorKey = (uint)(r | (g << 8) | (b << 16) | (a << 24));
            
            lock (pendingTextureLock)
            {
                if (solidColorTextureCache.TryGetValue(colorKey, out IntPtr cached))
                    return cached;
            }
            
            byte[] pixels = new byte[] { r, g, b, a };
            ulong hash = XXHash64.ComputeHash(pixels, 0, 4);
            // Mix in a sentinel so solid-color hashes don't collide with real texture hashes
            hash ^= 0x50C0_10C0_10C0_10C0UL;
            if (hash == 0) hash = 1;
            
            lock (pendingTextureLock)
            {
                pendingTextureUploads.Enqueue(new PendingTextureUpload
                {
                    texId = -1,
                    tintedCacheKey = 0,
                    pixelData = pixels,
                    hash = hash,
                    width = 1,
                    height = 1,
                    mipLevels = 1,
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                });
                
                var handle = new IntPtr((long)hash);
                solidColorTextureCache[colorKey] = handle;
                return handle;
            }
        }
        
        /// <summary>
        /// Prepare a Unity texture for Remix upload. Pixel data is captured on the calling thread,
        /// but the actual Remix API call is deferred to the render thread via ProcessPendingTextureUploads().
        /// Returns a provisional handle (the texture hash cast to IntPtr).
        /// </summary>
        public IntPtr UploadUnityTexture(Texture2D unityTexture, bool isNormalMap = false)
        {
            if (unityTexture == null || createTextureFunc == null)
                return IntPtr.Zero;
                
            int texId = unityTexture.GetInstanceID();
            
            // Check cache and pending queue (synchronized with render thread)
            lock (pendingTextureLock)
            {
                if (textureCache.TryGetValue(texId, out IntPtr cachedHandle))
                    return cachedHandle;
                if (pendingTextureIds.Contains(texId))
                {
                    textureHashCache.TryGetValue(texId, out ulong ph);
                    return ph != 0 ? new IntPtr((long)ph) : IntPtr.Zero;
                }
            }
            
            try
            {
                // Get raw texture data
                byte[] pixelData;
                byte[] hashSourceData;
                RemixAPI.remixapi_Format format;
                uint actualMipLevels = (uint)unityTexture.mipmapCount;
                
                // DXT5nm normal maps pack X in alpha and Y in green. Raw DXT5 upload
                // would pass the packed channels to Remix unchanged, so we must decompress
                // and unswizzle. Readable textures use GetPixels32 (CPU); non-readable use GPU readback.
                bool isDXT5nm = isNormalMap &&
                    (unityTexture.format == TextureFormat.DXT5 || unityTexture.format == TextureFormat.DXT5Crunched);
                
                // Readable DXT5nm: decompress on CPU to avoid GPU stall
                if (isDXT5nm && unityTexture.isReadable)
                {
                    Color32[] pixels = unityTexture.GetPixels32();
                    pixelData = new byte[pixels.Length * 4];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        byte x = pixels[i].a; // alpha = X
                        byte y = pixels[i].g; // green = Y
                        pixelData[i * 4 + 0] = x;
                        pixelData[i * 4 + 1] = y;
                        pixelData[i * 4 + 2] = zLookup[x * 256 + y];
                        pixelData[i * 4 + 3] = 255;
                    }
                    hashSourceData = pixelData;
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                    if (verboseTextureLogging.Value)
                        logger.LogInfo($"Unpacked readable DXT5nm normal map '{unityTexture.name}' ({unityTexture.width}x{unityTexture.height})");
                }
                // Handle non-readable textures via GPU readback
                else if (!unityTexture.isReadable)
                {
                    if (verboseTextureLogging.Value)
                        logger.LogInfo($"Texture '{unityTexture.name}' is not readable - forcing GPU readback");
                    
                    RenderTexture tmp = RenderTexture.GetTemporary(
                        unityTexture.width, unityTexture.height, 0,
                        RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    
                    RenderTexture previous = RenderTexture.active;
                    Graphics.Blit(unityTexture, tmp);
                    RenderTexture.active = tmp;
                    
                    Texture2D readableTexture = new Texture2D(unityTexture.width, unityTexture.height, TextureFormat.RGBA32, false, true);
                    readableTexture.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                    readableTexture.Apply();
                    
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(tmp);
                    
                    pixelData = readableTexture.GetRawTextureData();
                    
                    // DXT5nm: X stored in alpha, Y stored in green.
                    // Reconstruct standard tangent-space normal map: R=X, G=Y, B=Z, A=255.
                    if (isDXT5nm)
                    {
                        for (int i = 0; i < pixelData.Length; i += 4)
                        {
                            byte x = pixelData[i + 3]; // alpha = X
                            byte y = pixelData[i + 1]; // green = Y
                            pixelData[i + 0] = x;      // R = X
                            pixelData[i + 1] = y;      // G = Y (unchanged)
                            pixelData[i + 2] = zLookup[x * 256 + y]; // B = Z
                            pixelData[i + 3] = 255;
                        }
                        if (verboseTextureLogging.Value)
                            logger.LogInfo($"Unpacked DXT5nm normal map '{unityTexture.name}' ({unityTexture.width}x{unityTexture.height})");
                    }
                    
                    hashSourceData = pixelData;
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                    
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
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                }
                else
                {
                    // Use raw data for supported formats
                    pixelData = unityTexture.GetRawTextureData();
                    hashSourceData = pixelData;
                    
                    switch (unityTexture.format)
                    {
                        case TextureFormat.RGBA32:
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                            break;
                        case TextureFormat.BGRA32:
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_B8G8R8A8_UNORM;
                            break;
                        case TextureFormat.DXT1:
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC1_RGB_UNORM;
                            break;
                        case TextureFormat.DXT5:
                            format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC3_UNORM;
                            break;
                        default:
                            logger.LogWarning($"Unsupported texture format: {unityTexture.format}");
                            return IntPtr.Zero;
                    }
                }
                
                // Check alpha channel content for uncompressed RGBA formats
                bool hasAlpha = false;
                if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM)
                {
                    hasAlpha = SampleAlphaRGBA(pixelData, alphaOffset: 3, stride: 4);
                }
                else if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_B8G8R8A8_UNORM)
                {
                    hasAlpha = SampleAlphaRGBA(pixelData, alphaOffset: 3, stride: 4);
                }
                else if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC3_UNORM)
                {
                    hasAlpha = SampleAlphaBC3(pixelData);
                }
                // BC1 (DXT1) has only 1-bit punch-through alpha, treat as opaque
                
                if (hasAlpha)
                {
                    texturesWithAlpha.Add(texId);
                    
                    // Check for genuine cutout transparency: large regions of near-zero alpha.
                    // This distinguishes real transparency (decal backgrounds at alpha=0) from
                    // smoothness-as-alpha packing (Standard shader Opaque mode, values ~50-230).
                    bool hasCutoutAlpha;
                    if (format == RemixAPI.remixapi_Format.REMIXAPI_FORMAT_BC3_UNORM)
                        hasCutoutAlpha = SampleCutoutAlphaBC3(pixelData);
                    else
                        hasCutoutAlpha = SampleCutoutAlphaRGBA(pixelData, alphaOffset: 3, stride: 4);
                    if (hasCutoutAlpha)
                        texturesWithCutoutAlpha.Add(texId);
                }
                
                // Compute XXH64 hash
                ulong textureHash = XXHash64.ComputeHash(hashSourceData, 0, hashSourceData.Length);
                if (textureHash == 0) textureHash = 1;
                
                textureHashCache[texId] = textureHash;
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Computed XXH64 hash for '{unityTexture.name}': 0x{textureHash:X16} hasAlpha={hasAlpha} fmt={unityTexture.format}");
                
                // Queue for deferred upload on the render thread
                lock (pendingTextureLock)
                {
                    pendingTextureUploads.Enqueue(new PendingTextureUpload
                    {
                        texId = texId,
                        tintedCacheKey = 0,
                        pixelData = pixelData,
                        hash = textureHash,
                        width = (uint)unityTexture.width,
                        height = (uint)unityTexture.height,
                        mipLevels = actualMipLevels,
                        format = format
                    });
                    pendingTextureIds.Add(texId);
                }
                
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Queued texture '{unityTexture.name}' for render thread upload (hash: 0x{textureHash:X16})");
                
                return new IntPtr((long)textureHash);
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception uploading texture '{unityTexture.name}': {ex.Message}");
                return IntPtr.Zero;
            }
        }
        
        /// <summary>
        /// Upload an emissive texture pre-tinted by the emission color.
        /// Remix's shader ignores emissiveColorConstant when a texture is present,
        /// so we bake the color multiplication into the texture pixels.
        /// Returns (handle, hash) for the uploaded texture.
        /// </summary>
        private (IntPtr handle, ulong hash) UploadTintedEmissiveTexture(Texture2D tex, Color emissiveColor)
        {
            if (tex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);
            
            // Compute normalized tint direction (all components <= 1.0)
            float maxCh = Mathf.Max(emissiveColor.r, Mathf.Max(emissiveColor.g, emissiveColor.b));
            float tintR = maxCh > 0f ? emissiveColor.r / maxCh : 1f;
            float tintG = maxCh > 0f ? emissiveColor.g / maxCh : 1f;
            float tintB = maxCh > 0f ? emissiveColor.b / maxCh : 1f;
            
            // If tint is white, use normal upload path (benefits from per-texId caching)
            if (tintR > 0.99f && tintG > 0.99f && tintB > 0.99f)
            {
                var handle = UploadUnityTexture(tex);
                int texId = tex.GetInstanceID();
                textureHashCache.TryGetValue(texId, out ulong h);
                return (handle, h);
            }
            
            // Compound cache key: texture ID + quantized tint color
            int tintKey = ((int)(tintR * 255) << 16) | ((int)(tintG * 255) << 8) | (int)(tintB * 255);
            long cacheKey = ((long)tex.GetInstanceID() << 24) ^ (uint)tintKey;
            
            lock (pendingTextureLock)
            {
                if (tintedTextureCache.TryGetValue(cacheKey, out var cached))
                    return cached;
                if (pendingTintedKeys.Contains(cacheKey))
                    return (IntPtr.Zero, 0); // Will be available after render thread processes it
            }
            
            try
            {
                Color32[] pixels;
                if (tex.isReadable)
                {
                    pixels = tex.GetPixels32();
                }
                else
                {
                    RenderTexture tmp = RenderTexture.GetTemporary(
                        tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    RenderTexture prev = RenderTexture.active;
                    Graphics.Blit(tex, tmp);
                    RenderTexture.active = tmp;
                    Texture2D readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
                    readable.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                    readable.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(tmp);
                    pixels = readable.GetPixels32();
                    UnityEngine.Object.Destroy(readable);
                }
                
                // Tint each pixel by the normalized emission color direction
                byte tR = (byte)(tintR * 255f);
                byte tG = (byte)(tintG * 255f);
                byte tB = (byte)(tintB * 255f);
                
                byte[] pixelData = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixelData[i * 4 + 0] = (byte)((pixels[i].r * tR) / 255);
                    pixelData[i * 4 + 1] = (byte)((pixels[i].g * tG) / 255);
                    pixelData[i * 4 + 2] = (byte)((pixels[i].b * tB) / 255);
                    pixelData[i * 4 + 3] = pixels[i].a;
                }
                
                ulong hash = XXHash64.ComputeHash(pixelData, 0, pixelData.Length);
                if (hash == 0) hash = 1;
                
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Computed tinted emissive hash for '{tex.name}' tint=({tintR:F2},{tintG:F2},{tintB:F2}): 0x{hash:X16}");
                
                // Queue for deferred upload on the render thread
                lock (pendingTextureLock)
                {
                    pendingTextureUploads.Enqueue(new PendingTextureUpload
                    {
                        texId = -1,
                        tintedCacheKey = cacheKey,
                        pixelData = pixelData,
                        hash = hash,
                        width = (uint)tex.width,
                        height = (uint)tex.height,
                        mipLevels = 1,
                        format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                    });
                    pendingTintedKeys.Add(cacheKey);
                }
                
                return (new IntPtr((long)hash), hash);
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception uploading tinted emissive '{tex.name}': {ex.Message}");
                return (IntPtr.Zero, 0);
            }
        }
        
        /// <summary>
        /// Upload an SDF atlas as an emissive texture by converting alpha → tinted RGB.
        /// Alpha8 SDF atlases blit to RGBA as (0,0,0,glyph), so the standard upload
        /// produces a black RGB texture. This method reads the alpha channel and writes
        /// it into RGB multiplied by the given tint color.
        /// </summary>
        private (IntPtr handle, ulong hash) UploadSDFEmissiveTexture(Texture2D tex, Color tint)
        {
            if (tex == null || createTextureFunc == null)
                return (IntPtr.Zero, 0);
            
            // Cache key: texture ID ^ tint color hash, shifted to avoid collision with tinted emissive cache
            byte tR = (byte)(Mathf.Clamp01(tint.r) * 255f);
            byte tG = (byte)(Mathf.Clamp01(tint.g) * 255f);
            byte tB = (byte)(Mathf.Clamp01(tint.b) * 255f);
            int tintKey = (tR << 16) | (tG << 8) | tB;
            long cacheKey = ~((long)tex.GetInstanceID() << 24) ^ (uint)tintKey; // bitwise NOT to separate from tinted cache
            
            lock (pendingTextureLock)
            {
                if (tintedTextureCache.TryGetValue(cacheKey, out var cached))
                    return cached;
                if (pendingTintedKeys.Contains(cacheKey))
                    return (IntPtr.Zero, 0);
            }
            
            try
            {
                Color32[] pixels;
                if (tex.isReadable)
                {
                    pixels = tex.GetPixels32();
                }
                else
                {
                    RenderTexture tmp = RenderTexture.GetTemporary(
                        tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    RenderTexture prev = RenderTexture.active;
                    Graphics.Blit(tex, tmp);
                    RenderTexture.active = tmp;
                    Texture2D readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
                    readable.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                    readable.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(tmp);
                    pixels = readable.GetPixels32();
                    UnityEngine.Object.Destroy(readable);
                }
                
                // Convert: use max(r, g, b, a) as luminance to handle both Alpha8 (RGB=0, A=glyph)
                // and RGBA atlases. Multiply by tint color.
                byte[] pixelData = new byte[pixels.Length * 4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte lum = (byte)Mathf.Max(pixels[i].r, Mathf.Max(pixels[i].g, Mathf.Max(pixels[i].b, pixels[i].a)));
                    pixelData[i * 4 + 0] = (byte)((lum * tR) / 255);
                    pixelData[i * 4 + 1] = (byte)((lum * tG) / 255);
                    pixelData[i * 4 + 2] = (byte)((lum * tB) / 255);
                    pixelData[i * 4 + 3] = lum; // alpha = glyph shape for blending
                }
                
                ulong hash = XXHash64.ComputeHash(pixelData, 0, pixelData.Length);
                if (hash == 0) hash = 1;
                
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"Computed SDF emissive hash for '{tex.name}' tint=({tR},{tG},{tB}): 0x{hash:X16}");
                
                // Queue for deferred upload on the render thread
                lock (pendingTextureLock)
                {
                    pendingTextureUploads.Enqueue(new PendingTextureUpload
                    {
                        texId = -1,
                        tintedCacheKey = cacheKey,
                        pixelData = pixelData,
                        hash = hash,
                        width = (uint)tex.width,
                        height = (uint)tex.height,
                        mipLevels = 1,
                        format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM
                    });
                    pendingTintedKeys.Add(cacheKey);
                }
                
                return (new IntPtr((long)hash), hash);
            }
            catch (Exception ex)
            {
                logger.LogError($"Exception uploading SDF emissive '{tex.name}': {ex.Message}");
                return (IntPtr.Zero, 0);
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
        /// Check if uncompressed RGBA pixels have genuine cutout transparency: a significant
        /// fraction of pixels with near-zero alpha (< 16). This distinguishes real transparency
        /// (decal backgrounds, cutout shapes) from smoothness-as-alpha packing where values
        /// are typically 50-230.
        /// </summary>
        private static bool SampleCutoutAlphaRGBA(byte[] pixelData, int alphaOffset, int stride)
        {
            int pixelCount = pixelData.Length / stride;
            if (pixelCount == 0) return false;
            
            int sampleCount = Math.Min(pixelCount, 1024);
            int step = Math.Max(1, pixelCount / sampleCount);
            int nearZeroCount = 0;
            int totalSampled = 0;
            
            for (int i = 0; i < pixelCount; i += step)
            {
                int idx = i * stride + alphaOffset;
                if (idx >= pixelData.Length) break;
                totalSampled++;
                if (pixelData[idx] < 16)
                    nearZeroCount++;
            }
            
            // >= 10% of sampled pixels are near-transparent → genuine cutout
            return totalSampled > 0 && nearZeroCount * 10 >= totalSampled;
        }
        
        /// <summary>
        /// Check if a BC3/DXT5 texture has any non-trivial alpha by scanning alpha endpoint
        /// bytes in each 16-byte block. Returns true if any block has an endpoint below 250.
        /// </summary>
        private static bool SampleAlphaBC3(byte[] rawData)
        {
            int blockCount = rawData.Length / 16;
            int step = Math.Max(1, blockCount / 256);
            for (int b = 0; b < blockCount; b += step)
            {
                int offset = b * 16;
                byte a0 = rawData[offset];
                byte a1 = rawData[offset + 1];
                if (a0 < 250 || a1 < 250)
                    return true;
            }
            return false;
        }
        
        /// <summary>
        /// Check if a BC3/DXT5 texture has genuine cutout transparency by scanning alpha
        /// endpoints. Returns true if a significant fraction of blocks have near-zero alpha
        /// endpoints (< 16), indicating real transparency rather than smoothness packing.
        /// </summary>
        private static bool SampleCutoutAlphaBC3(byte[] rawData)
        {
            int blockCount = rawData.Length / 16;
            if (blockCount == 0) return false;
            
            int sampleCount = Math.Min(blockCount, 1024);
            int step = Math.Max(1, blockCount / sampleCount);
            int nearZeroCount = 0;
            int totalSampled = 0;
            
            for (int b = 0; b < blockCount; b += step)
            {
                int offset = b * 16;
                byte a0 = rawData[offset];
                byte a1 = rawData[offset + 1];
                totalSampled++;
                if (a0 < 16 || a1 < 16)
                    nearZeroCount++;
            }
            
            // >= 10% of sampled blocks have near-zero alpha → genuine cutout
            return totalSampled > 0 && nearZeroCount * 10 >= totalSampled;
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
                    {
                        albedoPath = DebugTextureHashPath;
                        lock (placeholderMaterialNames)
                            placeholderMaterialNames.Add(matData.materialName ?? $"mat_{materialId}");
                    }
                }

                string emissivePath = GetTexturePathFromHandle(matData.emissiveHandle);
                if (verboseTextureLogging.Value)
                    logger.LogInfo($"[MaterialCreate] '{matData.materialName}': albedo={albedoPath ?? "none"}, normal={normalPath ?? "none"}, emissive={emissivePath ?? "none"}, emColor=({matData.emissiveColor.r:F3},{matData.emissiveColor.g:F3},{matData.emissiveColor.b:F3}), emIntensity={matData.emissiveIntensity:F3}, alphaMode={matData.alphaMode}");
                
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
                        opaqueExt.blendType_hasvalue = 1; // remixapi_Bool.True
                        // kAlphaEmissive (1) makes the surface directly visible as a
                        // blended surface; kAlpha (0) only contributes indirect light
                        // bounces which makes blended geometry invisible in direct view.
                        opaqueExt.blendType_value = 1;
                        break;
                }
                
                // Log all OpaqueEXT values being sent to Remix
                if (verboseTextureLogging.Value)
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
                    // Emissive color: Remix expects a normalized direction color + scalar intensity.
                    // Compute combined intensity = colorBrightness * separateIntensity, then normalize color.
                    float emMaxCh = Mathf.Max(matData.emissiveColor.r, Mathf.Max(matData.emissiveColor.g, matData.emissiveColor.b));
                    float emCombinedIntensity = emMaxCh > 0f ? matData.emissiveIntensity * emMaxCh : matData.emissiveIntensity;
                    
                    var materialInfo = new RemixAPI.remixapi_MaterialInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO,
                        pNext = opaqueHandle.AddrOfPinnedObject(),
                        hash = matHash,
                        albedoTexture = albedoPath,
                        normalTexture = normalPath,
                        tangentTexture = null,
                        emissiveTexture = emissivePath,
                        emissiveIntensity = emCombinedIntensity,
                        emissiveColorConstant = new RemixAPI.remixapi_Float3D {
                            x = emMaxCh > 0f ? matData.emissiveColor.r / emMaxCh : 0f,
                            y = emMaxCh > 0f ? matData.emissiveColor.g / emMaxCh : 0f,
                            z = emMaxCh > 0f ? matData.emissiveColor.b / emMaxCh : 0f
                        },
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
                    
                    if (verboseTextureLogging.Value)
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
        /// Process queued texture uploads on the render thread.
        /// Must be called before ProcessMeshCreationBatch so textures are available when materials are created.
        /// </summary>
        public void ProcessPendingTextureUploads()
        {
            PendingTextureUpload[] batch;
            lock (pendingTextureLock)
            {
                if (pendingTextureUploads.Count == 0)
                    return;
                batch = pendingTextureUploads.ToArray();
                pendingTextureUploads.Clear();
            }
            
            foreach (var upload in batch)
            {
                GCHandle pinned = GCHandle.Alloc(upload.pixelData, GCHandleType.Pinned);
                try
                {
                    var info = new RemixAPI.remixapi_TextureInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_TEXTURE_INFO,
                        pNext = IntPtr.Zero,
                        hash = upload.hash,
                        width = upload.width,
                        height = upload.height,
                        depth = 1,
                        mipLevels = upload.mipLevels,
                        format = upload.format,
                        data = pinned.AddrOfPinnedObject(),
                        dataSize = (ulong)upload.pixelData.Length
                    };
                    
                    IntPtr handle;
                    RemixAPI.remixapi_ErrorCode result;
                    lock (apiLock) { result = createTextureFunc(ref info, out handle); }
                    
                    if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        lock (pendingTextureLock)
                        {
                            if (upload.texId >= 0)
                            {
                                textureCache[upload.texId] = handle;
                                pendingTextureIds.Remove(upload.texId);
                            }
                            if (upload.tintedCacheKey != 0)
                            {
                                tintedTextureCache[upload.tintedCacheKey] = (handle, upload.hash);
                                pendingTintedKeys.Remove(upload.tintedCacheKey);
                            }
                        }
                    }
                    else
                    {
                        logger.LogError($"Failed to upload deferred texture (hash 0x{upload.hash:X16}): {result}");
                        lock (pendingTextureLock)
                        {
                            if (upload.texId >= 0) pendingTextureIds.Remove(upload.texId);
                            if (upload.tintedCacheKey != 0) pendingTintedKeys.Remove(upload.tintedCacheKey);
                        }
                    }
                }
                finally
                {
                    pinned.Free();
                }
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
                    format = RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM,
                    data = pinned.AddrOfPinnedObject(),
                    dataSize = (ulong)pixels.Length
                };

                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createTextureFunc(ref info, out debugTextureHandle);
                }

                if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogInfo($"Created debug placeholder texture (hash: 0x{debugTextureHash:X16})");
                }
                else
                {
                    logger.LogWarning($"Failed to create debug texture: {result}");
                }
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
            
            // Discard pending uploads
            lock (pendingTextureLock)
            {
                pendingTextureUploads.Clear();
                pendingTextureIds.Clear();
                pendingTintedKeys.Clear();
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

        // --- Diagnostic getters for debug HUD ---
        public int TextureCacheCount => textureCache.Count;
        public int MaterialDataCount => materialTextureData.Count;

        /// <summary>
        /// Log a summary of captured material texture stats.
        /// </summary>
        public void LogMaterialStats()
        {
            int total = materialTextureData.Count;
            int withAlbedo = 0, noAlbedo = 0;
            foreach (var kvp in materialTextureData)
            {
                if (kvp.Value.albedoHandle != IntPtr.Zero)
                    withAlbedo++;
                else
                    noAlbedo++;
            }
            int pendingTex;
            lock (pendingTextureLock) { pendingTex = pendingTextureUploads.Count; }
            logger.LogInfo($"[MaterialStats] {total} materials captured: {withAlbedo} with albedo, {noAlbedo} without | texCache={textureCache.Count} pendingTex={pendingTex}");
        }

        /// <summary>
        /// Returns a snapshot of material names that fell back to the debug placeholder texture.
        /// </summary>
        public string[] GetPlaceholderMaterialNames()
        {
            lock (placeholderMaterialNames)
                return placeholderMaterialNames.Count > 0
                    ? new List<string>(placeholderMaterialNames).ToArray()
                    : Array.Empty<string>();
        }
    }
}
