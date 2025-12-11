using System;
using System.Linq;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Handles camera detection, selection, and conversion to Remix format
    /// </summary>
    public class RemixCameraHandler
    {
        private readonly ManualLogSource logger;
        private readonly ConfigEntry<string> configCameraName;
        private readonly ConfigEntry<string> configCameraTag;
        private readonly ConfigEntry<bool> configListCameras;
        
        private Camera currentCamera;
        private string lastCameraName = "";
        
        // Cached delegates
        private RemixAPI.PFN_remixapi_SetupCamera setupCameraFunc;
        
        public Camera CurrentCamera => currentCamera;
        
        public RemixCameraHandler(
            ManualLogSource logger,
            ConfigEntry<string> cameraName,
            ConfigEntry<string> cameraTag,
            ConfigEntry<bool> listCameras,
            RemixAPI.remixapi_Interface remixInterface)
        {
            this.logger = logger;
            this.configCameraName = cameraName;
            this.configCameraTag = cameraTag;
            this.configListCameras = listCameras;
            
            // Cache delegate
            if (remixInterface.SetupCamera != IntPtr.Zero)
            {
                setupCameraFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_SetupCamera>(
                    remixInterface.SetupCamera);
            }
        }
        
        /// <summary>
        /// List all available cameras in the scene
        /// </summary>
        public void ListAvailableCameras()
        {
            var allCameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            logger.LogInfo($"=== Available Cameras ({allCameras.Length}) ===");
            
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
                
                logger.LogInfo($"  [{i}] '{cam.name}'{statusInfo}");
                logger.LogInfo($"      Depth: {cam.depth}, ClearFlags: {cam.clearFlags}, CullingMask: 0x{cam.cullingMask:X}");
            }
            
            logger.LogInfo("=================================");
        }
        
        /// <summary>
        /// Get the preferred camera based on configuration
        /// </summary>
        public Camera GetPreferredCamera()
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
                    logger.LogWarning($"Camera '{configCameraName.Value}' not found or inactive. Falling back to auto-detection.");
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
                    selectedCamera = taggedCameras.OrderByDescending(c => c.depth).First();
                    selectionReason = "fallback (no MainCamera tag found)";
                }
            }
            
            // Log camera change
            if (selectedCamera != null && selectedCamera.name != lastCameraName)
            {
                if (selectionReason.Contains("fallback"))
                {
                    logger.LogWarning($"Using camera: '{selectedCamera.name}' ({selectionReason})");
                }
                else
                {
                    logger.LogInfo($"Using camera: '{selectedCamera.name}' ({selectionReason})");
                }
                lastCameraName = selectedCamera.name;
                currentCamera = selectedCamera;
            }
            
            return selectedCamera;
        }
        
        /// <summary>
        /// Reset camera tracking (call on scene change)
        /// </summary>
        public void ResetTracking()
        {
            lastCameraName = "";
            currentCamera = null;
        }
        
        /// <summary>
        /// Setup Remix camera from Unity camera
        /// </summary>
        public void SetupRemixCamera(Camera unityCamera)
        {
            if (setupCameraFunc == null || unityCamera == null)
                return;
            
            Vector3 pos = unityCamera.transform.position;
            Vector3 fwd = unityCamera.transform.forward;
            Vector3 up = unityCamera.transform.up;
            Vector3 right = unityCamera.transform.right;
            
            SetupRemixCamera(pos, fwd, up, right,
                unityCamera.fieldOfView, unityCamera.aspect,
                unityCamera.nearClipPlane, unityCamera.farClipPlane);
        }
        
        /// <summary>
        /// Setup Remix camera from raw parameters (for captured frame data)
        /// </summary>
        public void SetupRemixCamera(Vector3 position, Vector3 forward, Vector3 up, Vector3 right,
            float fov, float aspect, float nearPlane, float farPlane)
        {
            if (setupCameraFunc == null)
                return;
            
            // Convert from Unity Y-up to Z-up coordinate system
            var paramCamera = new RemixAPI.remixapi_CameraInfoParameterizedEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_CAMERA_INFO_PARAMETERIZED_EXT,
                pNext = IntPtr.Zero,
                position = new RemixAPI.remixapi_Float3D(position.x, position.z, position.y),
                forward = new RemixAPI.remixapi_Float3D(forward.x, forward.z, forward.y),
                up = new RemixAPI.remixapi_Float3D(up.x, up.z, up.y),
                right = new RemixAPI.remixapi_Float3D(right.x, right.z, right.y),
                fovYInDegrees = fov,
                aspect = aspect,
                nearPlane = nearPlane,
                farPlane = farPlane
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
                
                var result = setupCameraFunc(ref cameraInfo);
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    logger.LogWarning($"SetupCamera failed: {result}");
                }
            }
            finally
            {
                paramHandle.Free();
            }
        }
        
        /// <summary>
        /// Setup a test camera at origin looking down +Z
        /// </summary>
        public void SetupTestCamera(int windowWidth, int windowHeight, int frameCount)
        {
            if (setupCameraFunc == null)
                return;
            
            var paramCamera = new RemixAPI.remixapi_CameraInfoParameterizedEXT
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_CAMERA_INFO_PARAMETERIZED_EXT,
                pNext = IntPtr.Zero,
                position = new RemixAPI.remixapi_Float3D(0, 0, 0),
                forward = new RemixAPI.remixapi_Float3D(0, 0, 1),
                up = new RemixAPI.remixapi_Float3D(0, 1, 0),
                right = new RemixAPI.remixapi_Float3D(1, 0, 0),
                fovYInDegrees = 75.0f,
                aspect = (float)windowWidth / windowHeight,
                nearPlane = 0.1f,
                farPlane = 1000.0f
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
                
                var result = setupCameraFunc(ref cameraInfo);
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                {
                    if (frameCount % 300 == 0)
                    {
                        logger.LogWarning($"SetupCamera failed: {result}");
                    }
                }
            }
            finally
            {
                paramHandle.Free();
            }
        }
    }
}
