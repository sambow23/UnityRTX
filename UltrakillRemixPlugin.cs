using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Main BepInEx plugin - orchestrates all Remix components
    /// Refactored from 3309 lines to ~350 lines of orchestration code
    /// </summary>
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
        private ConfigEntry<bool> configUseVisibilityCulling;
        private ConfigEntry<int> configRendererCacheDuration;
        private ConfigEntry<int> configDebugLogInterval;
        private ConfigEntry<bool> configEnableLights;
        private ConfigEntry<float> configLightIntensityMultiplier;
        private ConfigEntry<int> configTargetFPS;

        // Debug Toggles
        private ConfigEntry<bool> configCaptureStaticMeshes;
        private ConfigEntry<bool> configCaptureSkinnedMeshes;
        private ConfigEntry<bool> configCaptureTextures;
        private ConfigEntry<bool> configCaptureMaterials;
        
        public static ManualLogSource LogSource { get; private set; }
        private RemixAPI.remixapi_Interface remixInterface;
        private IntPtr remixDll = IntPtr.Zero;
        private bool remixInitialized = false;
        private bool deviceRegistered = false;
        
        // COMPONENTS - All functionality delegated to these
        private RemixWindowManager windowManager;
        private RemixCameraHandler cameraHandler;
        private RemixLightConverter lightConverter;
        private RemixMaterialManager materialManager;
        private RemixMeshConverter meshConverter;
        private RemixFrameCapture frameCapture;
        private RemixRenderThread renderThread;
        private TextureCategoryManager textureCategoryManager;
        
        private int frameCount = 0;
        private static bool isQuitting = false;
        
        // Shared lock for all Remix API calls to prevent deadlocks
        private static readonly object remixApiLock = new object();
        
        void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo($"Plugin {PluginName} v{PluginVersion} is loading!");
            
            // Initialize configuration
            InitializeConfig();
            
            // Persist across scenes
            DontDestroyOnLoad(this.gameObject);
            hideFlags = HideFlags.HideAndDontSave;
            
            LogSource.LogInfo($"GameObject: {gameObject.name}, Active: {gameObject.activeSelf}, Enabled: {enabled}");
            
            // Subscribe to scene events
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            
            // Load Remix API
            try
            {
                LogSource.LogInfo("Loading Remix API interface...");
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
            configCameraName = Config.Bind("Camera", "CameraName", "",
                "Specific camera name to use for RTX Remix rendering. Leave empty to use auto-detection.");
            
            configCameraTag = Config.Bind("Camera", "CameraTag", "MainCamera",
                "Camera tag to search for if CameraName is not set.");
            
            configListCameras = Config.Bind("Camera", "ListCamerasOnSceneLoad", true,
                "Log all available cameras when a scene loads to help identify the correct camera name.");
            
            // Rendering Settings
            configUseGameGeometry = Config.Bind("Rendering", "EnableGameGeometry", true,
                "Enable rendering of game geometry through RTX Remix.");
            
            configUseDistanceCulling = Config.Bind("Rendering", "EnableDistanceCulling", false,
                "Enable distance-based culling of objects.");
            
            configMaxRenderDistance = Config.Bind("Rendering", "MaxRenderDistance", 500f,
                new ConfigDescription("Maximum render distance in Unity units.",
                    new AcceptableValueRange<float>(10f, 10000f)));
            
            configUseVisibilityCulling = Config.Bind("Rendering", "UseVisibilityCulling", false,
                "Use Unity's renderer.isVisible check to filter out invisible renderers. May cause visual issues in some games - disable if you see missing geometry.");
            
            configRendererCacheDuration = Config.Bind("Performance", "RendererCacheDuration", 300,
                new ConfigDescription("Number of frames to cache renderer list before refreshing.",
                    new AcceptableValueRange<int>(60, 3600)));
            
            configDebugLogInterval = Config.Bind("Debug", "DetailedLogInterval", 1800,
                new ConfigDescription("Number of frames between detailed logs. Set to 0 to disable.",
                    new AcceptableValueRange<int>(0, 10800)));
            
            // Lighting Settings
            configEnableLights = Config.Bind("Lighting", "EnableLights", true,
                "Convert Unity lights to RTX Remix lights.");
            
            configLightIntensityMultiplier = Config.Bind("Lighting", "IntensityMultiplier", 1.0f,
                new ConfigDescription("Global multiplier for all light intensities.",
                    new AcceptableValueRange<float>(0.01f, 100f)));
            
            // Performance Settings
            configTargetFPS = Config.Bind("Performance", "TargetFPS", 0,
                new ConfigDescription("Target FPS for the Remix render thread. Set to 0 for uncapped.",
                    new AcceptableValueRange<int>(0, 500)));

            // Debug Toggles
            configCaptureStaticMeshes = Config.Bind("Debug", "CaptureStaticMeshes", true,
                "Enable capturing and rendering of static meshes.");
            
            configCaptureSkinnedMeshes = Config.Bind("Debug", "CaptureSkinnedMeshes", true,
                "Enable capturing and rendering of skinned meshes.");
            
            configCaptureTextures = Config.Bind("Debug", "CaptureTextures", true,
                "Enable texture capturing and uploading.");
            
            configCaptureMaterials = Config.Bind("Debug", "CaptureMaterials", true,
                "Enable material capturing and creation.");
            
            LogSource.LogInfo("Configuration loaded:");
            LogSource.LogInfo($"  Camera Name: '{configCameraName.Value}' (empty = auto-detect)");
            LogSource.LogInfo($"  Camera Tag: '{configCameraTag.Value}'");
            LogSource.LogInfo($"  Game Geometry: {configUseGameGeometry.Value}");
            LogSource.LogInfo($"  Target FPS: {(configTargetFPS.Value == 0 ? "Uncapped" : configTargetFPS.Value.ToString())}");
        }
        
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            LogSource.LogInfo($"Scene loaded: {scene.name}, mode: {mode}");
            
            // Reset camera tracking
            cameraHandler?.ResetTracking();
            
            // List cameras if enabled
            if (configListCameras.Value && cameraHandler != null)
            {
                cameraHandler.ListAvailableCameras();
            }
            
            // Invalidate caches
            frameCapture?.InvalidateCaches();
            lightConverter?.ClearCache();
            
            // Refresh light cache on scene load
            lightConverter?.RefreshLightCache();
            
            // Initialize Remix if needed
            if (remixInitialized && !deviceRegistered)
            {
                LogSource.LogInfo("Starting Remix initialization...");
                try
                {
                    InitializeRemixDevice();
                }
                catch (Exception ex)
                {
                    LogSource.LogError($"Failed to initialize Remix device: {ex}");
                }
            }
            
            // Capture initial data
            if (configUseGameGeometry.Value && deviceRegistered && frameCapture != null)
            {
                var initialState = new RemixFrameCapture.FrameState();
                frameCapture.CaptureStaticMeshes(initialState, frameCount);
                renderThread?.UpdateFrameState(initialState);
            }
        }
        
        private void LoadRemixInterface()
        {
            LogSource.LogInfo("Loading Remix API interface...");
            
            // Find d3d9.dll
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
            
            // Load API
            var result = RemixAPI.InitializeRemixAPI(dllPath, out remixInterface, out remixDll);
            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                LogSource.LogError($"Failed to load Remix API: {result}");
                return;
            }
            
            LogSource.LogInfo("Remix API loaded successfully!");
            LogSource.LogInfo($"Interface pointers - CreateMesh: {remixInterface.CreateMesh}, DrawInstance: {remixInterface.DrawInstance}");
            
            remixInitialized = true;
            
            // Initialize components
            InitializeComponents();
            
            // Start device registration
            InvokeRepeating("TryRegisterDevice", 2.0f, 1.0f);
        }
        
        private void InitializeComponents()
        {
            LogSource.LogInfo("Initializing components...");
            
            // Create all components with dependencies
            textureCategoryManager = new TextureCategoryManager();
            
            windowManager = new RemixWindowManager(LogSource, remixInterface);
            
            cameraHandler = new RemixCameraHandler(
                LogSource,
                configCameraName,
                configCameraTag,
                configListCameras,
                remixInterface
            );
            
            lightConverter = new RemixLightConverter(
                LogSource,
                configEnableLights,
                configLightIntensityMultiplier,
                configDebugLogInterval,
                remixInterface,
                remixApiLock
            );
            
            materialManager = new RemixMaterialManager(
                LogSource,
                textureCategoryManager,
                configCaptureTextures,
                configCaptureMaterials,
                remixInterface,
                remixApiLock
            );
            
            meshConverter = new RemixMeshConverter(
                LogSource,
                materialManager,
                configDebugLogInterval,
                remixInterface,
                remixApiLock
            );
            
            frameCapture = new RemixFrameCapture(
                LogSource,
                cameraHandler,
                meshConverter,
                materialManager,
                configUseDistanceCulling,
                configMaxRenderDistance,
                configUseVisibilityCulling,
                configRendererCacheDuration,
                configDebugLogInterval,
                configCaptureStaticMeshes,
                configCaptureSkinnedMeshes
            );
            
            renderThread = new RemixRenderThread(
                LogSource,
                windowManager,
                cameraHandler,
                meshConverter,
                lightConverter,
                frameCapture,
                configTargetFPS,
                configDebugLogInterval,
                configEnableLights,
                configUseGameGeometry,
                remixInterface
            );
            
            LogSource.LogInfo("All components initialized");
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
                InitializeRemixDevice();
                if (deviceRegistered)
                {
                    CancelInvoke("TryRegisterDevice");
                }
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Failed in TryRegisterDevice: {ex}");
            }
        }
        
        private void InitializeRemixDevice()
        {
            // Set window dimensions
            int width = Screen.width > 0 ? Screen.width : 1920;
            int height = Screen.height > 0 ? Screen.height : 1080;
            
            if (windowManager != null)
            {
                windowManager.SetWindowDimensions(width, height);
            }
            
            deviceRegistered = true;
            
            // Start render thread (creates window and begins rendering)
            LogSource.LogInfo("Starting render thread...");
            renderThread?.Start();
        }
        
        void Update()
        {
            frameCount++;
            
            // Device registration happens via InvokeRepeating in TryRegisterDevice
        }
        
        void LateUpdate()
        {
            // Capture frame data on main thread
            UpdateFromPersistent();
        }
        
        void OnApplicationQuit()
        {
            LogSource.LogInfo("Application quitting...");
            isQuitting = true;
        }
        
        public void UpdateFromPersistent()
        {
            if (!remixInitialized || !deviceRegistered)
                return;
            
            if (frameCount % 300 == 1 && LogSource != null)
            {
                LogSource.LogInfo($"UpdateFromPersistent: frame={frameCount}, initialized={remixInitialized}, deviceReg={deviceRegistered}");
            }
            
            frameCount++;
            
            if (configUseGameGeometry.Value && frameCapture != null && renderThread != null)
            {
                var nextState = new RemixFrameCapture.FrameState();
                nextState.frameCount = frameCount;
                
                // Refresh light cache periodically (same interval as renderer cache)
                if (frameCount % configRendererCacheDuration.Value == 0 && lightConverter != null)
                {
                    lightConverter.RefreshLightCache();
                }
                
                // Capture static meshes and camera
                frameCapture.CaptureStaticMeshes(nextState, frameCount);
                
                // Capture skinned meshes
                frameCapture.CaptureSkinnedMeshes(nextState, frameCount);
                
                // Send to render thread (mesh creation moved to render thread to avoid deadlocks)
                renderThread.UpdateFrameState(nextState);
            }
        }
        
        void OnDestroy()
        {
            if (!isQuitting)
            {
                LogSource.LogWarning("OnDestroy called but app not quitting - recreating plugin...");
                
                var newGo = new GameObject("UnityRemix_Persistent");
                GameObject.DontDestroyOnLoad(newGo);
                newGo.hideFlags = HideFlags.HideAndDontSave;
                var newBehaviour = newGo.AddComponent<RemixPersistentBehaviour>();
                newBehaviour.Initialize(this);
                
                return;
            }
            
            LogSource.LogInfo("OnDestroy called during quit - cleaning up...");
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            CleanupRemix();
        }
        
        public static void SetQuitting()
        {
            isQuitting = true;
        }
        
        private void CleanupRemix()
        {
            if (!remixInitialized) return;
            
            LogSource.LogInfo("Cleaning up Remix...");
            
            // Stop render thread
            renderThread?.Stop();
            
            // Cleanup all components
            materialManager?.Cleanup();
            meshConverter?.Cleanup();
            frameCapture?.Cleanup();
            lightConverter?.ClearCache();
            windowManager?.DestroyRemixWindow();
            
            // Shutdown Remix API
            RemixAPI.ShutdownAndUnloadRemixDll(ref remixInterface, remixDll);
            remixDll = IntPtr.Zero;
            remixInitialized = false;
            
            LogSource.LogInfo("Remix cleanup complete");
        }
    }
}
