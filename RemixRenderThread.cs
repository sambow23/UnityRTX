using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Manages the background render thread for Remix
    /// </summary>
    public class RemixRenderThread
    {
        private readonly ManualLogSource logger;
        private readonly RemixWindowManager windowManager;
        private readonly RemixCameraHandler cameraHandler;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixLightConverter lightConverter;
        private readonly RemixFrameCapture frameCapture;
        
        private readonly ConfigEntry<int> configTargetFPS;
        private readonly ConfigEntry<int> configDebugLogInterval;
        private readonly ConfigEntry<bool> configEnableLights;
        private readonly ConfigEntry<bool> configUseGameGeometry;
        
        // Cached delegates
        private RemixAPI.PFN_remixapi_Present presentFunc;
        
        // Thread state
        private Thread renderThread;
        private volatile bool renderThreadRunning = false;
        
        // Test objects
        private IntPtr testMeshHandle = IntPtr.Zero;
        private IntPtr testLightHandle = IntPtr.Zero;
        
        // Frame state
        private volatile RemixFrameCapture.FrameState currentFrameState = new RemixFrameCapture.FrameState();
        private readonly object captureLock = new object();
        
        public RemixRenderThread(
            ManualLogSource logger,
            RemixWindowManager windowManager,
            RemixCameraHandler cameraHandler,
            RemixMeshConverter meshConverter,
            RemixLightConverter lightConverter,
            RemixFrameCapture frameCapture,
            ConfigEntry<int> targetFPS,
            ConfigEntry<int> debugLogInterval,
            ConfigEntry<bool> enableLights,
            ConfigEntry<bool> useGameGeometry,
            RemixAPI.remixapi_Interface remixInterface)
        {
            this.logger = logger;
            this.windowManager = windowManager;
            this.cameraHandler = cameraHandler;
            this.meshConverter = meshConverter;
            this.lightConverter = lightConverter;
            this.frameCapture = frameCapture;
            this.configTargetFPS = targetFPS;
            this.configDebugLogInterval = debugLogInterval;
            this.configEnableLights = enableLights;
            this.configUseGameGeometry = useGameGeometry;
            
            // Cache delegate
            if (remixInterface.Present != IntPtr.Zero)
            {
                presentFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Present>(
                    remixInterface.Present);
            }
        }
        
        /// <summary>
        /// Update frame state (called from main thread)
        /// </summary>
        public void UpdateFrameState(RemixFrameCapture.FrameState newState)
        {
            lock (captureLock)
            {
                currentFrameState = newState;
            }
        }
        
        /// <summary>
        /// Start the render thread
        /// </summary>
        public void Start()
        {
            if (renderThread != null && renderThread.IsAlive)
            {
                logger.LogInfo("Render thread already running");
                return;
            }
            
            renderThreadRunning = true;
            renderThread = new Thread(RenderThreadLoop);
            renderThread.IsBackground = true;
            renderThread.Start();
            logger.LogInfo("Render thread started");
        }
        
        /// <summary>
        /// Stop the render thread
        /// </summary>
        public void Stop()
        {
            renderThreadRunning = false;
            if (renderThread != null && renderThread.IsAlive)
            {
                renderThread.Join(1000);
            }
        }
        
        /// <summary>
        /// Main render loop
        /// </summary>
        private void RenderThreadLoop()
        {
            logger.LogInfo("Render thread loop starting...");
            
            // Create window on this thread
            if (!windowManager.CreateRemixWindow())
            {
                logger.LogError("Failed to create Remix window on render thread");
                return;
            }
            
            // Create test objects
            logger.LogInfo("Creating test triangle and light...");
            testMeshHandle = meshConverter.CreateTestTriangle();
            testLightHandle = lightConverter.CreateTestLight();
            
            int frameNum = 0;
            
            while (renderThreadRunning)
            {
                try
                {
                    // Process messages
                    windowManager.PumpWindowsMessages();
                    
                    // Render frame
                    RenderFrame(frameNum);
                    frameNum++;
                    
                    // Frame rate limiting
                    uint waitMs = 0;
                    if (configTargetFPS.Value > 0)
                    {
                        waitMs = (uint)(1000 / configTargetFPS.Value);
                    }
                    else
                    {
                        waitMs = 1; // Uncapped but still responsive
                    }
                    
                    // Wait for messages or timeout
                    if (windowManager.WaitForMessages(waitMs))
                    {
                        windowManager.PumpWindowsMessages();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Render thread error: {ex}");
                    Thread.Sleep(1000);
                }
            }
            
            logger.LogInfo("Render thread loop ended");
        }
        
        /// <summary>
        /// Render a single frame
        /// </summary>
        private void RenderFrame(int frameNum)
        {
            try
            {
                if (configUseGameGeometry.Value)
                {
                    // Process queued mesh creation on render thread (prevents main thread deadlocks)
                    frameCapture?.ProcessMeshCreationBatch();
                    
                    // Render game geometry
                    RenderGameGeometry();
                    
                    // Process Unity lights
                    lightConverter.ProcessLights(frameNum);
                    
                    // Draw test light if lights disabled
                    if (!configEnableLights.Value && testLightHandle != IntPtr.Zero)
                    {
                        // DrawLightInstance would be called here
                    }
                }
                else
                {
                    // Test mode - just render triangle and light
                    cameraHandler.SetupTestCamera(
                        windowManager.WindowWidth,
                        windowManager.WindowHeight,
                        frameNum
                    );
                    
                    // Draw test objects
                    if (testMeshHandle != IntPtr.Zero)
                    {
                        meshConverter.DrawMeshInstance(
                            testMeshHandle,
                            UnityEngine.Matrix4x4.identity,
                            1
                        );
                    }
                }
                
                // Present
                if (presentFunc != null)
                {
                    var presentInfo = new RemixAPI.remixapi_PresentInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_PRESENT_INFO,
                        pNext = IntPtr.Zero,
                        hwndOverride = IntPtr.Zero
                    };
                    
                    var result = presentFunc(ref presentInfo);
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        if (configDebugLogInterval.Value > 0 && frameNum % configDebugLogInterval.Value == 0)
                        {
                            logger.LogWarning($"Present failed: {result}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in RenderFrame: {ex}");
            }
        }
        
        /// <summary>
        /// Render game geometry from captured frame state
        /// </summary>
        private void RenderGameGeometry()
        {
            // Get frame state atomically
            RemixFrameCapture.FrameState state;
            lock (captureLock)
            {
                state = currentFrameState;
            }
            
            // Setup camera - CRITICAL for rendering!
            if (state.camera.valid)
            {
                var cam = state.camera;
                // Convert camera from Unity Y-up to Z-up coordinate system
                cameraHandler.SetupRemixCamera(
                    cam.position, cam.forward, cam.up, cam.right,
                    cam.fov, cam.aspect, cam.nearPlane, cam.farPlane
                );
            }
            else
            {
                // Fallback to test camera if no valid camera captured
                cameraHandler.SetupTestCamera(
                    windowManager.WindowWidth,
                    windowManager.WindowHeight,
                    state.frameCount
                );
            }
            
            // Draw static mesh instances
            uint objectPickingValue = 1;
            
            foreach (var instance in state.instances)
            {
                if (meshConverter.TryGetMeshHandle(instance.meshId, out IntPtr meshHandle))
                {
                    meshConverter.DrawMeshInstance(meshHandle, instance.localToWorld, objectPickingValue);
                    objectPickingValue++;
                }
            }
            
            // Draw skinned meshes
            RenderSkinnedMeshes(state, objectPickingValue);
        }
        
        /// <summary>
        /// Render skinned meshes from frame state
        /// </summary>
        private void RenderSkinnedMeshes(RemixFrameCapture.FrameState state, uint startObjectPickingValue)
        {
            if (state.skinned == null || state.skinned.Count == 0)
                return;
            
            HashSet<int> updatedMeshes = new HashSet<int>();
            uint objectPickingValue = startObjectPickingValue;
            
            foreach (var skinned in state.skinned)
            {
                try
                {
                    // Create mesh from baked data with material
                    IntPtr meshHandle = meshConverter.CreateRemixMeshFromData(
                        skinned.meshId,
                        skinned.vertices,
                        skinned.normals,
                        skinned.uvs,
                        skinned.triangles,
                        state.frameCount,
                        skinned.materialId
                    );
                    
                    if (meshHandle == IntPtr.Zero)
                        continue;
                    
                    meshConverter.UpdateSkinnedMeshHandle(skinned.meshId, meshHandle);
                    updatedMeshes.Add(skinned.meshId);
                    
                    // Draw instance
                    //logger.LogInfo($"Drawing skinned mesh {skinned.meshId}: meshHandle=0x{meshHandle.ToInt64():X}, materialId={skinned.materialId}");
                    meshConverter.DrawMeshInstance(meshHandle, skinned.localToWorld, objectPickingValue);
                    objectPickingValue++;
                }
                catch { }
            }
            
            // Cleanup stale meshes periodically
            if (state.frameCount % 60 == 0)
            {
                meshConverter.CleanupStaleSkinnedMeshes(updatedMeshes);
            }
        }
    }
}
