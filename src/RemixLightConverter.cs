using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Converts Unity lights to Remix lights
    /// </summary>
    public class RemixLightConverter
    {
        private readonly ManualLogSource logger;
        private readonly ConfigEntry<bool> configEnableLights;
        private readonly ConfigEntry<float> configLightIntensityMultiplier;
        private readonly ConfigEntry<int> configDebugLogInterval;
        private readonly object apiLock;
        
        // Cached delegates
        private RemixAPI.PFN_remixapi_CreateLight createLightFunc;
        private RemixAPI.PFN_remixapi_DestroyLight destroyLightFunc;
        private RemixAPI.PFN_remixapi_DrawLightInstance drawLightInstanceFunc;
        
        // Cache for Unity lights - maps Light instance ID to Remix handle
        private Dictionary<int, IntPtr> lightCache = new Dictionary<int, IntPtr>();
        private List<Light> cachedLights = new List<Light>();
        
        public RemixLightConverter(
            ManualLogSource logger,
            ConfigEntry<bool> enableLights,
            ConfigEntry<float> intensityMultiplier,
            ConfigEntry<int> debugLogInterval,
            RemixAPI.remixapi_Interface remixInterface,
            object apiLock)
        {
            this.logger = logger;
            this.configEnableLights = enableLights;
            this.configLightIntensityMultiplier = intensityMultiplier;
            this.configDebugLogInterval = debugLogInterval;
            this.apiLock = apiLock;
            
            // Cache delegates
            if (remixInterface.CreateLight != IntPtr.Zero)
            {
                createLightFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_CreateLight>(
                    remixInterface.CreateLight);
            }
            
            if (remixInterface.DestroyLight != IntPtr.Zero)
            {
                destroyLightFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DestroyLight>(
                    remixInterface.DestroyLight);
            }
            
            if (remixInterface.DrawLightInstance != IntPtr.Zero)
            {
                drawLightInstanceFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_DrawLightInstance>(
                    remixInterface.DrawLightInstance);
            }
        }
        
        /// <summary>
        /// Refresh cached light list from scene
        /// </summary>
        public void RefreshLightCache()
        {
            if (!configEnableLights.Value)
                return;
                
            cachedLights.Clear();
            cachedLights.AddRange(UnityEngine.Object.FindObjectsOfType<Light>());
        }
        
        /// <summary>
        /// Clear light cache (call on scene change)
        /// </summary>
        public void ClearCache()
        {
            foreach (var lightHandle in lightCache.Values)
            {
                if (lightHandle != IntPtr.Zero && destroyLightFunc != null)
                {
                    try { destroyLightFunc(lightHandle); } catch { }
                }
            }
            lightCache.Clear();
            cachedLights.Clear();
        }
        
        /// <summary>
        /// Process and draw all Unity lights
        /// </summary>
        public void ProcessLights(int frameCount)
        {
            if (!configEnableLights.Value || drawLightInstanceFunc == null)
                return;
            
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
                    lightHandle = CreateRemixLightFromUnity(light, frameCount);
                    
                    if (lightHandle != IntPtr.Zero)
                    {
                        lightCache[lightId] = lightHandle;
                    }
                }
                
                // Draw light instance
                if (lightHandle != IntPtr.Zero)
                {
                    lock (apiLock)
                    {
                        drawLightInstanceFunc(lightHandle);
                    }
                }
            }
        }
        
        /// <summary>
        /// Create Remix light from Unity light
        /// </summary>
        private IntPtr CreateRemixLightFromUnity(Light light, int frameCount)
        {
            if (createLightFunc == null)
                return IntPtr.Zero;
            
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
                    isDynamic = 0,
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
                        if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 0)
                        {
                            logger.LogInfo($"Directional light '{light.name}' not yet supported");
                        }
                        break;
                    
                    default:
                        if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 0)
                        {
                            logger.LogInfo($"Light type {light.type} for '{light.name}' not supported");
                        }
                        break;
                }
                
                return lightHandle;
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to create Remix light from '{light.name}': {ex.Message}");
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
                // Convert Unity Y-up to Remix Z-up: (x, y, z) -> (x, z, y)
                position = new RemixAPI.remixapi_Float3D(position.x, position.z, position.y),
                radius = light.range * 0.1f,
                shaping_hasvalue = 0,
                shaping_value = new RemixAPI.remixapi_LightInfoLightShaping(),
                volumetricRadianceScale = 1.0f
            };
            
            GCHandle sphereHandle = GCHandle.Alloc(sphereExt, GCHandleType.Pinned);
            
            try
            {
                baseInfo.pNext = sphereHandle.AddrOfPinnedObject();
                
                IntPtr handle;
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createLightFunc(ref baseInfo, out handle);
                }
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogWarning($"Failed to create point light '{light.name}': {result}");
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
                // Convert Unity Y-up to Remix Z-up: (x, y, z) -> (x, z, y)
                direction = new RemixAPI.remixapi_Float3D(direction.x, direction.z, direction.y),
                coneAngleDegrees = light.spotAngle,
                coneSoftness = 0.1f,
                focusExponent = 1.0f
            };
            
            var sphereExt = new RemixAPI.remixapi_LightInfoSphereEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO_SPHERE_EXT,
                pNext = IntPtr.Zero,
                // Convert Unity Y-up to Remix Z-up: (x, y, z) -> (x, z, y)
                position = new RemixAPI.remixapi_Float3D(position.x, position.z, position.y),
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
                RemixAPI.remixapi_ErrorCode result;
                lock (apiLock)
                {
                    result = createLightFunc(ref baseInfo, out handle);
                }
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogWarning($"Failed to create spot light '{light.name}': {result}");
                    return IntPtr.Zero;
                }
                
                return handle;
            }
            finally
            {
                sphereHandle.Free();
            }
        }
        
        /// <summary>
        /// Create a test light for debugging
        /// </summary>
        public IntPtr CreateTestLight()
        {
            if (createLightFunc == null)
                return IntPtr.Zero;
            
            var sphereLight = new RemixAPI.remixapi_LightInfoSphereEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_LIGHT_INFO_SPHERE_EXT,
                pNext = IntPtr.Zero,
                position = new RemixAPI.remixapi_Float3D(0, -1, 0),
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
                    hash = 0x3,
                    radiance = new RemixAPI.remixapi_Float3D(100, 200, 100)
                };
                
                IntPtr handle;
                var result = createLightFunc(ref lightInfo, out handle);
                
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogError($"Failed to create test light: {result}");
                    return IntPtr.Zero;
                }
                
                logger.LogInfo($"Test light created! Handle: {handle}");
                return handle;
            }
            finally
            {
                sphereHandle.Free();
            }
        }
    }
}
