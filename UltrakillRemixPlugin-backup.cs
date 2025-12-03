using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class UnityRemixPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Unity.remix";
        public const string PluginName = "Unity RTX Remix";
        public const string PluginVersion = "1.0.0";

        // Configuration entries
        private ConfigEntry<string> configCameraName;
        private ConfigEntry<string> configCameraTag;
        private ConfigEntry<bool> configListCameras;
        private ConfigEntry<bool> configUseGameGeometry;
        private ConfigEntry<bool> configUseDistanceCulling;
        private ConfigEntry<float> configMaxRenderDistance;
        private ConfigEntry<int> configRendererCacheDuration;
        private ConfigEntry<int> configDebugLogInterval;
        private ConfigEntry<bool> configEnableLights;
        private ConfigEntry<float> configLightIntensityMultiplier;
        private ConfigEntry<int> configTargetFPS;
        
        public static ManualLogSource LogSource { get; private set; }
        private RemixAPI.remixapi_Interface remixInterface;
        private IntPtr remixDll = IntPtr.Zero;
        private bool remixInitialized = false;
        private bool deviceRegistered = false;
        private bool meshCreated = false;
        private IntPtr testMeshHandle = IntPtr.Zero;
        private int frameCount = 0;
        
        // D3D9 device handles
        private IntPtr d3d9 = IntPtr.Zero;
        private IntPtr d3d9Device = IntPtr.Zero;

        // Function delegates (cached from interface)
        private RemixAPI.PFN_remixapi_Shutdown Shutdown;
        private RemixAPI.PFN_remixapi_CreateMesh CreateMesh;
        private RemixAPI.PFN_remixapi_DestroyMesh DestroyMesh;
        private RemixAPI.PFN_remixapi_SetupCamera SetupCamera;
        private RemixAPI.PFN_remixapi_DrawInstance DrawInstance;
        private RemixAPI.PFN_remixapi_CreateLight CreateLight;
        private RemixAPI.PFN_remixapi_DestroyLight DestroyLight;
        private RemixAPI.PFN_remixapi_DrawLightInstance DrawLightInstance;
        private RemixAPI.PFN_remixapi_dxvk_CreateD3D9 CreateD3D9;
        private RemixAPI.PFN_remixapi_dxvk_RegisterD3D9Device RegisterD3D9Device;
        private RemixAPI.PFN_remixapi_Startup Startup;
        private RemixAPI.PFN_remixapi_Present Present;
        private RemixAPI.PFN_remixapi_CreateTexture CreateTexture;
        private RemixAPI.PFN_remixapi_DestroyTexture DestroyTexture;
        private RemixAPI.PFN_remixapi_CreateMaterial CreateMaterial;
        private RemixAPI.PFN_remixapi_DestroyMaterial DestroyMaterial;
        private RemixAPI.PFN_remixapi_AddTextureHash AddTextureHash;
        private RemixAPI.PFN_remixapi_RemoveTextureHash RemoveTextureHash;
        
        private IntPtr testLightHandle = IntPtr.Zero;
        
        // Texture category manager for tracking which textures belong to which categories
        private TextureCategoryManager textureCategoryManager = new TextureCategoryManager();
        
        // Cache for game meshes - maps Unity mesh instance ID to Remix handle
        private ConcurrentDictionary<int, IntPtr> meshCache = new ConcurrentDictionary<int, IntPtr>();
        private bool useGameGeometry = true;  // Set to true to render game geometry
        
        // Cache for Unity lights - maps Light instance ID to Remix handle
        private Dictionary<int, IntPtr> lightCache = new Dictionary<int, IntPtr>();
        private List<Light> cachedLights = new List<Light>();
        
        // Cache for uploaded textures - maps Unity texture instance ID to Remix handle
        private Dictionary<int, IntPtr> textureCache = new Dictionary<int, IntPtr>();
        
        // Cache for texture hashes - maps Unity texture instance ID to XXH64 hash
        private Dictionary<int, ulong> textureHashCache = new Dictionary<int, ulong>();
        
        // Cache for materials - maps Unity material instance ID to Remix material handle
        private Dictionary<int, IntPtr> materialCache = new Dictionary<int, IntPtr>();
        
        // Material data captured from Unity materials
        private struct MaterialTextureData
        {
            public IntPtr albedoHandle;
            public IntPtr normalHandle;
            public ulong albedoTextureHash;     // XXH64 hash of albedo texture
            public ulong normalTextureHash;     // XXH64 hash of normal texture
            public Color albedoColor;
            public string materialName;
            public IntPtr remixMaterialHandle;  // Created Remix material
        }
        private Dictionary<int, MaterialTextureData> materialTextureData = new Dictionary<int, MaterialTextureData>();
        
        // Renderer caching to avoid expensive FindObjectsOfType calls
        private List<MeshRenderer> cachedRenderers = new List<MeshRenderer>();
        private List<SkinnedMeshRenderer> cachedSkinnedRenderers = new List<SkinnedMeshRenderer>();
        private int rendererCacheFrame = -1;
        private const int RENDERER_CACHE_DURATION = 300; // Rebuild cache every 5 seconds @ 60fps
        
        // Mesh creation batching
        private Queue<Mesh> meshesToCreate = new Queue<Mesh>();
        
        // Track which material each mesh uses (mesh ID -> material ID)
        private Dictionary<int, int> meshToMaterialMap = new Dictionary<int, int>();
        
        // Queue for async material creation
        private Queue<int> pendingMaterialCreation = new Queue<int>();
        private bool materialCreationThreadRunning = false;
        private Thread materialCreationThread = null;
        
        /// <summary>
        /// Get category flags for a mesh based on its material's albedo texture
        /// </summary>
        private uint GetCategoryFlagsForMesh(int meshId)
        {
            // Look up which material this mesh uses
            if (meshToMaterialMap.TryGetValue(meshId, out int materialId))
            {
                // Look up the material's texture data
                if (materialTextureData.TryGetValue(materialId, out MaterialTextureData matData))
                {
                    // Get category flags based on albedo texture hash
                    // This matches how Remix does it: uses getColorTexture().getImageHash()
                    return textureCategoryManager.GetCategoryFlagsForMaterial(matData.albedoTextureHash);
                }
            }
            
            return 0; // No category flags
        }
        
        /// <summary>
        /// Register a texture to a category. This updates both Remix's internal state
        /// and our local category manager so the flags are applied on DrawInstance.
        /// </summary>
        public void RegisterTextureCategory(ulong textureHash, string categoryName)
        {
            if (AddTextureHash == null) return;
            
            // Convert hash to hex string format Remix expects
            string hashString = textureHash.ToString("X");
            
            // Call Remix API to add it to the category
            var result = AddTextureHash(categoryName, hashString);
            
            if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                // Also update our local tracker
                textureCategoryManager.SetTextureCategory(textureHash, categoryName);
                LogSource.LogInfo($"Registered texture 0x{textureHash:X16} to category '{categoryName}'");
            }
            else
            {
                LogSource.LogError($"Failed to register texture to category: {result}");
            }
        }
        
        /// <summary>
        /// Generate stable content-based hash for a mesh that persists across sessions.
        /// This ensures mesh replacements work correctly.
        /// </summary>
        private static ulong GenerateMeshHash(Mesh mesh)
        {
            // Use FNV-1a hash algorithm
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
            
            // Hash vertex count and index count as stable identifiers
            hash ^= (ulong)mesh.vertexCount;
            hash *= 1099511628211UL;
            hash ^= (ulong)mesh.triangles.Length;
            hash *= 1099511628211UL;
            
            // Hash first few vertices for uniqueness (if available)
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
        
        // Distance-based optimization (optional, can interfere with path tracing)
        private bool useDistanceCulling = false; // Set to true to skip very far objects
        private float maxRenderDistanceSqr = 250000f; // 500 units squared
        
        // Camera tracking
        private Camera currentCamera = null;
        private string lastCameraName = "";
        
        // Thread-safe data transfer from Unity main thread to render thread
        private struct CameraData
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
        
        private struct MeshInstanceData
        {
            public int meshId;
            public Matrix4x4 localToWorld;
        }
        
        private class FrameState
        {
            public CameraData camera;
            public List<MeshInstanceData> instances = new List<MeshInstanceData>();
            public List<SkinnedMeshData> skinned = new List<SkinnedMeshData>();
            public int frameCount;
        }
        
        private volatile FrameState currentFrameState = new FrameState();
        private readonly object captureLock = new object();
        
        // Win32 API for window management

        // Win32 API for window management
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();
        
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle, 
            [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
            [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
            uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
        
        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, 
            int X, int Y, int cx, int cy, uint uFlags);
        
        [DllImport("user32.dll")]
        private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
        
        [DllImport("user32.dll")]
        private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
        
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string lpModuleName);
        
        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);
        
        [DllImport("user32.dll")]
        private static extern bool UnregisterClassW([MarshalAs(UnmanagedType.LPWStr)] string lpClassName, IntPtr hInstance);
        
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        
        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
        }
        
        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);
        
        private const int IDC_ARROW = 32512;
        private const uint CS_HREDRAW = 0x0002;
        private const uint CS_VREDRAW = 0x0001;
        
        private static WndProcDelegate wndProcDelegate;
        private static bool windowClassRegistered = false;
        private const string WINDOW_CLASS_NAME = "RemixWindowClass";
        
        private const uint WM_PAINT = 0x000F;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        
        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
        
        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
        
        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public int rcPaint_left;
            public int rcPaint_top;
            public int rcPaint_right;
            public int rcPaint_bottom;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }
        
        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // Handle specific messages
            switch (msg)
            {
                case WM_PAINT:
                    // CRITICAL: Must handle WM_PAINT or Windows marks window as frozen
                    // Begin/EndPaint tells Windows we processed the paint request
                    PAINTSTRUCT ps;
                    BeginPaint(hWnd, out ps);
                    // Remix handles all rendering, we just need to acknowledge the paint
                    EndPaint(hWnd, ref ps);
                    return IntPtr.Zero;
                
                case WM_ERASEBKGND:
                    // Don't erase background - Remix will draw over it
                    return new IntPtr(1);
                    
                case WM_NCHITTEST:
                    // Make sure we report hits in client area for mouse input
                    IntPtr result = DefWindowProcW(hWnd, msg, wParam, lParam);
                    return result;
            }
            
            // Default window procedure handles all other messages
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        
        // Window style constants
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;  // Normal window with title bar and borders
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_APPWINDOW = 0x00040000;  // Show in taskbar
        private const int GWL_EXSTYLE = -20;
        private const int SW_SHOW = 5;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;
        
        private IntPtr remixWindow = IntPtr.Zero;

        void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo($"Plugin {PluginName} v{PluginVersion} is loading!");
            
            // Initialize configuration
            InitializeConfig();
            
            // BepInEx plugins inherit from BaseUnityPlugin which is a MonoBehaviour
            // but we need to make sure our GameObject persists
            DontDestroyOnLoad(this.gameObject);
            
            // Also mark ourselves as not to be destroyed
            hideFlags = HideFlags.HideAndDontSave;
            
            LogSource.LogInfo($"GameObject: {gameObject.name}, Active: {gameObject.activeSelf}, Enabled: {enabled}");
            
            // Subscribe to scene loaded event
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            
            // Load the Remix API but DON'T call Startup - Remix is already 
            // initialized by the d3d9.dll hook. We just need to get the interface
            // to call CreateMesh, DrawInstance, etc.
            try
            {
                LogSource.LogInfo("Loading Remix API interface (not calling Startup)...");
                LoadRemixInterface();
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Failed to load Remix interface: {ex}");
            }
        }
        
        private void InitializeConfig()
        {
            // Camera Settings
            configCameraName = Config.Bind("Camera",
                "CameraName",
                "",
                "Specific camera name to use for RTX Remix rendering. Leave empty to use auto-detection (Camera.main or first camera with MainCamera tag).");
            
            configCameraTag = Config.Bind("Camera",
                "CameraTag",
                "MainCamera",
                "Camera tag to search for if CameraName is not set. Default is 'MainCamera'.");
            
            configListCameras = Config.Bind("Camera",
                "ListCamerasOnSceneLoad",
                true,
                "Log all available cameras when a scene loads to help identify the correct camera name.");
            
            // Rendering Settings
            configUseGameGeometry = Config.Bind("Rendering",
                "EnableGameGeometry",
                true,
                "Enable rendering of game geometry through RTX Remix. Disable to only show test triangle.");
            
            configUseDistanceCulling = Config.Bind("Rendering",
                "EnableDistanceCulling",
                false,
                "Enable distance-based culling of objects. May improve performance but can affect path tracing quality.");
            
            configMaxRenderDistance = Config.Bind("Rendering",
                "MaxRenderDistance",
                500f,
                new ConfigDescription(
                    "Maximum render distance in Unity units (only used if EnableDistanceCulling is true).",
                    new AcceptableValueRange<float>(10f, 10000f)));
            
            configRendererCacheDuration = Config.Bind("Performance",
                "RendererCacheDuration",
                300,
                new ConfigDescription(
                    "Number of frames to cache renderer list before refreshing. Higher values = better performance but slower response to new objects.",
                    new AcceptableValueRange<int>(60, 3600)));
            
            configDebugLogInterval = Config.Bind("Debug",
                "DetailedLogInterval",
                1800,
                new ConfigDescription(
                    "Number of frames between detailed mesh capture debug logs. Set to 0 to disable detailed logging. 1800 frames = 30 seconds at 60 FPS.",
                    new AcceptableValueRange<int>(0, 10800)));
            
            // Lighting Settings
            configEnableLights = Config.Bind("Lighting",
                "EnableLights",
                true,
                "Convert Unity lights to RTX Remix lights. Supports Point, Spot, Directional, and Area lights.");
            
            configLightIntensityMultiplier = Config.Bind("Lighting",
                "IntensityMultiplier",
                1.0f,
                new ConfigDescription(
                    "Global multiplier for all light intensities. Adjust if lights are too bright or too dim in Remix.",
                    new AcceptableValueRange<float>(0.01f, 100f)));
            
            // Performance Settings
            configTargetFPS = Config.Bind("Performance",
                "TargetFPS",
                0,
                new ConfigDescription(
                    "Target FPS for the Remix render thread. Set to 0 for uncapped.",
                    new AcceptableValueRange<int>(0, 500)));
            
            // Apply config values
            useGameGeometry = configUseGameGeometry.Value;
            useDistanceCulling = configUseDistanceCulling.Value;
            maxRenderDistanceSqr = configMaxRenderDistance.Value * configMaxRenderDistance.Value;
            
            LogSource.LogInfo("Configuration loaded:");
            LogSource.LogInfo($"  Camera Name: '{configCameraName.Value}' (empty = auto-detect)");
            LogSource.LogInfo($"  Camera Tag: '{configCameraTag.Value}'");
            LogSource.LogInfo($"  List Cameras: {configListCameras.Value}");
            LogSource.LogInfo($"  Game Geometry: {configUseGameGeometry.Value}");
            LogSource.LogInfo($"  Distance Culling: {configUseDistanceCulling.Value}");
            if (configUseDistanceCulling.Value)
            {
                LogSource.LogInfo($"  Max Render Distance: {configMaxRenderDistance.Value} units");
            }
            LogSource.LogInfo($"  Target FPS: {(configTargetFPS.Value == 0 ? "Uncapped" : configTargetFPS.Value.ToString())}");
        }
        
        private static UnityRemixPlugin instance;
        
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            LogSource.LogInfo($"Scene loaded: {scene.name}, mode: {mode}");
            
            // Reset camera tracking so the new scene's camera is logged
            lastCameraName = "";
            currentCamera = null;
            
            // List available cameras if enabled
            if (configListCameras.Value)
            {
                ListAvailableCameras();
            }
            
            // Keep static reference for data capture
            instance = this;
            
            // Invalidate renderer caches on scene change
            rendererCacheFrame = -1;
            cachedRenderers.Clear();
            cachedSkinnedRenderers.Clear();
            cachedLights.Clear();
            lastSkinnedTransforms.Clear();
            LogSource.LogInfo("Renderer caches invalidated for new scene");
            
            // Clear light cache - will be rebuilt
            foreach (var lightHandle in lightCache.Values)
            {
                if (lightHandle != IntPtr.Zero)
                {
                    try { DestroyLight(lightHandle); } catch { }
                }
            }
            lightCache.Clear();
            
            if (remixInitialized && !deviceRegistered)
            {
                LogSource.LogInfo("Starting Remix initialization...");
                try
                {
                    RegisterRemixDevice();
                    if (deviceRegistered)
                    {
                        LogSource.LogInfo("Starting render thread (will create window and mesh)...");
                        
                        // Start a background thread for rendering - it will create window and mesh
                        StartRenderThread();
                    }
                }
                catch (Exception ex)
                {
                    LogSource.LogError($"Failed to start Remix: {ex}");
                }
            }
            
            // Capture data on scene load
            if (useGameGeometry && deviceRegistered)
            {
                FrameState initialState = new FrameState();
                CaptureStaticMeshes(initialState);
                lock (captureLock)
                {
                    currentFrameState = initialState;
                }
            }
        }
        
        private void ListAvailableCameras()
        {
            var allCameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            LogSource.LogInfo($"=== Available Cameras ({allCameras.Length}) ===");
            
            for (int i = 0; i < allCameras.Length; i++)
            {
                var cam = allCameras[i];
                string statusInfo = "";
                
                if (cam == Camera.main)
                    statusInfo += " [Camera.main]";
                
                try
                {
                    if (cam.CompareTag(configCameraTag.Value))
                        statusInfo += $" [Tag: {configCameraTag.Value}]";
                }
                catch
                {
                    statusInfo += " [No Tag]";
                }
                
                if (!cam.enabled)
                    statusInfo += " [DISABLED]";
                if (!cam.gameObject.activeInHierarchy)
                    statusInfo += " [INACTIVE]";
                
                LogSource.LogInfo($"  [{i}] '{cam.name}'{statusInfo}");
                LogSource.LogInfo($"      Depth: {cam.depth}, ClearFlags: {cam.clearFlags}, CullingMask: 0x{cam.cullingMask:X}");
            }
            
            LogSource.LogInfo("=================================");
        }
        
        private Camera GetPreferredCamera()
        {
            Camera selectedCamera = null;
            string selectionReason = "";
            
            // Try specific camera name first
            if (!string.IsNullOrEmpty(configCameraName.Value))
            {
                var allCameras = UnityEngine.Object.FindObjectsOfType<Camera>();
                var namedCamera = allCameras.FirstOrDefault(c => 
                    c.name.Equals(configCameraName.Value, StringComparison.OrdinalIgnoreCase) &&
                    c.enabled && c.gameObject.activeInHierarchy);
                
                if (namedCamera != null)
                {
                    selectedCamera = namedCamera;
                    selectionReason = $"matched config name '{configCameraName.Value}'";
                }
                else if (lastCameraName != configCameraName.Value)
                {
                    LogSource.LogWarning($"Camera '{configCameraName.Value}' not found or inactive. Falling back to auto-detection.");
                    lastCameraName = configCameraName.Value;
                }
            }
            
            // Try Camera.main
            if (selectedCamera == null && Camera.main != null && Camera.main.enabled && Camera.main.gameObject.activeInHierarchy)
            {
                selectedCamera = Camera.main;
                selectionReason = "Camera.main";
            }
            
            // Try first camera with configured tag
            if (selectedCamera == null)
            {
                var taggedCameras = UnityEngine.Object.FindObjectsOfType<Camera>()
                    .Where(c => c.enabled && c.gameObject.activeInHierarchy)
                    .ToArray();
                
                // First try to find one with the configured tag
                var withTag = taggedCameras.Where(c => 
                {
                    try { return c.CompareTag(configCameraTag.Value); }
                    catch { return false; }
                }).OrderByDescending(c => c.depth).FirstOrDefault();
                
                if (withTag != null)
                {
                    selectedCamera = withTag;
                    selectionReason = $"tag '{configCameraTag.Value}'";
                }
                else if (taggedCameras.Length > 0)
                {
                    // Fallback: any enabled camera, prefer higher depth
                    selectedCamera = taggedCameras.OrderByDescending(c => c.depth).First();
                    selectionReason = "fallback (no MainCamera tag found)";
                }
            }
            
            // Log camera change
            if (selectedCamera != null && selectedCamera.name != lastCameraName)
            {
                if (selectionReason.Contains("fallback"))
                {
                    LogSource.LogWarning($"Using camera: '{selectedCamera.name}' ({selectionReason})");
                }
                else
                {
                    LogSource.LogInfo($"Using camera: '{selectedCamera.name}' ({selectionReason})");
                }
                lastCameraName = selectedCamera.name;
                currentCamera = selectedCamera;
            }
            
            return selectedCamera;
        }

        void OnDestroy()
        {
            // Check if this is a real shutdown or just a scene change
            if (!isQuitting)
            {
                LogSource.LogWarning("OnDestroy called but app not quitting - recreating plugin...");
                
                // Create a new persistent GameObject to keep the plugin alive
                var newGo = new GameObject("UnityRemix_Persistent");
                GameObject.DontDestroyOnLoad(newGo);
                newGo.hideFlags = HideFlags.HideAndDontSave;
                var newPlugin = newGo.AddComponent<RemixPersistentBehaviour>();
                newPlugin.Initialize(this);
                
                return;
            }
            
            LogSource.LogInfo("OnDestroy called during quit - cleaning up...");
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            CleanupRemix();
        }
        
        private static bool isQuitting = false;
        
        public static void SetQuitting()
        {
            isQuitting = true;
        }
        
        private int persistentUpdateCount = 0;
        
        /// <summary>
        /// Called from RemixPersistentBehaviour's LateUpdate to capture skinned meshes
        /// </summary>
        public void UpdateFromPersistent()
        {
            persistentUpdateCount++;
            
            if (persistentUpdateCount % 300 == 1)
            {
                LogSource.LogInfo($"UpdateFromPersistent: count={persistentUpdateCount}, remixInit={remixInitialized}, meshCreated={meshCreated}, deviceReg={deviceRegistered}");
            }
            
            if (!remixInitialized || !meshCreated) return;
            
            frameCount++;
            
            // Capture game data on main thread to ensure thread safety with Unity API
            if (useGameGeometry && deviceRegistered)
            {
                FrameState nextState = new FrameState();
                nextState.frameCount = frameCount;
                
                // 1. Capture static meshes and camera
                CaptureStaticMeshes(nextState);
                
                // 2. Process any queued mesh creations
                ProcessMeshCreationBatch();
                
                // 3. Capture skinned meshes (BakeMesh requires main thread)
                CaptureSkinnedMeshes(nextState);
                
                // Commit state
                lock (captureLock)
                {
                    currentFrameState = nextState;
                }
            }
        }
        
        void OnApplicationQuit()
        {
            LogSource.LogInfo("Application quitting...");
            isQuitting = true;
        }
        
        void OnDisable()
        {
            LogSource.LogWarning($"OnDisable called! enabled={enabled}, gameObject.activeSelf={gameObject?.activeSelf}");
        }
        
        void OnEnable()
        {
            LogSource.LogInfo($"OnEnable called! Frame: {frameCount}");
        }

        void LateUpdate()
        {
            // Capture skinned meshes after animation update but before rendering
            UpdateFromPersistent();
        }

        void Update()
        {
            frameCount++;
            
            // Step 1: Register D3D9 device after some frames when window is ready
            if (remixInitialized && !deviceRegistered && frameCount > 60)
            {
                LogSource.LogInfo($"Attempting to register D3D9 device at frame {frameCount}...");
                try
                {
                    RegisterRemixDevice();
                }
                catch (Exception ex)
                {
                    LogSource.LogError($"Failed to register D3D9 device: {ex}");
                }
            }
            
            // Step 2: Create mesh after device is registered
            if (deviceRegistered && !meshCreated)
            {
                LogSource.LogInfo($"Attempting to create mesh at frame {frameCount}...");
                try
                {
                    CreateTestTriangle();
                    meshCreated = true;
                }
                catch (Exception ex)
                {
                    LogSource.LogError($"Failed to create mesh: {ex}");
                }
            }
        }
        
        private int windowWidth = 1920;
        private int windowHeight = 1080;
        
        private void RegisterRemixDevice()
        {
            // Store dimensions for the render thread to use
            windowWidth = Screen.width > 0 ? Screen.width : 1920;
            windowHeight = Screen.height > 0 ? Screen.height : 1080;
            
            LogSource.LogInfo($"Will create Remix window ({windowWidth}x{windowHeight}) on render thread...");
            
            // Mark as registered - actual window creation happens on render thread
            deviceRegistered = true;
        }
        
        private bool CreateRemixWindow()
        {
            LogSource.LogInfo($"Creating Remix window ({windowWidth}x{windowHeight}) on render thread...");
            
            IntPtr hInstance = GetModuleHandleW(null);
            
            // Register our own window class if not already done
            if (!windowClassRegistered)
            {
                // Keep delegate alive
                wndProcDelegate = WndProc;
                
                var wc = new WNDCLASS
                {
                    style = CS_HREDRAW | CS_VREDRAW,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInstance,
                    hIcon = IntPtr.Zero,
                    hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = WINDOW_CLASS_NAME
                };
                
                ushort classAtom = RegisterClassW(ref wc);
                if (classAtom == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    LogSource.LogError($"Failed to register window class, error: {error}");
                    return false;
                }
                
                windowClassRegistered = true;
                LogSource.LogInfo("Window class registered successfully");
            }
            
            // Create window with our custom class
            string windowTitle = $"{Application.productName} - RTX [{BuildInfo.GitHash}]";
            remixWindow = CreateWindowExW(
                WS_EX_APPWINDOW,  // Show in taskbar
                WINDOW_CLASS_NAME,  // Our custom window class
                windowTitle,
                WS_OVERLAPPEDWINDOW | WS_VISIBLE,  // Normal window with title bar
                100, 100, windowWidth, windowHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero
            );
            
            if (remixWindow == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                LogSource.LogError($"Failed to create Remix window, error: {error}");
                return false;
            }
            
            LogSource.LogInfo($"Remix window created: {remixWindow}");
            
            // Show the window
            ShowWindow(remixWindow, SW_SHOW);
            
            // Call Remix Startup with our new window
            LogSource.LogInfo("Calling Remix Startup...");
            var startupInfo = new RemixAPI.remixapi_StartupInfo
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_STARTUP_INFO,
                pNext = IntPtr.Zero,
                hwnd = remixWindow,
                disableSrgbConversionForOutput = 0,
                forceNoVkSwapchain = 0,
                editorModeEnabled = 0
            };
            var result = Startup(ref startupInfo);
            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                LogSource.LogError($"Remix Startup failed: {result}");
                DestroyWindow(remixWindow);
                remixWindow = IntPtr.Zero;
                return false;
            }
            
            LogSource.LogInfo("Remix Startup succeeded!");
            return true;
        }
        
        private Thread renderThread = null;
        private volatile bool renderThreadRunning = false;
        
        private void StartRenderThread()
        {
            if (renderThread != null && renderThread.IsAlive)
            {
                LogSource.LogInfo("Render thread already running");
                return;
            }
            
            renderThreadRunning = true;
            renderThread = new Thread(RenderThreadLoop);
            renderThread.IsBackground = true;
            renderThread.Start();
            LogSource.LogInfo("Render thread started");
        }
        
        // Windows message pump
        [DllImport("user32.dll")]
        private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);
        
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
        
        [DllImport("user32.dll")]
        private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
        
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }
        
        private const uint PM_REMOVE = 0x0001;
        private const uint QS_ALLINPUT = 0x04FF;
        private const uint MWMO_INPUTAVAILABLE = 0x0004;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_TIMEOUT = 0x00000102;
        
        private void RenderThreadLoop()
        {
            LogSource.LogInfo("Render thread loop starting...");
            
            // Create window on this thread so message pump works
            if (!CreateRemixWindow())
            {
                LogSource.LogError("Failed to create Remix window on render thread");
                return;
            }
            
            // Create mesh and light
            LogSource.LogInfo("Creating test triangle mesh on render thread...");
            CreateTestTriangle();
            meshCreated = true;
            
            int frameNum = 0;
            
            while (renderThreadRunning && deviceRegistered)
            {
                try
                {
                    // Process all pending messages first
                    PumpWindowsMessages();
                    
                    // Render the frame
                    RenderFrame();
                    frameNum++;
                    
                    if (frameNum % 300 == 0)
                    {
                        // Log stats
                    }
                    
                    // Frame rate limiting using MsgWait (no jitter, responsive to messages)
                    uint waitMs = 0;
                    if (configTargetFPS.Value > 0)
                    {
                        waitMs = (uint)(1000 / configTargetFPS.Value);
                    }
                    else
                    {
                        // Uncapped: use 1ms wait to let Windows process messages
                        // Without this, tight loop causes "not responding"
                        waitMs = 1;
                    }
                    
                    // Wait for either messages or timeout
                    // This is MUCH better than Thread.Sleep because:
                    // 1. Immediately wakes when messages arrive (no waiting full timeout)
                    // 2. Windows knows we're responsive to messages
                    // 3. Still allows high framerates (1ms = up to 1000fps theoretical)
                    uint result = MsgWaitForMultipleObjectsEx(0, null, waitMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                    if (result == WAIT_OBJECT_0)
                    {
                        // Messages available, pump them immediately
                        PumpWindowsMessages();
                    }
                }
                catch (Exception ex)
                {
                    LogSource.LogError($"Render thread error: {ex}");
                    Thread.Sleep(1000);  // Wait before retrying
                }
            }
            
            LogSource.LogInfo("Render thread loop ended");
        }
        
        /// <summary>
        /// Pumps Windows messages for the current thread to keep the window responsive.
        /// Call this frequently to prevent "not responding" issues.
        /// </summary>
        private void PumpWindowsMessages()
        {
            MSG msg;
            while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        
        private void RefreshRendererCache()
        {
            cachedRenderers.Clear();
            cachedSkinnedRenderers.Clear();
            
            // Only call expensive FindObjectsOfType when cache is stale
            cachedRenderers.AddRange(UnityEngine.Object.FindObjectsOfType<MeshRenderer>());
            cachedSkinnedRenderers.AddRange(UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>());
            
            rendererCacheFrame = frameCount;
            LogSource.LogInfo($"Renderer cache refreshed: {cachedRenderers.Count} static, {cachedSkinnedRenderers.Count} skinned");
        }
        
        private void RefreshLightCache()
        {
            if (!configEnableLights.Value)
                return;
                
            cachedLights.Clear();
            cachedLights.AddRange(UnityEngine.Object.FindObjectsOfType<Light>());
            
            if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 0)
            {
                LogSource.LogInfo($"Light cache refreshed: {cachedLights.Count} lights found");
            }
        }
        
        private void CaptureStaticMeshes(FrameState state)
        {
            // Rebuild renderer cache periodically or on first call
            if (frameCount - rendererCacheFrame > configRendererCacheDuration.Value || rendererCacheFrame < 0)
            {
                RefreshRendererCache();
            }
            
            // Access Unity objects from main thread (safe)
            Camera mainCam = GetPreferredCamera();
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
            
            // Static meshes from cached list
            foreach (var renderer in cachedRenderers)
            {
                // Null check for destroyed objects
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                
                // Optional distance culling (disabled by default to preserve path tracing accuracy)
                if (useDistanceCulling)
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
                
                // Queue mesh for creation if not in cache
                if (!meshCache.ContainsKey(meshId))
                {
                    if (!meshesToCreate.Contains(mesh))
                    {
                        meshesToCreate.Enqueue(mesh);
                        
                        // Also capture material textures for this renderer
                        var material = renderer.sharedMaterial;
                        if (material != null)
                        {
                            int matId = material.GetInstanceID();
                            if (!materialTextureData.ContainsKey(matId))
                            {
                                CaptureMaterialTextures(material, matId);
                            }
                            // Map this mesh to this material
                            meshToMaterialMap[meshId] = matId;
                            
                            if (meshCache.Count < 10) // Log first few mappings
                            {
                                LogSource.LogInfo($"Mapped mesh '{mesh.name}' (ID: {meshId}) to material (ID: {matId})");
                            }
                        }
                    }
                    continue; // Skip this frame, will have mesh next frame
                }
                
                if (meshCache.ContainsKey(meshId))
                {
                    state.instances.Add(new MeshInstanceData
                    {
                        meshId = meshId,
                        localToWorld = renderer.transform.localToWorldMatrix
                    });
                }
            }
        }
        
        private void ProcessMeshCreationBatch()
        {
            int batchSize = Math.Min(10, meshesToCreate.Count); // Process 10 per frame max
            
            for (int i = 0; i < batchSize; i++)
            {
                if (meshesToCreate.Count == 0) break;
                
                var mesh = meshesToCreate.Dequeue();
                if (mesh == null) continue;
                
                int meshId = mesh.GetInstanceID();
                
                // Double-check it's not already cached
                if (meshCache.ContainsKey(meshId))
                    continue;
                
                try
                {
                    IntPtr handle = CreateRemixMeshFromUnity(mesh);
                    if (handle != IntPtr.Zero)
                    {
                        meshCache[meshId] = handle;
                    }
                }
                catch (Exception ex)
                {
                    LogSource.LogWarning($"Failed to create mesh in batch: {ex.Message}");
                }
            }
        }
        
        // Cache for baked skinned meshes
        private Dictionary<int, Mesh> bakedMeshes = new Dictionary<int, Mesh>();
        
        // Dirty tracking for skinned meshes - only update if transform changed
        private Dictionary<int, Matrix4x4> lastSkinnedTransforms = new Dictionary<int, Matrix4x4>();
        
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
        
        // Skinned mesh data captured on main thread
        private struct SkinnedMeshData
        {
            public int meshId;
            public Vector3[] vertices;
            public Vector3[] normals;
            public Vector2[] uvs;
            public int[] triangles;
            public Matrix4x4 localToWorld;
        }
        
        // D3D9 COM interface methods
        [DllImport("d3d9.dll")]
        private static extern int Direct3DCreate9Ex(uint SDKVersion, out IntPtr ppD3D);
        
        private const uint D3D_SDK_VERSION = 32;
        private const uint D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x00000040;
        private const uint D3DDEVTYPE_HAL = 1;
        private const uint D3DFMT_X8R8G8B8 = 22;
        private const uint D3DFMT_D24S8 = 75;
        private const uint D3DSWAPEFFECT_DISCARD = 1;
        private const uint D3DPRESENT_INTERVAL_ONE = 1;
        
        [StructLayout(LayoutKind.Sequential)]
        private struct D3DPRESENT_PARAMETERS
        {
            public uint BackBufferWidth;
            public uint BackBufferHeight;
            public uint BackBufferFormat;
            public uint BackBufferCount;
            public uint MultiSampleType;
            public uint MultiSampleQuality;
            public uint SwapEffect;
            public IntPtr hDeviceWindow;
            public int Windowed;
            public int EnableAutoDepthStencil;
            public uint AutoDepthStencilFormat;
            public uint Flags;
            public uint FullScreen_RefreshRateInHz;
            public uint PresentationInterval;
        }
        
        private IntPtr CreateD3D9DeviceFromD3D9(IntPtr pD3D9, IntPtr hwnd)
        {
            // IDirect3D9Ex vtable layout (from d3d9.h):
            // 0: QueryInterface
            // 1: AddRef  
            // 2: Release
            // 3: RegisterSoftwareDevice
            // 4: GetAdapterCount
            // 5: GetAdapterIdentifier
            // 6: GetAdapterModeCount
            // 7: EnumAdapterModes
            // 8: GetAdapterDisplayMode
            // 9: CheckDeviceType
            // 10: CheckDeviceFormat
            // 11: CheckDeviceMultiSampleType
            // 12: CheckDepthStencilMatch
            // 13: CheckDeviceFormatConversion
            // 14: GetDeviceCaps
            // 15: GetAdapterMonitor
            // 16: CreateDevice
            // -- IDirect3D9Ex extends IDirect3D9 --
            // 17: GetAdapterModeCountEx
            // 18: EnumAdapterModesEx
            // 19: GetAdapterDisplayModeEx
            // 20: CreateDeviceEx
            // 21: GetAdapterLUID
            
            // Get the vtable
            IntPtr vtable = Marshal.ReadIntPtr(pD3D9);
            
            // CreateDeviceEx is at index 20 for IDirect3D9Ex
            IntPtr createDeviceExPtr = Marshal.ReadIntPtr(vtable, 20 * IntPtr.Size);
            LogSource.LogInfo($"CreateDeviceEx function pointer: {createDeviceExPtr}");
            
            // Define the delegate for CreateDeviceEx
            var createDeviceEx = Marshal.GetDelegateForFunctionPointer<CreateDeviceExDelegate>(createDeviceExPtr);
            
            // Get screen dimensions
            int width = Screen.width > 0 ? Screen.width : 1920;
            int height = Screen.height > 0 ? Screen.height : 1080;
            LogSource.LogInfo($"Using resolution: {width}x{height}");
            
            // Setup present parameters - use simpler settings
            var pp = new D3DPRESENT_PARAMETERS
            {
                BackBufferWidth = (uint)width,
                BackBufferHeight = (uint)height,
                BackBufferFormat = 0, // D3DFMT_UNKNOWN - let D3D choose
                BackBufferCount = 1,
                MultiSampleType = 0,
                MultiSampleQuality = 0,
                SwapEffect = D3DSWAPEFFECT_DISCARD,
                hDeviceWindow = hwnd,
                Windowed = 1,
                EnableAutoDepthStencil = 0, // Don't create depth buffer
                AutoDepthStencilFormat = 0,
                Flags = 0,
                FullScreen_RefreshRateInHz = 0,
                PresentationInterval = D3DPRESENT_INTERVAL_ONE
            };
            
            IntPtr device = IntPtr.Zero;
            int hr = createDeviceEx(
                pD3D9,
                0, // Adapter
                D3DDEVTYPE_HAL,
                hwnd,
                D3DCREATE_HARDWARE_VERTEXPROCESSING,
                ref pp,
                IntPtr.Zero, // pFullscreenDisplayMode
                out device
            );
            
            if (hr < 0)
            {
                LogSource.LogError($"CreateDeviceEx failed with HRESULT: 0x{hr:X8}");
                
                // Try with software vertex processing
                LogSource.LogInfo("Retrying with software vertex processing...");
                hr = createDeviceEx(
                    pD3D9,
                    0,
                    D3DDEVTYPE_HAL,
                    hwnd,
                    0x00000020, // D3DCREATE_SOFTWARE_VERTEXPROCESSING
                    ref pp,
                    IntPtr.Zero,
                    out device
                );
                
                if (hr < 0)
                {
                    LogSource.LogError($"CreateDeviceEx (software) failed with HRESULT: 0x{hr:X8}");
                    return IntPtr.Zero;
                }
            }
            
            return device;
        }
        
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateDeviceExDelegate(
            IntPtr pD3D9,
            uint Adapter,
            uint DeviceType,
            IntPtr hFocusWindow,
            uint BehaviorFlags,
            ref D3DPRESENT_PARAMETERS pPresentationParameters,
            IntPtr pFullscreenDisplayMode,
            out IntPtr ppReturnedDeviceInterface
        );

        
        private int skinnedCaptureCount = 0;
        
        private void CaptureSkinnedMeshes(FrameState state)
        {
            // Rebuild renderer cache if stale
            if (frameCount - rendererCacheFrame > RENDERER_CACHE_DURATION || rendererCacheFrame < 0)
            {
                RefreshRendererCache();
            }
            
            skinnedCaptureCount++;
            bool doLog = configDebugLogInterval.Value > 0 && (skinnedCaptureCount % configDebugLogInterval.Value == 1);
            
            if (doLog)
            {
                LogSource.LogInfo($"CaptureSkinnedMeshes: cached {cachedSkinnedRenderers.Count} skinned renderers");
            }
            
            int skipped = 0;
            int noMesh = 0;
            int baked = 0;
            int skippedUnchanged = 0;
            
            // Use cached list instead of FindObjectsOfType
            foreach (var skinned in cachedSkinnedRenderers)
            {
                if (skinned == null || !skinned.enabled || !skinned.gameObject.activeInHierarchy)
                {
                    skipped++;
                    continue;
                }
                
                if (skinned.sharedMesh == null)
                {
                    noMesh++;
                    continue;
                }
                
                try
                {
                    int skinnedId = skinned.GetInstanceID();
                    Matrix4x4 currentTransform = skinned.transform.localToWorldMatrix;
                    
                    // For skinned meshes, we must bake every frame
                    lastSkinnedTransforms[skinnedId] = currentTransform;
                    
                    // Calculate unscaled matrix to prevent double-scaling
                    // BakeMesh captures bone scaling (inherited from root), so we shouldn't apply root scale again
                    // BUT we must preserve the SIGN of the scale to handle mirroring (negative scale)
                    Vector3 lossyScale = skinned.transform.lossyScale;
                    Vector3 signScale = new Vector3(
                        Mathf.Sign(lossyScale.x),
                        Mathf.Sign(lossyScale.y),
                        Mathf.Sign(lossyScale.z)
                    );
                    
                    Matrix4x4 unscaledMatrix = Matrix4x4.TRS(skinned.transform.position, skinned.transform.rotation, signScale);
                    
                    // Get or create baked mesh
                    if (!bakedMeshes.TryGetValue(skinnedId, out Mesh bakedMesh) || bakedMesh == null)
                    {
                        bakedMesh = new Mesh();
                        bakedMesh.name = $"Baked_{skinned.name}";
                        bakedMeshes[skinnedId] = bakedMesh;
                    }
                    
                    // Bake current pose (must be on main thread)
                    skinned.BakeMesh(bakedMesh);
                    
                    // Copy mesh data for render thread
                    var verts = bakedMesh.vertices;
                    var norms = bakedMesh.normals;
                    var uvCoords = bakedMesh.uv;
                    
                    // Combine all submeshes into one triangle list, filtering out non-triangle topology
                    var allTris = new List<int>();
                    for (int i = 0; i < bakedMesh.subMeshCount; i++)
                    {
                        var topology = bakedMesh.GetTopology(i);
                        if (topology != MeshTopology.Triangles)
                        {
                            if (doLog)
                            {
                                LogSource.LogWarning($"  Skipping submesh {i} of '{skinned.name}' with non-triangle topology: {topology}");
                            }
                            continue;
                        }
                        
                        var subTris = bakedMesh.GetTriangles(i);
                        if (subTris != null && subTris.Length > 0)
                        {
                            allTris.AddRange(subTris);
                        }
                    }
                    var tris = allTris.ToArray();
                    
                    if (verts == null || verts.Length == 0)
                    {
                        if (doLog)
                            LogSource.LogWarning($"  Baked mesh {skinned.name} has no vertices!");
                        continue;
                    }
                    
                    // Validate triangle count before adding to state
                    if (tris == null || tris.Length == 0)
                    {
                        if (doLog)
                            LogSource.LogWarning($"  Baked mesh {skinned.name} has no triangles!");
                        continue;
                    }
                    
                    if (tris.Length % 3 != 0)
                    {
                        LogSource.LogError($"  Baked mesh {skinned.name} has invalid triangle count: {tris.Length} (not divisible by 3). Skipping.");
                        continue;
                    }
                    
                    if (doLog)
                    {
                        var camPos = state.camera.valid ? state.camera.position.ToString() : "Invalid";
                        LogSource.LogInfo($"  Captured Skinned: {skinned.name} (Verts: {verts.Length}, Tris: {tris.Length/3})");
                        LogSource.LogInfo($"    - Mesh Pos: {skinned.transform.position}");
                        LogSource.LogInfo($"    - Cam Pos:  {camPos}");
                        LogSource.LogInfo($"    - Scale:    {skinned.transform.lossyScale} -> Forcing (1,1,1)");
                        LogSource.LogInfo($"    - Bounds:   {bakedMesh.bounds}");
                    }
                    
                    state.skinned.Add(new SkinnedMeshData
                    {
                        meshId = skinnedId,
                        vertices = verts,
                        normals = norms,
                        uvs = uvCoords,
                        triangles = tris,
                        localToWorld = unscaledMatrix
                    });
                    baked++;
                }
                catch (Exception ex)
                {
                    if (configDebugLogInterval.Value > 0 && skinnedCaptureCount % configDebugLogInterval.Value == 1)
                        LogSource.LogError($"  Failed to bake {skinned.name}: {ex.Message}");
                }
            }
            
            if (configDebugLogInterval.Value > 0 && skinnedCaptureCount % configDebugLogInterval.Value == 1)
            {
                LogSource.LogInfo($"CaptureSkinnedMeshes: baked={baked}, skipped={skipped}, noMesh={noMesh}, unchanged={skippedUnchanged}");
            }
        }
        
        private void EnsureMeshInCache(Mesh mesh)
        {
            int meshId = mesh.GetInstanceID();
            if (meshCache.ContainsKey(meshId))
                return;
            
            // Create Remix mesh from Unity mesh
            try
            {
                IntPtr remixHandle = CreateRemixMeshFromUnity(mesh);
                if (remixHandle != IntPtr.Zero)
                {
                    meshCache[meshId] = remixHandle;
                    if (meshCache.Count % 100 == 0)
                    {
                        LogSource.LogInfo($"Mesh cache size: {meshCache.Count}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Failed to create Remix mesh: {ex.Message}");
            }
        }
        
        private IntPtr CreateRemixMeshFromUnity(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uvs = mesh.uv;
            
            if (vertices == null || vertices.Length == 0)
                return IntPtr.Zero;
            
            // Extract triangles from all submeshes, filtering out non-triangle topology
            var allTris = new List<int>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var topology = mesh.GetTopology(i);
                if (topology != MeshTopology.Triangles)
                {
                    if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 0)  // Log occasionally to avoid spam
                    {
                        LogSource.LogWarning($"Skipping submesh {i} of '{mesh.name}' with non-triangle topology: {topology}");
                    }
                    continue;
                }
                
                var subTris = mesh.GetTriangles(i);
                if (subTris != null && subTris.Length > 0)
                {
                    allTris.AddRange(subTris);
                }
            }
            
            int[] triangles = allTris.ToArray();
            
            // Validate triangle count
            if (triangles == null || triangles.Length == 0)
            {
                return IntPtr.Zero;
            }
            
            if (triangles.Length % 3 != 0)
            {
                LogSource.LogError($"Mesh '{mesh.name}' has invalid triangle count: {triangles.Length} (not divisible by 3). Skipping.");
                return IntPtr.Zero;
            }
            
            // Validate that all indices are within bounds
            for (int i = 0; i < triangles.Length; i++)
            {
                if (triangles[i] < 0 || triangles[i] >= vertices.Length)
                {
                    LogSource.LogError($"Mesh '{mesh.name}' has out-of-bounds index {triangles[i]} at position {i} (vertex count: {vertices.Length}). Skipping.");
                    return IntPtr.Zero;
                }
            }
            
            // Ensure we have normals
            if (normals == null || normals.Length != vertices.Length)
            {
                normals = new Vector3[vertices.Length];
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = Vector3.up;
            }
            
            // Ensure we have UVs
            if (uvs == null || uvs.Length != vertices.Length)
            {
                uvs = new Vector2[vertices.Length];
            }
            
            // Create Remix vertices using the helper function
            // Convert from Unity Y-up to Z-up coordinate system for USD
            var remixVerts = new RemixAPI.remixapi_HardcodedVertex[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                remixVerts[i] = RemixAPI.MakeVertex(
                    vertices[i].x,  vertices[i].z,  vertices[i].y,   // Y→Z, Z→Y (Y-up to Z-up)
                    normals[i].x,   normals[i].z,   normals[i].y,    // Same for normals
                    uvs[i].x, uvs[i].y,
                    0xFFFFFFFF
                );
            }
            
            // Convert indices to uint
            uint[] indices = null;
            if (triangles != null && triangles.Length > 0)
            {
                indices = new uint[triangles.Length];
                for (int i = 0; i < triangles.Length; i++)
                    indices[i] = (uint)triangles[i];
            }
            
            // Pin arrays
            GCHandle vertexHandle = GCHandle.Alloc(remixVerts, GCHandleType.Pinned);
            GCHandle indexHandle = indices != null ? GCHandle.Alloc(indices, GCHandleType.Pinned) : default;
            
            try
            {
                // Look up material texture data for this mesh
                int meshId = mesh.GetInstanceID();
                IntPtr materialHandle = IntPtr.Zero;
                string materialInfo = "no material";
                
                if (meshToMaterialMap.TryGetValue(meshId, out int matId) && 
                    materialTextureData.TryGetValue(matId, out var matData))
                {
                    // Use the material hash as handle (Remix uses hash as handle)
                    if (matData.remixMaterialHandle != IntPtr.Zero)
                    {
                        materialHandle = matData.remixMaterialHandle;
                    }
                    else
                    {
                        // Material not created yet, use 64-bit hash directly
                        ulong matHash = GenerateMaterialHash(matData.materialName, matId);
                        materialHandle = new IntPtr((long)matHash);
                    }
                    
                    string albedoPath = GetTexturePathFromHandle(matData.albedoHandle);
                    string normalPath = GetTexturePathFromHandle(matData.normalHandle);
                    
                    materialInfo = $"material='{matData.materialName}' (handle: 0x{materialHandle.ToInt64():X}), albedo={albedoPath ?? "none"}, normal={normalPath ?? "none"}";
                }
                
                // Log mesh creation with material info (first few times per session)
                if (meshCache.Count < 20 || frameCount % 300 == 0)
                {
                    LogSource.LogInfo($"Creating mesh '{mesh.name}' (ID: {meshId}) with {materialInfo}");
                }
                
                var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                {
                    vertices_values = vertexHandle.AddrOfPinnedObject(),
                    vertices_count = (ulong)remixVerts.Length,
                    indices_values = indices != null ? indexHandle.AddrOfPinnedObject() : IntPtr.Zero,
                    indices_count = indices != null ? (ulong)indices.Length : 0,
                    skinning_hasvalue = 0,
                    skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                    material = materialHandle  // Use created Remix material
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
                    var result = CreateMesh(ref meshInfo, out handle);
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        return IntPtr.Zero;
                    }
                    
                    LogSource.LogInfo($"Created mesh '{mesh.name}' with hash: 0x{meshHash:X16}");
                    
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
                if (indices != null)
                    indexHandle.Free();
            }
        }
        
        private void RenderGameGeometry()
        {
            // Get current frame state atomically
            FrameState state;
            lock (captureLock)
            {
                state = currentFrameState;
            }
            
            // Setup camera from captured data
            if (state.camera.valid)
            {
                var cam = state.camera;
                // Convert camera from Unity Y-up to Z-up coordinate system
                var paramCamera = new RemixAPI.remixapi_CameraInfoParameterizedEXT
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_CAMERA_INFO_PARAMETERIZED_EXT,
                    pNext = IntPtr.Zero,
                    position = new RemixAPI.remixapi_Float3D(cam.position.x, cam.position.z, cam.position.y),
                    forward = new RemixAPI.remixapi_Float3D(cam.forward.x, cam.forward.z, cam.forward.y),
                    up = new RemixAPI.remixapi_Float3D(cam.up.x, cam.up.z, cam.up.y),
                    right = new RemixAPI.remixapi_Float3D(cam.right.x, cam.right.z, cam.right.y),
                    fovYInDegrees = cam.fov,
                    aspect = cam.aspect,
                    nearPlane = cam.nearPlane,
                    farPlane = cam.farPlane
                };

                GCHandle paramHandle = GCHandle.Alloc(paramCamera, GCHandleType.Pinned);
                try
                {
                    var cameraInfo = new RemixAPI.remixapi_CameraInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_CAMERA_INFO,
                        pNext = paramHandle.AddrOfPinnedObject(),
                        type = RemixAPI.remixapi_CameraType.REMIXAPI_CAMERA_TYPE_WORLD
                    };

                    SetupCamera(ref cameraInfo);
                }
                finally
                {
                    paramHandle.Free();
                }
            }
            else
            {
                // Fallback to test camera
                SetupTestCamera();
            }
            
            // Draw all static mesh instances with ObjectPicking support
            uint objectPickingValue = 1; // Start at 1, 0 is reserved
            
            foreach (var instance in state.instances)
            {
                if (!meshCache.TryGetValue(instance.meshId, out IntPtr meshHandle))
                    continue;
                
                // Convert Unity Matrix4x4 to Remix transform (3x4 row-major)
                // Convert from Unity Y-up to Z-up coordinate system
                var m = instance.localToWorld;
                var transform = RemixAPI.remixapi_Transform.FromMatrix(
                    m.m00,  m.m02,  m.m01,  m.m03,   // Row 0: X stays X, Z→Y, Y→Z
                    m.m20,  m.m22,  m.m21,  m.m23,   // Row 1: Z row → Y row (Y-up → Z-up)
                    m.m10,  m.m12,  m.m11,  m.m13    // Row 2: Y row → Z row
                );
                
                // Create ObjectPicking extension for viewport selection
                var objectPickingExt = new RemixAPI.remixapi_InstanceInfoObjectPickingEXT
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_OBJECT_PICKING_EXT,
                    pNext = IntPtr.Zero,
                    objectPickingValue = objectPickingValue
                };
                
                // Pin the extension struct
                GCHandle pickingHandle = GCHandle.Alloc(objectPickingExt, GCHandleType.Pinned);
                
                try
                {
                    // Note: categoryFlags are now auto-applied by the Remix runtime!
                    // The runtime looks up texture hashes and applies categories automatically,
                    // just like it does for D3D9-hooked content. You can still manually set
                    // categoryFlags here if needed for custom behavior.
                    
                    var instanceInfo = new RemixAPI.remixapi_InstanceInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                        pNext = pickingHandle.AddrOfPinnedObject(), // Chain the extension!
                        categoryFlags = 0, // Runtime auto-applies based on texture categorization!
                        mesh = meshHandle,
                        transform = transform,
                        doubleSided = 1
                    };
                    
                    DrawInstance(ref instanceInfo);
                    objectPickingValue++; // Increment for next draw call
                }
                finally
                {
                    pickingHandle.Free();
                }
            }
            
            // Draw skinned mesh instances (captured from main thread)
            RenderSkinnedMeshes(state, objectPickingValue);
        }
        
        // Cache for skinned mesh Remix handles - keyed by skinned renderer ID
        private Dictionary<int, IntPtr> skinnedMeshHandles = new Dictionary<int, IntPtr>();
        
        private int skinnedRenderCount = 0;
        
        private void RenderSkinnedMeshes(FrameState state, uint startObjectPickingValue)
        {
            if (state.skinned == null || state.skinned.Count == 0)
                return;
                
            skinnedRenderCount++;
            if (skinnedRenderCount % 300 == 1)
            {
                LogSource.LogInfo($"RenderSkinnedMeshes: rendering {state.skinned.Count} skinned meshes");
            }
            
            // Track which meshes were updated this frame
            HashSet<int> updatedMeshes = new HashSet<int>();
            
            uint objectPickingValue = startObjectPickingValue;
            
            foreach (var skinned in state.skinned)
            {
                try
                {
                    // Destroy previous mesh for this ID if it exists
                    // We must do this to avoid leaking VRAM since we create a new mesh every frame
                    if (skinnedMeshHandles.TryGetValue(skinned.meshId, out IntPtr oldHandle))
                    {
                        DestroyMesh(oldHandle);
                        skinnedMeshHandles.Remove(skinned.meshId);
                    }
                    
                    // Create Remix mesh from the baked vertex data
                    // Use frameCount from state to ensure consistent hashing
                    IntPtr meshHandle = CreateRemixMeshFromData(
                        skinned.meshId,
                        skinned.vertices,
                        skinned.normals,
                        skinned.uvs,
                        skinned.triangles,
                        state.frameCount
                    );
                    
                    if (meshHandle == IntPtr.Zero)
                        continue;
                    
                    // Store new handle
                    skinnedMeshHandles[skinned.meshId] = meshHandle;
                    updatedMeshes.Add(skinned.meshId);
                    
                    // Convert Unity Matrix4x4 to Remix transform
                    // Convert from Unity Y-up to Z-up coordinate system
                    var m = skinned.localToWorld;
                    var transform = RemixAPI.remixapi_Transform.FromMatrix(
                        m.m00,  m.m02,  m.m01,  m.m03,   // Row 0: X stays X, Z→Y, Y→Z
                        m.m20,  m.m22,  m.m21,  m.m23,   // Row 1: Z row → Y row (Y-up → Z-up)
                        m.m10,  m.m12,  m.m11,  m.m13    // Row 2: Y row → Z row
                    );
                    
                    // Create ObjectPicking extension for viewport selection
                    var objectPickingExt = new RemixAPI.remixapi_InstanceInfoObjectPickingEXT
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_OBJECT_PICKING_EXT,
                        pNext = IntPtr.Zero,
                        objectPickingValue = objectPickingValue
                    };
                    
                    // Pin the extension struct
                    GCHandle pickingHandle = GCHandle.Alloc(objectPickingExt, GCHandleType.Pinned);
                    
                    try
                    {
                        // Note: categoryFlags are auto-applied by the Remix runtime
                        
                        var instanceInfo = new RemixAPI.remixapi_InstanceInfo
                        {
                            sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                            pNext = pickingHandle.AddrOfPinnedObject(), // Chain the extension!
                            categoryFlags = 0, // Runtime auto-applies based on texture categorization!
                            mesh = meshHandle,
                            transform = transform,
                            doubleSided = 1
                        };
                        
                        DrawInstance(ref instanceInfo);
                        objectPickingValue++; // Increment for next draw call
                    }
                    finally
                    {
                        pickingHandle.Free();
                    }
                }
                catch { }
            }
            
            // Optional: Cleanup handles for meshes that are no longer present
            // This handles the case where a weapon is unequipped/disabled
            // We can do this periodically to save performance
            if (skinnedRenderCount % 60 == 0)
            {
                List<int> toRemove = new List<int>();
                foreach (var kvp in skinnedMeshHandles)
                {
                    if (!updatedMeshes.Contains(kvp.Key))
                    {
                        DestroyMesh(kvp.Value);
                        toRemove.Add(kvp.Key);
                    }
                }
                
                foreach (int id in toRemove)
                {
                    skinnedMeshHandles.Remove(id);
                }
            }
        }
        
        private IntPtr CreateRemixMeshFromData(int meshId, Vector3[] vertices, Vector3[] normals, Vector2[] uvs, int[] triangles, int frameHash)
        {
            if (vertices == null || vertices.Length == 0)
                return IntPtr.Zero;
            
            // Validate triangles
            if (triangles == null || triangles.Length == 0)
            {
                return IntPtr.Zero;
            }
            
            if (triangles.Length % 3 != 0)
            {
                if (skinnedRenderCount % 300 == 1)
                {
                    LogSource.LogError($"Skinned mesh {meshId} has invalid triangle count: {triangles.Length} (not divisible by 3). Skipping.");
                }
                return IntPtr.Zero;
            }
            
            // Validate that all indices are within bounds
            for (int i = 0; i < triangles.Length; i++)
            {
                if (triangles[i] < 0 || triangles[i] >= vertices.Length)
                {
                    if (skinnedRenderCount % 300 == 1)
                    {
                        LogSource.LogError($"Skinned mesh {meshId} has out-of-bounds index {triangles[i]} at position {i} (vertex count: {vertices.Length}). Skipping.");
                    }
                    return IntPtr.Zero;
                }
            }
            
            // Use a strictly unique hash for every frame to prevent collision/jitter
            // combining meshId (high bits) and frameHash (low bits)
            // This ensures even if frameHash wraps (unlikely with int), it won't collide immediately
            ulong dynamicHash = ((ulong)meshId << 32) | (uint)frameHash;
            
            // Ensure we have normals
            if (normals == null || normals.Length != vertices.Length)
            {
                normals = new Vector3[vertices.Length];
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = Vector3.up;
            }
            
            // Ensure we have UVs
            if (uvs == null || uvs.Length != vertices.Length)
            {
                uvs = new Vector2[vertices.Length];
            }
            
            // Use pooled GCHandles to reduce allocations
            if (!pinnedMeshPool.TryGetValue(meshId, out PinnedMeshData poolData))
            {
                poolData = new PinnedMeshData
                {
                    vertices = new RemixAPI.remixapi_HardcodedVertex[vertices.Length],
                    indices = triangles != null ? new uint[triangles.Length] : new uint[0],
                    isPinned = false,
                    vertexCapacity = vertices.Length,
                    indexCapacity = triangles?.Length ?? 0
                };
            }
            
            // Resize arrays if needed
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
            
            if (triangles != null && poolData.indexCapacity < triangles.Length)
            {
                if (poolData.isPinned && poolData.indexCapacity > 0)
                {
                    poolData.indexHandle.Free();
                }
                poolData.indices = new uint[triangles.Length];
                poolData.indexCapacity = triangles.Length;
            }
            
            // Fill vertex data
            // Convert from Unity Y-up to Z-up coordinate system for USD
            for (int i = 0; i < vertices.Length; i++)
            {
                poolData.vertices[i] = RemixAPI.MakeVertex(
                    vertices[i].x,  vertices[i].z,  vertices[i].y,   // Y→Z, Z→Y (Y-up to Z-up)
                    normals[i].x,   normals[i].z,   normals[i].y,    // Same for normals
                    uvs[i].x, uvs[i].y,
                    0xFFFFFFFF
                );
            }
            
            // Fill index data
            if (triangles != null)
            {
                for (int i = 0; i < triangles.Length; i++)
                    poolData.indices[i] = (uint)triangles[i];
            }
            
            // Pin once if not already pinned
            if (!poolData.isPinned)
            {
                poolData.vertexHandle = GCHandle.Alloc(poolData.vertices, GCHandleType.Pinned);
                if (triangles != null && triangles.Length > 0)
                {
                    poolData.indexHandle = GCHandle.Alloc(poolData.indices, GCHandleType.Pinned);
                }
                poolData.isPinned = true;
                pinnedMeshPool[meshId] = poolData;
            }
            
            // Create Remix mesh using pooled handles
            var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
            {
                vertices_values = poolData.vertexHandle.AddrOfPinnedObject(),
                vertices_count = (ulong)vertices.Length,
                indices_values = triangles != null && triangles.Length > 0 ? poolData.indexHandle.AddrOfPinnedObject() : IntPtr.Zero,
                indices_count = triangles != null ? (ulong)triangles.Length : 0,
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
                    hash = dynamicHash,
                    surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                    surfaces_count = 1
                };
                
                IntPtr handle;
                var result = CreateMesh(ref meshInfo, out handle);
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
        
        private void ProcessLights()
        {
            if (!configEnableLights.Value)
                return;
            
            // Refresh light cache periodically
            if (frameCount - rendererCacheFrame > configRendererCacheDuration.Value || rendererCacheFrame < 0)
            {
                RefreshLightCache();
            }
            
            foreach (var light in cachedLights)
            {
                if (light == null || !light.enabled || !light.gameObject.activeInHierarchy)
                    continue;
                
                int lightId = light.GetInstanceID();
                
                // Create or update light
                IntPtr lightHandle = IntPtr.Zero;
                
                if (!lightCache.TryGetValue(lightId, out lightHandle) || lightHandle == IntPtr.Zero)
                {
                    // Create new Remix light
                    lightHandle = CreateRemixLightFromUnity(light);
                    
                    if (lightHandle != IntPtr.Zero)
                    {
                        lightCache[lightId] = lightHandle;
                    }
                }
                
                // Draw light instance
                if (lightHandle != IntPtr.Zero)
                {
                    DrawLightInstance(lightHandle);
                }
            }
        }
        
        private IntPtr CreateRemixLightFromUnity(Light light)
        {
            try
            {
                // Convert Unity color and intensity to Remix radiance
                Color lightColor = light.color * light.intensity * configLightIntensityMultiplier.Value;
                var radiance = new RemixAPI.remixapi_Float3D(lightColor.r, lightColor.g, lightColor.b);
                
                // Create base light info
                var lightInfo = new RemixAPI.remixapi_LightInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO,
                    pNext = IntPtr.Zero,
                    hash = (ulong)light.GetInstanceID(),
                    radiance = radiance,
                    isDynamic = 0, // Unity lights can move, but we recreate them
                    ignoreViewModel = 0
                };
                
                IntPtr lightHandle = IntPtr.Zero;
                
                switch (light.type)
                {
                    case LightType.Point:
                        lightHandle = CreatePointLight(light, lightInfo);
                        break;
                    
                    case LightType.Spot:
                        lightHandle = CreateSpotLight(light, lightInfo);
                        break;
                    
                    case LightType.Directional:
                        // Directional lights require distant light extension
                        // Not implemented yet - would need remixapi_LightInfoDistantEXT
                        if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 0)
                        {
                            LogSource.LogInfo($"Directional light '{light.name}' not yet supported");
                        }
                        break;
                    
                    default:
                        if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 0)
                        {
                            LogSource.LogInfo($"Light type {light.type} for '{light.name}' not supported");
                        }
                        break;
                }
                
                return lightHandle;
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Failed to create Remix light from '{light.name}': {ex.Message}");
                return IntPtr.Zero;
            }
        }
        
        private IntPtr CreatePointLight(Light light, RemixAPI.remixapi_LightInfo baseInfo)
        {
            var position = light.transform.position;
            
            var sphereExt = new RemixAPI.remixapi_LightInfoSphereEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO_SPHERE_EXT,
                pNext = IntPtr.Zero,
                position = new RemixAPI.remixapi_Float3D(position.x, position.y, position.z),
                radius = light.range * 0.1f, // Convert range to radius (approximate)
                shaping_hasvalue = 0,
                shaping_value = new RemixAPI.remixapi_LightInfoLightShaping(),
                volumetricRadianceScale = 1.0f
            };
            
            GCHandle sphereHandle = GCHandle.Alloc(sphereExt, GCHandleType.Pinned);
            
            try
            {
                baseInfo.pNext = sphereHandle.AddrOfPinnedObject();
                
                IntPtr handle;
                var result = CreateLight(ref baseInfo, out handle);
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 0)
                    {
                        LogSource.LogWarning($"Failed to create point light '{light.name}': {result}");
                    }
                    return IntPtr.Zero;
                }
                
                return handle;
            }
            finally
            {
                sphereHandle.Free();
            }
        }
        
        private IntPtr CreateSpotLight(Light light, RemixAPI.remixapi_LightInfo baseInfo)
        {
            var position = light.transform.position;
            var direction = light.transform.forward;
            
            var shaping = new RemixAPI.remixapi_LightInfoLightShaping
            {
                direction = new RemixAPI.remixapi_Float3D(direction.x, direction.y, direction.z),
                coneAngleDegrees = light.spotAngle,
                coneSoftness = 0.1f, // Unity doesn't expose this, use default
                focusExponent = 1.0f
            };
            
            var sphereExt = new RemixAPI.remixapi_LightInfoSphereEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO_SPHERE_EXT,
                pNext = IntPtr.Zero,
                position = new RemixAPI.remixapi_Float3D(position.x, position.y, position.z),
                radius = light.range * 0.1f,
                shaping_hasvalue = 1,
                shaping_value = shaping,
                volumetricRadianceScale = 1.0f
            };
            
            GCHandle sphereHandle = GCHandle.Alloc(sphereExt, GCHandleType.Pinned);
            
            try
            {
                baseInfo.pNext = sphereHandle.AddrOfPinnedObject();
                
                IntPtr handle;
                var result = CreateLight(ref baseInfo, out handle);
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 0)
                    {
                        LogSource.LogWarning($"Failed to create spot light '{light.name}': {result}");
                    }
                    return IntPtr.Zero;
                }
                
                return handle;
            }
            finally
            {
                sphereHandle.Free();
            }
        }
        
        private void RenderFrame()
        {
            if (!deviceRegistered || !meshCreated) return;
            
            try
            {
                if (useGameGeometry)
                {
                    // Render game geometry from captured data
                    RenderGameGeometry();
                    
                    // Process and render Unity lights
                    ProcessLights();
                    
                    // Also draw test light for illumination if lights are disabled
                    if (!configEnableLights.Value && testLightHandle != IntPtr.Zero)
                    {
                        DrawLightInstance(testLightHandle);
                    }
                }
                else
                {
                    // Setup camera first - required for Remix to render
                    SetupTestCamera();
                    
                    // Draw light each frame
                    if (testLightHandle != IntPtr.Zero)
                    {
                        DrawLightInstance(testLightHandle);
                    }
                    
                    // Draw test triangle each frame
                    if (testMeshHandle != IntPtr.Zero)
                    {
                        DrawTestTriangle();
                    }
                }
                
                // Call Present since we have our own window
                var presentInfo = new RemixAPI.remixapi_PresentInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_PRESENT_INFO,
                    pNext = IntPtr.Zero,
                    hwndOverride = IntPtr.Zero
                };
                
                var result = Present(ref presentInfo);
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    // Only log occasionally to avoid spam
                    if (configDebugLogInterval.Value > 0 && currentFrameState.frameCount % configDebugLogInterval.Value == 0)
                    {
                        LogSource.LogWarning($"Present failed: {result}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Error in RenderFrame: {ex}");
            }
        }

        /// <summary>
        /// Load the Remix API interface without calling Startup.
        /// Remix is already initialized by the d3d9.dll hook - we just need
        /// the function pointers to inject geometry.
        /// </summary>
        private void LoadRemixInterface()
        {
            LogSource.LogInfo("Loading Remix API interface...");

            // Find d3d9.dll in game directory
            string gamePath = Application.dataPath;
            string dllPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(gamePath),
                "d3d9.dll"
            );

            LogSource.LogInfo($"Looking for Remix DLL at: {dllPath}");

            if (!System.IO.File.Exists(dllPath))
            {
                LogSource.LogError($"Remix DLL not found at {dllPath}");
                LogSource.LogInfo("Please place the RTX Remix d3d9.dll in the game root folder.");
                return;
            }

            // Load the Remix API
            var result = RemixAPI.InitializeRemixAPI(dllPath, out remixInterface, out remixDll);
            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                LogSource.LogError($"Failed to load Remix API: {result}");
                return;
            }

            LogSource.LogInfo("Remix API loaded successfully!");
            LogSource.LogInfo($"Interface pointers - CreateMesh: {remixInterface.CreateMesh}, DrawInstance: {remixInterface.DrawInstance}");

            // Cache function delegates
            Shutdown = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Shutdown>(remixInterface.Shutdown);
            CreateMesh = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateMesh>(remixInterface.CreateMesh);
            DestroyMesh = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyMesh>(remixInterface.DestroyMesh);
            SetupCamera = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_SetupCamera>(remixInterface.SetupCamera);
            DrawInstance = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DrawInstance>(remixInterface.DrawInstance);
            CreateLight = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateLight>(remixInterface.CreateLight);
            DestroyLight = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyLight>(remixInterface.DestroyLight);
            DrawLightInstance = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DrawLightInstance>(remixInterface.DrawLightInstance);
            CreateD3D9 = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_dxvk_CreateD3D9>(remixInterface.dxvk_CreateD3D9);
            RegisterD3D9Device = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_dxvk_RegisterD3D9Device>(remixInterface.dxvk_RegisterD3D9Device);
            Startup = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Startup>(remixInterface.Startup);
            Present = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Present>(remixInterface.Present);
            CreateTexture = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateTexture>(remixInterface.CreateTexture);
            DestroyTexture = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyTexture>(remixInterface.DestroyTexture);
            CreateMaterial = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateMaterial>(remixInterface.CreateMaterial);
            DestroyMaterial = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyMaterial>(remixInterface.DestroyMaterial);
            AddTextureHash = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_AddTextureHash>(remixInterface.AddTextureHash);
            RemoveTextureHash = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_RemoveTextureHash>(remixInterface.RemoveTextureHash);

            LogSource.LogInfo("Remix interface loaded - will register D3D9 device after window is ready");
            remixInitialized = true;
            
            // Use InvokeRepeating since Update() doesn't seem to run
            InvokeRepeating("TryRegisterDevice", 2.0f, 1.0f);
        }
        
        private void TryRegisterDevice()
        {
            if (deviceRegistered)
            {
                CancelInvoke("TryRegisterDevice");
                return;
            }
            
            LogSource.LogInfo("TryRegisterDevice called via InvokeRepeating...");
            try
            {
                RegisterRemixDevice();
                if (deviceRegistered)
                {
                    CancelInvoke("TryRegisterDevice");
                    LogSource.LogInfo("Starting render thread (will create window and mesh)...");
                    StartRenderThread();
                }
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Failed in TryRegisterDevice: {ex}");
            }
        }

        private void CreateTestTriangle()
        {
            // Create a simple triangle mesh - matching the C example
            // Triangle positioned at z=10, facing -Z
            RemixAPI.remixapi_HardcodedVertex[] vertices = new RemixAPI.remixapi_HardcodedVertex[3]
            {
                RemixAPI.MakeVertex( 5, -5, 10),  // bottom right
                RemixAPI.MakeVertex( 0,  5, 10),  // top center
                RemixAPI.MakeVertex(-5, -5, 10),  // bottom left
            };

            // No indices - just use vertices directly like the C example
            // Pin arrays in memory
            GCHandle vertexHandle = GCHandle.Alloc(vertices, GCHandleType.Pinned);

            try
            {
                var surface = new RemixAPI.remixapi_MeshInfoSurfaceTriangles
                {
                    vertices_values = vertexHandle.AddrOfPinnedObject(),
                    vertices_count = (ulong)vertices.Length,
                    indices_values = IntPtr.Zero,  // No indices
                    indices_count = 0,
                    skinning_hasvalue = 0,
                    skinning_value = new RemixAPI.remixapi_MeshInfoSkinning(),
                    material = IntPtr.Zero // Use default material
                };

                GCHandle surfaceHandle = GCHandle.Alloc(surface, GCHandleType.Pinned);

                try
                {
                    var meshInfo = new RemixAPI.remixapi_MeshInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,
                        pNext = IntPtr.Zero,
                        hash = 0x1, // Unique identifier (matching C example)
                        surfaces_values = surfaceHandle.AddrOfPinnedObject(),
                        surfaces_count = 1
                    };

                    var result = CreateMesh(ref meshInfo, out testMeshHandle);
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        LogSource.LogError($"Failed to create test mesh: {result}");
                    }
                    else
                    {
                        LogSource.LogInfo($"Test triangle mesh created! Handle: {testMeshHandle}");
                    }
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
            
            // Also create a light so we can see the triangle
            CreateTestLight();
        }
        
        private void CreateTestLight()
        {
            // Create a sphere light in front of the triangle (matching C example)
            var sphereLight = new RemixAPI.remixapi_LightInfoSphereEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO_SPHERE_EXT,
                pNext = IntPtr.Zero,
                position = new RemixAPI.remixapi_Float3D(0, -1, 0),  // Above and in front
                radius = 0.1f,
                shaping_hasvalue = 0,
                shaping_value = new RemixAPI.remixapi_LightInfoLightShaping()
            };
            
            GCHandle sphereHandle = GCHandle.Alloc(sphereLight, GCHandleType.Pinned);
            
            try
            {
                var lightInfo = new RemixAPI.remixapi_LightInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO,
                    pNext = sphereHandle.AddrOfPinnedObject(),
                    hash = 0x3,  // Matching C example
                    radiance = new RemixAPI.remixapi_Float3D(100, 200, 100)  // Greenish light
                };
                
                var result = CreateLight(ref lightInfo, out testLightHandle);
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    LogSource.LogError($"Failed to create light: {result}");
                }
                else
                {
                    LogSource.LogInfo($"Test light created! Handle: {testLightHandle}");
                }
            }
            finally
            {
                sphereHandle.Free();
            }
        }

        private void SetupRemixCamera(Camera unityCamera)
        {
            // Use parameterized camera like the C example - simpler and more reliable
            Vector3 pos = unityCamera.transform.position;
            Vector3 fwd = unityCamera.transform.forward;
            Vector3 up = unityCamera.transform.up;
            Vector3 right = unityCamera.transform.right;

            // Create parameterized camera extension
            var paramCamera = new RemixAPI.remixapi_CameraInfoParameterizedEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_CAMERA_INFO_PARAMETERIZED_EXT,
                pNext = IntPtr.Zero,
                position = new RemixAPI.remixapi_Float3D(pos.x, pos.y, pos.z),
                forward = new RemixAPI.remixapi_Float3D(fwd.x, fwd.y, fwd.z),
                up = new RemixAPI.remixapi_Float3D(up.x, up.y, up.z),
                right = new RemixAPI.remixapi_Float3D(right.x, right.y, right.z),
                fovYInDegrees = unityCamera.fieldOfView,
                aspect = unityCamera.aspect,
                nearPlane = unityCamera.nearClipPlane,
                farPlane = unityCamera.farClipPlane
            };

            // Pin the extension struct
            GCHandle paramHandle = GCHandle.Alloc(paramCamera, GCHandleType.Pinned);
            try
            {
                var cameraInfo = new RemixAPI.remixapi_CameraInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_CAMERA_INFO,
                    pNext = paramHandle.AddrOfPinnedObject(),
                    type = RemixAPI.remixapi_CameraType.REMIXAPI_CAMERA_TYPE_WORLD
                };

                var result = SetupCamera(ref cameraInfo);
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    LogSource.LogWarning($"SetupCamera failed: {result}");
                }
            }
            finally
            {
                paramHandle.Free();
            }
        }

        private void SetupTestCamera()
        {
            // Simple camera at origin looking down +Z axis (towards the triangle at z=10)
            var paramCamera = new RemixAPI.remixapi_CameraInfoParameterizedEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_CAMERA_INFO_PARAMETERIZED_EXT,
                pNext = IntPtr.Zero,
                position = new RemixAPI.remixapi_Float3D(0, 0, 0),      // At origin
                forward = new RemixAPI.remixapi_Float3D(0, 0, 1),       // Looking down +Z
                up = new RemixAPI.remixapi_Float3D(0, 1, 0),            // Y is up
                right = new RemixAPI.remixapi_Float3D(1, 0, 0),         // X is right
                fovYInDegrees = 75.0f,
                aspect = (float)windowWidth / windowHeight,
                nearPlane = 0.1f,
                farPlane = 1000.0f
            };

            // Pin the extension struct
            GCHandle paramHandle = GCHandle.Alloc(paramCamera, GCHandleType.Pinned);
            try
            {
                var cameraInfo = new RemixAPI.remixapi_CameraInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_CAMERA_INFO,
                    pNext = paramHandle.AddrOfPinnedObject(),
                    type = RemixAPI.remixapi_CameraType.REMIXAPI_CAMERA_TYPE_WORLD
                };

                var result = SetupCamera(ref cameraInfo);
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    // Only log occasionally
                    if (frameCount % 300 == 0)
                    {
                        LogSource.LogWarning($"SetupCamera failed: {result}");
                    }
                }
            }
            finally
            {
                paramHandle.Free();
            }
        }
        
        private void DrawTestTriangle()
        {
            // Create identity transform - triangle is already positioned at z=10 in mesh
            var instanceInfo = new RemixAPI.remixapi_InstanceInfo
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                pNext = IntPtr.Zero,
                categoryFlags = 0, // Default category
                mesh = testMeshHandle,
                transform = RemixAPI.remixapi_Transform.Identity(),
                doubleSided = 1
            };

            var result = DrawInstance(ref instanceInfo);
            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                LogSource.LogWarning($"DrawInstance failed: {result}");
            }
        }

        private void CleanupRemix()
        {
            if (!remixInitialized) return;

            LogSource.LogInfo("Cleaning up Remix...");
            
            // Stop threads first
            renderThreadRunning = false;
            if (renderThread != null && renderThread.IsAlive)
            {
                renderThread.Join(1000);  // Wait up to 1 second
            }
            
            materialCreationThreadRunning = false;
            if (materialCreationThread != null && materialCreationThread.IsAlive)
            {
                materialCreationThread.Join(1000);  // Wait up to 1 second
            }

            if (testMeshHandle != IntPtr.Zero && DestroyMesh != null)
            {
                DestroyMesh(testMeshHandle);
                testMeshHandle = IntPtr.Zero;
            }
            
            // Destroy uploaded textures
            if (DestroyTexture != null)
            {
                foreach (var handle in textureCache.Values)
                {
                    if (handle != IntPtr.Zero)
                    {
                        DestroyTexture(handle);
                    }
                }
            }
            textureCache.Clear();
            
            // Free all pinned GCHandles
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

            // Use the proper shutdown function
            RemixAPI.ShutdownAndUnloadRemixDll(ref remixInterface, remixDll);
            remixDll = IntPtr.Zero;
            remixInitialized = false;
            
            // Destroy our Remix window
            if (remixWindow != IntPtr.Zero)
            {
                DestroyWindow(remixWindow);
                remixWindow = IntPtr.Zero;
            }

            LogSource.LogInfo("Remix cleanup complete.");
        }
        
        /// <summary>
        /// Capture textures from a Unity material and upload them to Remix
        /// </summary>
        private void CaptureMaterialTextures(Material material, int materialId)
        {
            if (material == null)
                return;
                
            var matData = new MaterialTextureData
            {
                materialName = material.name,
                albedoColor = Color.white,
                albedoHandle = IntPtr.Zero,
                normalHandle = IntPtr.Zero,
                albedoTextureHash = 0,
                normalTextureHash = 0
            };
            
            // Get albedo color
            if (material.HasProperty("_Color"))
            {
                matData.albedoColor = material.GetColor("_Color");
            }
            
            // Upload albedo/diffuse texture
            if (material.HasProperty("_MainTex"))
            {
                var tex = material.GetTexture("_MainTex") as Texture2D;
                if (tex != null)
                {
                    // Try to upload (will handle non-readable textures via GPU readback)
                    matData.albedoHandle = UploadUnityTexture(tex, srgb: true);
                    if (matData.albedoHandle != IntPtr.Zero)
                    {
                        // Get the hash that was computed during upload
                        int texId = tex.GetInstanceID();
                        if (textureHashCache.TryGetValue(texId, out ulong hash))
                        {
                            matData.albedoTextureHash = hash;
                        }
                        LogSource.LogInfo($"Captured albedo texture for material '{material.name}' (hash: 0x{matData.albedoTextureHash:X16})");
                    }
                }
            }
            
            // Upload normal map
            if (material.HasProperty("_BumpMap"))
            {
                var tex = material.GetTexture("_BumpMap") as Texture2D;
                if (tex != null)
                {
                    // Try to upload (will handle non-readable textures via GPU readback)
                    matData.normalHandle = UploadUnityTexture(tex, srgb: false);
                    if (matData.normalHandle != IntPtr.Zero)
                    {
                        // Get the hash that was computed during upload
                        int texId = tex.GetInstanceID();
                        if (textureHashCache.TryGetValue(texId, out ulong hash))
                        {
                            matData.normalTextureHash = hash;
                        }
                        LogSource.LogInfo($"Captured normal texture for material '{material.name}' (hash: 0x{matData.normalTextureHash:X16})");
                    }
                }
            }
            
            // Store for later use (material will be created async)
            materialTextureData[materialId] = matData;
            
            // Queue for async material creation
            lock (pendingMaterialCreation)
            {
                if (!pendingMaterialCreation.Contains(materialId))
                {
                    pendingMaterialCreation.Enqueue(materialId);
                }
            }
            
            // Start material creation thread if not running
            if (!materialCreationThreadRunning)
            {
                StartMaterialCreationThread();
            }
            
            // NOTE: CreateMaterial causes deadlock when called from capture thread
            // Textures are uploaded and available in texture table by hash
            // Remix can reference them via hash paths, or we can manually create materials in toolkit
            string albedoPath = GetTexturePathFromHandle(matData.albedoHandle);
            string normalPath = GetTexturePathFromHandle(matData.normalHandle);
            LogSource.LogInfo($"Textures ready for material '{material.name}': albedo={albedoPath ?? "none"}, normal={normalPath ?? "none"}");
        }
        
        /// <summary>
        /// Generate a 64-bit hash for a material (Remix uses full 64-bit hashes)
        /// </summary>
        private ulong GenerateMaterialHash(string materialName, int materialId)
        {
            // Combine material name and ID for hash
            string input = $"{materialName}_{materialId}";
            
            // Simple FNV-1a 64-bit hash
            ulong hash = 14695981039346656037UL; // FNV offset basis
            foreach (char c in input)
            {
                hash ^= c;
                hash *= 1099511628211UL; // FNV prime
            }
            
            // Ensure non-zero
            if (hash == 0) hash = 1;
            
            return hash;
        }
        
        /// <summary>
        /// Start background thread for async material creation
        /// </summary>
        private void StartMaterialCreationThread()
        {
            if (materialCreationThreadRunning)
                return;
                
            materialCreationThreadRunning = true;
            materialCreationThread = new Thread(MaterialCreationThreadFunc);
            materialCreationThread.IsBackground = true;
            materialCreationThread.Start();
            LogSource.LogInfo("Started async material creation thread");
        }
        
        /// <summary>
        /// Background thread function for creating materials
        /// </summary>
        private void MaterialCreationThreadFunc()
        {
            LogSource.LogInfo("Material creation thread running");
            
            while (materialCreationThreadRunning)
            {
                int materialId = -1;
                
                // Check if there's work to do
                lock (pendingMaterialCreation)
                {
                    if (pendingMaterialCreation.Count > 0)
                    {
                        materialId = pendingMaterialCreation.Dequeue();
                    }
                }
                
                if (materialId >= 0 && materialTextureData.ContainsKey(materialId))
                {
                    // Wait longer to ensure textures are uploaded and in texture table
                    // CreateTexture uses async GPU work that needs time to complete
                    Thread.Sleep(200);
                    
                    var matData = materialTextureData[materialId];
                    IntPtr handle = CreateRemixMaterialSimple(ref matData, materialId);
                    
                    if (handle != IntPtr.Zero)
                    {
                        // Update the stored data with the handle
                        matData.remixMaterialHandle = handle;
                        materialTextureData[materialId] = matData;
                    }
                }
                else
                {
                    // No work, sleep longer
                    Thread.Sleep(100);
                }
            }
            
            LogSource.LogInfo("Material creation thread stopped");
        }
        
        /// <summary>
        /// Create a simplified Remix material without opaque extension (to avoid hang)
        /// </summary>
        private IntPtr CreateRemixMaterialSimple(ref MaterialTextureData matData, int materialId)
        {
            if (CreateMaterial == null)
                return IntPtr.Zero;
                
            try
            {
                // Get texture paths
                string albedoPath = GetTexturePathFromHandle(matData.albedoHandle);
                string normalPath = GetTexturePathFromHandle(matData.normalHandle);
                
                // Generate 64-bit material hash (Remix uses full 64-bit hashes)
                ulong matHash = GenerateMaterialHash(matData.materialName, materialId);
                
                // Create simple material info WITHOUT pNext chain
                var materialInfo = new RemixAPI.remixapi_MaterialInfo
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO,
                    pNext = IntPtr.Zero,  // No extension chain
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
                
                // Create material
                var result = CreateMaterial(ref materialInfo, out IntPtr materialHandle);
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    LogSource.LogError($"Failed to create Remix material for '{matData.materialName}': {result}");
                    return IntPtr.Zero;
                }
                
                LogSource.LogInfo($"Created Remix material '{matData.materialName}' with hash 0x{matHash:X} (handle returned: {materialHandle}) (albedo: {albedoPath ?? "none"}, normal: {normalPath ?? "none"})");
                
                return materialHandle;
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Exception creating Remix material for '{matData.materialName}': {ex.Message}\n{ex.StackTrace}");
                return IntPtr.Zero;
            }
        }
        
        /// <summary>
        /// Create a Remix material from captured texture data (FULL VERSION - may cause hang)
        /// </summary>
        private IntPtr CreateRemixMaterial(ref MaterialTextureData matData, int materialId)
        {
            if (CreateMaterial == null)
                return IntPtr.Zero;
                
            try
            {
                // Get texture paths
                string albedoPath = GetTexturePathFromHandle(matData.albedoHandle);
                string normalPath = GetTexturePathFromHandle(matData.normalHandle);
                
                // Generate 64-bit material hash (Remix uses full 64-bit hashes)
                ulong matHash = GenerateMaterialHash(matData.materialName, materialId);
                
                // Create opaque extension with PBR properties
                var opaqueExt = new RemixAPI.remixapi_MaterialInfoOpaqueEXT
                {
                    sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_OPAQUE_EXT,
                    pNext = IntPtr.Zero,
                    roughnessTexture = null,
                    metallicTexture = null,
                    anisotropy = 0.0f,
                    albedoConstant = new RemixAPI.remixapi_Float3D 
                    { 
                        x = matData.albedoColor.r, 
                        y = matData.albedoColor.g, 
                        z = matData.albedoColor.b 
                    },
                    opacityConstant = matData.albedoColor.a,
                    roughnessConstant = 0.5f,  // Default medium roughness
                    metallicConstant = 0.0f,   // Default non-metallic
                    thinFilmThickness_hasvalue = 0,
                    thinFilmThickness_value = 0.0f,
                    alphaIsThinFilmThickness = 0,
                    heightTexture = null,
                    displaceIn = 0.0f,
                    useDrawCallAlphaState = 0,
                    blendType_hasvalue = 0,
                    blendType_value = 0,
                    invertedBlend = 0,
                    alphaTestType = 0,
                    alphaReferenceValue = 0,
                    displaceOut = 0.0f
                };
                
                // Pin the extension struct
                GCHandle opaqueHandle = GCHandle.Alloc(opaqueExt, GCHandleType.Pinned);
                
                try
                {
                    // Create main material info
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
                        filterMode = 0,
                        wrapModeU = 0,
                        wrapModeV = 0
                    };
                    
                    // Create material
                    var result = CreateMaterial(ref materialInfo, out IntPtr materialHandle);
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        LogSource.LogError($"Failed to create Remix material for '{matData.materialName}': {result}");
                        return IntPtr.Zero;
                    }
                    
                    LogSource.LogInfo($"Created Remix material '{matData.materialName}' with hash 0x{matHash:X} (albedo: {albedoPath ?? "none"}, normal: {normalPath ?? "none"})");
                    
                    return materialHandle;
                }
                finally
                {
                    opaqueHandle.Free();
                }
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Exception creating Remix material for '{matData.materialName}': {ex.Message}");
                return IntPtr.Zero;
            }
        }
        
        /// <summary>
        /// Upload a Unity texture to Remix
        /// </summary>
        private IntPtr UploadUnityTexture(Texture2D unityTexture, bool srgb = true)
        {
            if (unityTexture == null || CreateTexture == null)
                return IntPtr.Zero;
                
            int texId = unityTexture.GetInstanceID();
            
            // Check cache first
            if (textureCache.TryGetValue(texId, out IntPtr cachedHandle))
            {
                return cachedHandle;
            }
            
            try
            {
                // Get raw texture data from Unity
                byte[] pixelData;
                byte[] hashSourceData;
                RemixAPI.remixapi_Format format;
                uint actualMipLevels = (uint)unityTexture.mipmapCount; // Track actual mip levels we have
                
                // Try to read texture data - if not readable, force-read from GPU
                if (!unityTexture.isReadable)
                {
                    LogSource.LogInfo($"Texture '{unityTexture.name}' is not readable - forcing GPU readback");
                    LogSource.LogInfo($"  Original format: {unityTexture.format}, Dimensions: {unityTexture.width}x{unityTexture.height}, Mipmaps: {unityTexture.mipmapCount}");
                    LogSource.LogInfo($"  TextureFormat enum value: {(int)unityTexture.format}");
                    LogSource.LogInfo($"  Filter mode: {unityTexture.filterMode}, Wrap mode: {unityTexture.wrapMode}");
                    LogSource.LogInfo($"  sRGB requested: {srgb}");
                    
                    // Determine proper color space for RenderTexture
                    RenderTextureReadWrite colorSpace = srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
                    
                    // Create a temporary RenderTexture and copy the texture to it
                    RenderTexture tmp = RenderTexture.GetTemporary(
                        unityTexture.width,
                        unityTexture.height,
                        0,
                        RenderTextureFormat.ARGB32,
                        colorSpace);
                    
                    // Copy the texture to the RenderTexture using proper filtering
                    RenderTexture previous = RenderTexture.active;
                    Graphics.Blit(unityTexture, tmp);
                    RenderTexture.active = tmp;
                    
                    // Create readable texture with proper format
                    Texture2D readableTexture = new Texture2D(unityTexture.width, unityTexture.height, TextureFormat.RGBA32, false, !srgb);
                    readableTexture.ReadPixels(new Rect(0, 0, tmp.width, tmp.height), 0, 0);
                    readableTexture.Apply();
                    
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(tmp);
                    
                    // Now read from the readable copy
                    pixelData = readableTexture.GetRawTextureData();
                    hashSourceData = pixelData;
                    format = srgb ? RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_SRGB 
                                  : RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                    
                    LogSource.LogInfo($"  GPU readback complete: {pixelData.Length} bytes, Format: RGBA32");
                    LogSource.LogInfo($"  NOTE: GPU readback only captures 1 mip level, overriding mipmap count");
                    
                    // GPU readback only gives us the top mip level, not all mipmaps!
                    actualMipLevels = 1;
                    
                    // Sample first few pixels for debugging
                    if (pixelData.Length >= 16)
                    {
                        LogSource.LogInfo($"  First 4 pixels (RGBA): " +
                            $"[{pixelData[0]},{pixelData[1]},{pixelData[2]},{pixelData[3]}] " +
                            $"[{pixelData[4]},{pixelData[5]},{pixelData[6]},{pixelData[7]}] " +
                            $"[{pixelData[8]},{pixelData[9]},{pixelData[10]},{pixelData[11]}] " +
                            $"[{pixelData[12]},{pixelData[13]},{pixelData[14]},{pixelData[15]}]");
                    }
                    
                    // Clean up temporary texture
                    UnityEngine.Object.Destroy(readableTexture);
                }
                // Handle format conversion for unsupported formats
                else if (unityTexture.format == TextureFormat.RGB24)
                {
                    LogSource.LogInfo($"Texture '{unityTexture.name}' is RGB24 - converting to RGBA32");
                    
                    // Convert RGB24 to RGBA32 (add alpha channel)
                    Color32[] pixels = unityTexture.GetPixels32();
                    pixelData = new byte[pixels.Length * 4];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixelData[i * 4 + 0] = pixels[i].r;
                        pixelData[i * 4 + 1] = pixels[i].g;
                        pixelData[i * 4 + 2] = pixels[i].b;
                        pixelData[i * 4 + 3] = 255; // Full alpha
                    }
                    hashSourceData = pixelData;
                    format = srgb ? RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_SRGB 
                                  : RemixAPI.remixapi_Format.REMIXAPI_FORMAT_R8G8B8A8_UNORM;
                }
                else
                {
                    // Use raw texture data for supported formats
                    pixelData = unityTexture.GetRawTextureData();
                    hashSourceData = pixelData;
                    
                    // Determine format based on Unity texture format
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
                            LogSource.LogWarning($"Unsupported texture format: {unityTexture.format} for texture '{unityTexture.name}'");
                            return IntPtr.Zero;
                    }
                }
                
                // ===== CRITICAL: Compute XXH64 hash of actual texture data =====
                ulong textureHash = XXHash64.ComputeHash(hashSourceData, 0, hashSourceData.Length);
                
                // Ensure non-zero hash (Remix uses 0 as invalid)
                if (textureHash == 0) textureHash = 1;
                
                // Cache the hash for later use
                textureHashCache[texId] = textureHash;
                
                LogSource.LogInfo($"Computed XXH64 hash for '{unityTexture.name}': 0x{textureHash:X16} ({unityTexture.width}x{unityTexture.height}, {unityTexture.format})");
                
                // Pin pixel data in memory
                GCHandle pixelHandle = GCHandle.Alloc(pixelData, GCHandleType.Pinned);
                
                try
                {
                    
                    // Create texture info
                    var textureInfo = new RemixAPI.remixapi_TextureInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_TEXTURE_INFO,
                        pNext = IntPtr.Zero,
                        hash = textureHash,
                        width = (uint)unityTexture.width,
                        height = (uint)unityTexture.height,
                        depth = 1,
                        mipLevels = actualMipLevels, // Use actual mip count (1 for GPU readback, original for readable)
                        format = format,
                        data = pixelHandle.AddrOfPinnedObject(),
                        dataSize = (ulong)pixelData.Length
                    };
                    
                    // Upload to Remix
                    var result = CreateTexture(ref textureInfo, out IntPtr textureHandle);
                    
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        LogSource.LogError($"Failed to create texture '{unityTexture.name}': {result}");
                        return IntPtr.Zero;
                    }
                    
                    // Cache it
                    textureCache[texId] = textureHandle;
                    
                    LogSource.LogInfo($"Successfully uploaded texture '{unityTexture.name}' to Remix with handle: 0x{textureHandle.ToInt64():X}");
                    
                    return textureHandle;
                }
                finally
                {
                    pixelHandle.Free();
                }
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Exception uploading texture '{unityTexture.name}': {ex.Message}");
                return IntPtr.Zero;
            }
        }
        
        /// <summary>
        /// Get texture path string for use in materials (converts handle to hash string)
        /// </summary>
        private string GetTexturePathFromHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return null;
                
            ulong hash = (ulong)handle;
            return $"0x{hash:X}";
        }
    }
    
    /// <summary>
    /// Persistent behaviour that survives scene changes and handles LateUpdate for skinned mesh capture
    /// </summary>
    public class RemixPersistentBehaviour : MonoBehaviour
    {
        private UnityRemixPlugin plugin;
        private static ManualLogSource LogSource => UnityRemixPlugin.LogSource;
        
        public void Initialize(UnityRemixPlugin sourcePlugin)
        {
            plugin = sourcePlugin;
            LogSource.LogInfo("RemixPersistentBehaviour initialized");
        }
        
        void LateUpdate()
        {
            // Cast to object to avoid Unity's lifetime check (overloaded == operator)
            // We know the C# object exists and we want to keep using it
            if ((object)plugin != null)
            {
                plugin.UpdateFromPersistent();
            }
            else
            {
                if (Time.frameCount % 300 == 0)
                    LogSource.LogWarning("RemixPersistentBehaviour: Plugin reference is null!");
            }
        }
        
        void OnApplicationQuit()
        {
            UnityRemixPlugin.SetQuitting();
        }
    }
}
