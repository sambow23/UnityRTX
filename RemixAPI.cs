using System;
using System.Runtime.InteropServices;

namespace UnityRemix
{
    /// <summary>
    /// P/Invoke bindings for Remix C API
    /// Based on remix_c.h from dxvk-remix v0.6.1
    /// </summary>
    public static class RemixAPI
    {
        #region Version Constants
        
        public const uint REMIXAPI_VERSION_MAJOR = 0;
        public const uint REMIXAPI_VERSION_MINOR = 6;
        public const uint REMIXAPI_VERSION_PATCH = 1;
        
        public static ulong REMIXAPI_VERSION_MAKE(uint major, uint minor, uint patch)
        {
            return ((ulong)major << 48) | ((ulong)minor << 16) | (ulong)patch;
        }
        
        #endregion

        #region Enums

        // Boolean type for C interop
        public enum remixapi_Bool : int
        {
            False = 0,
            True = 1
        }

        public enum remixapi_ErrorCode : int
        {
            REMIXAPI_ERROR_CODE_SUCCESS = 0,
            REMIXAPI_ERROR_CODE_GENERAL_FAILURE = 1,
            REMIXAPI_ERROR_CODE_LOAD_LIBRARY_FAILURE = 2,
            REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS = 3,
            REMIXAPI_ERROR_CODE_GET_PROC_ADDRESS_FAILURE = 4,
            REMIXAPI_ERROR_CODE_ALREADY_EXISTS = 5,
            REMIXAPI_ERROR_CODE_REGISTERING_NON_REMIX_D3D9_DEVICE = 6,
            REMIXAPI_ERROR_CODE_REMIX_DEVICE_WAS_NOT_REGISTERED = 7,
            REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION = 8,
            REMIXAPI_ERROR_CODE_SET_DLL_DIRECTORY_FAILURE = 9,
            REMIXAPI_ERROR_CODE_GET_FULL_PATH_NAME_FAILURE = 10,
            REMIXAPI_ERROR_CODE_NOT_INITIALIZED = 11,
        }

        // Struct types - values from remix_c.h
        public enum remixapi_StructType : int
        {
            REMIXAPI_STRUCT_TYPE_NONE = 0,
            REMIXAPI_STRUCT_TYPE_INITIALIZE_LIBRARY_INFO = 1,
            REMIXAPI_STRUCT_TYPE_MATERIAL_INFO = 2,
            REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_PORTAL_EXT = 3,
            REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_TRANSLUCENT_EXT = 4,
            REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_OPAQUE_EXT = 5,
            REMIXAPI_STRUCT_TYPE_LIGHT_INFO = 6,
            REMIXAPI_STRUCT_TYPE_LIGHT_INFO_DISTANT_EXT = 7,
            REMIXAPI_STRUCT_TYPE_LIGHT_INFO_CYLINDER_EXT = 8,
            REMIXAPI_STRUCT_TYPE_LIGHT_INFO_DISK_EXT = 9,
            REMIXAPI_STRUCT_TYPE_LIGHT_INFO_RECT_EXT = 10,
            REMIXAPI_STRUCT_TYPE_LIGHT_INFO_SPHERE_EXT = 11,
            REMIXAPI_STRUCT_TYPE_MESH_INFO = 12,
            REMIXAPI_STRUCT_TYPE_INSTANCE_INFO = 13,
            REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_BONE_TRANSFORMS_EXT = 14,
            REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_BLEND_EXT = 15,
            REMIXAPI_STRUCT_TYPE_CAMERA_INFO = 16,
            REMIXAPI_STRUCT_TYPE_CAMERA_INFO_PARAMETERIZED_EXT = 17,
            REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_OPAQUE_SUBSURFACE_EXT = 18,
            REMIXAPI_STRUCT_TYPE_INSTANCE_INFO_OBJECT_PICKING_EXT = 19,
            REMIXAPI_STRUCT_TYPE_LIGHT_INFO_DOME_EXT = 20,
            REMIXAPI_STRUCT_TYPE_LIGHT_INFO_USD_EXT = 21,
            REMIXAPI_STRUCT_TYPE_STARTUP_INFO = 22,
            REMIXAPI_STRUCT_TYPE_PRESENT_INFO = 23,
            REMIXAPI_STRUCT_TYPE_TEXTURE_INFO = 25,
        }

        public enum remixapi_CameraType : int
        {
            REMIXAPI_CAMERA_TYPE_WORLD = 0,
            REMIXAPI_CAMERA_TYPE_SKY = 1,
            REMIXAPI_CAMERA_TYPE_VIEW_MODEL = 2,
        }

        public enum remixapi_Format : int
        {
            REMIXAPI_FORMAT_R8G8B8A8_UNORM = 37,
            REMIXAPI_FORMAT_R8G8B8A8_SRGB = 43,
            REMIXAPI_FORMAT_B8G8R8A8_UNORM = 44,
            REMIXAPI_FORMAT_B8G8R8A8_SRGB = 50,
            REMIXAPI_FORMAT_BC1_RGB_UNORM = 131,
            REMIXAPI_FORMAT_BC1_RGB_SRGB = 132,
            REMIXAPI_FORMAT_BC3_UNORM = 135,
            REMIXAPI_FORMAT_BC3_SRGB = 136,
            REMIXAPI_FORMAT_BC5_UNORM = 139,
            REMIXAPI_FORMAT_BC7_UNORM = 145,
            REMIXAPI_FORMAT_BC7_SRGB = 146,
        }

        [Flags]
        public enum remixapi_InstanceCategoryBit : uint
        {
            REMIXAPI_INSTANCE_CATEGORY_BIT_WORLD_UI = 1 << 0,
            REMIXAPI_INSTANCE_CATEGORY_BIT_WORLD_MATTE = 1 << 1,
            REMIXAPI_INSTANCE_CATEGORY_BIT_SKY = 1 << 2,
            REMIXAPI_INSTANCE_CATEGORY_BIT_IGNORE = 1 << 3,
            REMIXAPI_INSTANCE_CATEGORY_BIT_IGNORE_LIGHTS = 1 << 4,
            REMIXAPI_INSTANCE_CATEGORY_BIT_IGNORE_ANTI_CULLING = 1 << 5,
            REMIXAPI_INSTANCE_CATEGORY_BIT_IGNORE_MOTION_BLUR = 1 << 6,
            REMIXAPI_INSTANCE_CATEGORY_BIT_IGNORE_OPACITY_MICROMAP = 1 << 7,
            REMIXAPI_INSTANCE_CATEGORY_BIT_IGNORE_ALPHA_CHANNEL = 1 << 8,
            REMIXAPI_INSTANCE_CATEGORY_BIT_HIDDEN = 1 << 9,
            REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE = 1 << 10,
            REMIXAPI_INSTANCE_CATEGORY_BIT_BEAM = 1 << 11,
            REMIXAPI_INSTANCE_CATEGORY_BIT_DECAL_STATIC = 1 << 12,
            REMIXAPI_INSTANCE_CATEGORY_BIT_DECAL_DYNAMIC = 1 << 13,
            REMIXAPI_INSTANCE_CATEGORY_BIT_DECAL_SINGLE_OFFSET = 1 << 14,
            REMIXAPI_INSTANCE_CATEGORY_BIT_DECAL_NO_OFFSET = 1 << 15,
            REMIXAPI_INSTANCE_CATEGORY_BIT_ALPHA_BLEND_TO_CUTOUT = 1 << 16,
            REMIXAPI_INSTANCE_CATEGORY_BIT_TERRAIN = 1 << 17,
            REMIXAPI_INSTANCE_CATEGORY_BIT_ANIMATED_WATER = 1 << 18,
            REMIXAPI_INSTANCE_CATEGORY_BIT_THIRD_PERSON_PLAYER_MODEL = 1 << 19,
            REMIXAPI_INSTANCE_CATEGORY_BIT_THIRD_PERSON_PLAYER_BODY = 1 << 20,
            REMIXAPI_INSTANCE_CATEGORY_BIT_IGNORE_BAKED_LIGHTING = 1 << 21,
            REMIXAPI_INSTANCE_CATEGORY_BIT_IGNORE_TRANSPARENCY_LAYER = 1 << 22,
            REMIXAPI_INSTANCE_CATEGORY_BIT_PARTICLE_EMITTER = 1 << 23,
            REMIXAPI_INSTANCE_CATEGORY_BIT_LEGACY_EMISSIVE = 1 << 24,
        }

        #endregion

        #region Structs

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_Float2D
        {
            public float x, y;

            public remixapi_Float2D(float x, float y)
            {
                this.x = x; this.y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_Float3D
        {
            public float x, y, z;

            public remixapi_Float3D(float x, float y, float z)
            {
                this.x = x; this.y = y; this.z = z;
            }
        }

        // Transform is 3x4 matrix (3 rows, 4 columns) - row major
        // float matrix[3][4]
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct remixapi_Transform
        {
            public fixed float matrix[12]; // 3x4 = 12 floats
            
            public static remixapi_Transform Identity()
            {
                var t = new remixapi_Transform();
                // Row 0: [1, 0, 0, 0]
                t.matrix[0] = 1; t.matrix[1] = 0; t.matrix[2] = 0; t.matrix[3] = 0;
                // Row 1: [0, 1, 0, 0]
                t.matrix[4] = 0; t.matrix[5] = 1; t.matrix[6] = 0; t.matrix[7] = 0;
                // Row 2: [0, 0, 1, 0]
                t.matrix[8] = 0; t.matrix[9] = 0; t.matrix[10] = 1; t.matrix[11] = 0;
                return t;
            }
            
            public static remixapi_Transform FromMatrix(
                float m00, float m01, float m02, float m03,
                float m10, float m11, float m12, float m13,
                float m20, float m21, float m22, float m23)
            {
                var t = new remixapi_Transform();
                t.matrix[0] = m00; t.matrix[1] = m01; t.matrix[2] = m02; t.matrix[3] = m03;
                t.matrix[4] = m10; t.matrix[5] = m11; t.matrix[6] = m12; t.matrix[7] = m13;
                t.matrix[8] = m20; t.matrix[9] = m21; t.matrix[10] = m22; t.matrix[11] = m23;
                return t;
            }
        }

        // Vertex struct with padding as per remix_c.h
        // position[3], normal[3], texcoord[2], color, _pad0-6 (7 padding uint32s)
        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_HardcodedVertex
        {
            public float position_x, position_y, position_z;
            public float normal_x, normal_y, normal_z;
            public float texcoord_x, texcoord_y;
            public uint color;
            public uint _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_MeshInfoSkinning
        {
            public uint bonesPerVertex;
            public IntPtr blendWeights_values;
            public uint blendWeights_count;
            public IntPtr blendIndices_values;
            public uint blendIndices_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_MeshInfoSurfaceTriangles
        {
            public IntPtr vertices_values;  // const remixapi_HardcodedVertex*
            public ulong vertices_count;
            public IntPtr indices_values;   // const uint32_t*
            public ulong indices_count;
            public uint skinning_hasvalue;  // remixapi_Bool
            public remixapi_MeshInfoSkinning skinning_value;
            public IntPtr material;         // remixapi_MaterialHandle
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_MeshInfo
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public ulong hash;
            public IntPtr surfaces_values;  // const remixapi_MeshInfoSurfaceTriangles*
            public uint surfaces_count;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_CameraInfoParameterizedEXT
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public remixapi_Float3D position;
            public remixapi_Float3D forward;
            public remixapi_Float3D up;
            public remixapi_Float3D right;
            public float fovYInDegrees;
            public float aspect;
            public float nearPlane;
            public float farPlane;
        }

        // CameraInfo uses 4x4 matrices: float view[4][4], float projection[4][4]
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct remixapi_CameraInfo
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public remixapi_CameraType type;
            public fixed float view[16];       // 4x4 matrix
            public fixed float projection[16]; // 4x4 matrix
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_InstanceInfo
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public uint categoryFlags;  // remixapi_InstanceCategoryFlags
            public IntPtr mesh;         // remixapi_MeshHandle
            public remixapi_Transform transform;
            public uint doubleSided;    // remixapi_Bool
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_InstanceInfoObjectPickingEXT
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public uint objectPickingValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_LightInfoSphereEXT
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public remixapi_Float3D position;
            public float radius;
            public uint shaping_hasvalue;  // remixapi_Bool
            public remixapi_LightInfoLightShaping shaping_value;
            public float volumetricRadianceScale;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_LightInfoLightShaping
        {
            public remixapi_Float3D direction;
            public float coneAngleDegrees;
            public float coneSoftness;
            public float focusExponent;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_LightInfo
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public ulong hash;
            public remixapi_Float3D radiance;
            public uint isDynamic;       // remixapi_Bool
            public uint ignoreViewModel; // remixapi_Bool
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_StartupInfo
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public IntPtr hwnd;
            public uint disableSrgbConversionForOutput; // remixapi_Bool
            public uint forceNoVkSwapchain;             // remixapi_Bool
            public uint editorModeEnabled;              // remixapi_Bool
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_PresentInfo
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public IntPtr hwndOverride; // Can be NULL
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_TextureInfo
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public ulong hash;
            public uint width;
            public uint height;
            public uint depth;
            public uint mipLevels;
            public remixapi_Format format;
            public IntPtr data;     // const void*
            public ulong dataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_InitializeLibraryInfo
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public ulong version;
        }

        // Material structures
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct remixapi_MaterialInfo
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            public ulong hash;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string albedoTexture;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string normalTexture;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string tangentTexture;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string emissiveTexture;
            public float emissiveIntensity;
            public remixapi_Float3D emissiveColorConstant;
            public byte spriteSheetRow;
            public byte spriteSheetCol;
            public byte spriteSheetFps;
            public byte filterMode;
            public byte wrapModeU;
            public byte wrapModeV;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct remixapi_MaterialInfoOpaqueEXT
        {
            public remixapi_StructType sType;
            public IntPtr pNext;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string roughnessTexture;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string metallicTexture;
            public float anisotropy;
            public remixapi_Float3D albedoConstant;
            public float opacityConstant;
            public float roughnessConstant;
            public float metallicConstant;
            public remixapi_Bool thinFilmThickness_hasvalue;
            public float thinFilmThickness_value;
            public remixapi_Bool alphaIsThinFilmThickness;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string heightTexture;
            public float displaceIn;
            public remixapi_Bool useDrawCallAlphaState;
            public remixapi_Bool blendType_hasvalue;
            public int blendType_value;
            public remixapi_Bool invertedBlend;
            public int alphaTestType;
            public byte alphaReferenceValue;
            public float displaceOut;
        }

        #endregion

        #region Function Pointer Delegates

        // All Remix API functions use __stdcall convention
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_Shutdown();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_CreateMaterial(
            ref remixapi_MaterialInfo info, out IntPtr out_handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_DestroyMaterial(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_CreateMesh(
            ref remixapi_MeshInfo info, out IntPtr out_handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_DestroyMesh(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_SetupCamera(ref remixapi_CameraInfo info);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_DrawInstance(ref remixapi_InstanceInfo info);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_CreateLight(
            ref remixapi_LightInfo info, out IntPtr out_handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_DestroyLight(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_DrawLightInstance(IntPtr lightHandle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_SetConfigVariable(
            [MarshalAs(UnmanagedType.LPStr)] string key,
            [MarshalAs(UnmanagedType.LPStr)] string value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_AddTextureHash(
            [MarshalAs(UnmanagedType.LPStr)] string textureCategory,
            [MarshalAs(UnmanagedType.LPStr)] string textureHash);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_RemoveTextureHash(
            [MarshalAs(UnmanagedType.LPStr)] string textureCategory,
            [MarshalAs(UnmanagedType.LPStr)] string textureHash);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_Startup(ref remixapi_StartupInfo info);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_Present(ref remixapi_PresentInfo info);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_InitializeLibrary(
            ref remixapi_InitializeLibraryInfo info,
            out remixapi_Interface out_result);

        // DXVK interop functions
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_dxvk_CreateD3D9(
            uint editorModeEnabled,  // remixapi_Bool
            out IntPtr out_pD3D9);   // IDirect3D9Ex**

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_dxvk_RegisterD3D9Device(
            IntPtr d3d9Device);      // IDirect3DDevice9Ex*

        // Texture functions
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_CreateTexture(
            ref remixapi_TextureInfo info, out IntPtr out_handle);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate remixapi_ErrorCode PFN_remixapi_DestroyTexture(IntPtr handle);

        #endregion

        #region Interface Struct

        // This struct layout MUST match remix_c.h exactly!
        // Order matters - it's the order in which function pointers appear
        [StructLayout(LayoutKind.Sequential)]
        public struct remixapi_Interface
        {
            public IntPtr Shutdown;              // PFN_remixapi_Shutdown
            public IntPtr CreateMaterial;        // PFN_remixapi_CreateMaterial
            public IntPtr DestroyMaterial;       // PFN_remixapi_DestroyMaterial
            public IntPtr CreateMesh;            // PFN_remixapi_CreateMesh
            public IntPtr CreateMeshBatched;     // PFN_remixapi_CreateMeshBatched
            public IntPtr DestroyMesh;           // PFN_remixapi_DestroyMesh
            public IntPtr SetupCamera;           // PFN_remixapi_SetupCamera
            public IntPtr DrawInstance;          // PFN_remixapi_DrawInstance
            public IntPtr CreateLight;           // PFN_remixapi_CreateLight
            public IntPtr CreateLightBatched;    // PFN_remixapi_CreateLightBatched
            public IntPtr DestroyLight;          // PFN_remixapi_DestroyLight
            public IntPtr DrawLightInstance;     // PFN_remixapi_DrawLightInstance
            public IntPtr SetConfigVariable;     // PFN_remixapi_SetConfigVariable
            public IntPtr AddTextureHash;        // PFN_remixapi_AddTextureHash
            public IntPtr RemoveTextureHash;     // PFN_remixapi_RemoveTextureHash
            public IntPtr CreateTexture;         // PFN_remixapi_CreateTexture
            public IntPtr DestroyTexture;        // PFN_remixapi_DestroyTexture
            // DXVK interoperability
            public IntPtr dxvk_CreateD3D9;       // PFN_remixapi_dxvk_CreateD3D9
            public IntPtr dxvk_RegisterD3D9Device;
            public IntPtr dxvk_GetExternalSwapchain;
            public IntPtr dxvk_GetVkImage;
            public IntPtr dxvk_CopyRenderingOutput;
            public IntPtr dxvk_SetDefaultOutput;
            public IntPtr dxvk_GetTextureHash;
            // Object picking utils
            public IntPtr pick_RequestObjectPicking;
            public IntPtr pick_HighlightObjects;
            // Core functions
            public IntPtr Startup;               // PFN_remixapi_Startup
            public IntPtr Present;               // PFN_remixapi_Present
            public IntPtr GetUIState;
            public IntPtr SetUIState;
            // Optional extension functions (v0.5.1+)
            public IntPtr RegisterCallbacks;
            public IntPtr AutoInstancePersistentLights;
            public IntPtr UpdateLightDefinition;
        }

        #endregion

        #region DLL Loading

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr hModule);

        private static IntPtr _remixDll = IntPtr.Zero;

        /// <summary>
        /// Load Remix DLL and initialize the API interface.
        /// This mirrors remixapi_lib_loadRemixDllAndInitialize from remix_c.h
        /// </summary>
        public static remixapi_ErrorCode InitializeRemixAPI(string dllPath, out remixapi_Interface remixInterface, out IntPtr remixDll)
        {
            remixInterface = new remixapi_Interface();
            remixDll = IntPtr.Zero;

            if (string.IsNullOrEmpty(dllPath))
            {
                return remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS;
            }

            // Load the Remix DLL
            IntPtr hModule = LoadLibraryW(dllPath);
            if (hModule == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                return remixapi_ErrorCode.REMIXAPI_ERROR_CODE_LOAD_LIBRARY_FAILURE;
            }

            // Get the initialization function
            IntPtr initFuncPtr = GetProcAddress(hModule, "remixapi_InitializeLibrary");
            if (initFuncPtr == IntPtr.Zero)
            {
                FreeLibrary(hModule);
                return remixapi_ErrorCode.REMIXAPI_ERROR_CODE_GET_PROC_ADDRESS_FAILURE;
            }

            // Convert to delegate
            var initFunc = Marshal.GetDelegateForFunctionPointer<PFN_remixapi_InitializeLibrary>(initFuncPtr);

            // Create initialization info with version
            var info = new remixapi_InitializeLibraryInfo
            {
                sType = remixapi_StructType.REMIXAPI_STRUCT_TYPE_INITIALIZE_LIBRARY_INFO,
                pNext = IntPtr.Zero,
                version = REMIXAPI_VERSION_MAKE(REMIXAPI_VERSION_MAJOR, REMIXAPI_VERSION_MINOR, REMIXAPI_VERSION_PATCH)
            };

            // Call initialization
            var result = initFunc(ref info, out remixInterface);
            
            if (result != remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                FreeLibrary(hModule);
                return result;
            }

            remixDll = hModule;
            _remixDll = hModule;
            return remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS;
        }

        /// <summary>
        /// Shutdown and unload the Remix DLL.
        /// This mirrors remixapi_lib_shutdownAndUnloadRemixDll from remix_c.h
        /// </summary>
        public static remixapi_ErrorCode ShutdownAndUnloadRemixDll(ref remixapi_Interface remixInterface, IntPtr remixDll)
        {
            if (remixInterface.Shutdown == IntPtr.Zero)
            {
                if (remixDll != IntPtr.Zero)
                {
                    FreeLibrary(remixDll);
                }
                return remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS;
            }

            var shutdownFunc = Marshal.GetDelegateForFunctionPointer<PFN_remixapi_Shutdown>(remixInterface.Shutdown);
            var status = shutdownFunc();

            if (remixDll != IntPtr.Zero)
            {
                FreeLibrary(remixDll);
            }

            remixInterface = new remixapi_Interface();
            _remixDll = IntPtr.Zero;
            return status;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Create a vertex with position, normal, texcoord and color
        /// </summary>
        public static remixapi_HardcodedVertex MakeVertex(float x, float y, float z, 
            float nx = 0, float ny = 0, float nz = -1,
            float u = 0, float v = 0, uint color = 0xFFFFFFFF)
        {
            return new remixapi_HardcodedVertex
            {
                position_x = x, position_y = y, position_z = z,
                normal_x = nx, normal_y = ny, normal_z = nz,
                texcoord_x = u, texcoord_y = v,
                color = color,
                _pad0 = 0, _pad1 = 0, _pad2 = 0, _pad3 = 0, _pad4 = 0, _pad5 = 0, _pad6 = 0
            };
        }

        #endregion
    }
}
