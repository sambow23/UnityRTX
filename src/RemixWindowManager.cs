using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Manages Win32 window creation and D3D9 device initialization for Remix
    /// </summary>
    public class RemixWindowManager
    {
        private readonly ManualLogSource logger;
        private RemixAPI.PFN_remixapi_dxvk_CreateD3D9 createD3D9Func;
        private RemixAPI.PFN_remixapi_Startup startupFunc;
        
        private IntPtr remixWindow = IntPtr.Zero;
        private int windowWidth = 1920;
        private int windowHeight = 1080;
        
        // Window management delegates and state
        private static WndProcDelegate wndProcDelegate;
        private static bool windowClassRegistered = false;
        private const string WINDOW_CLASS_NAME = "RemixWindowClass";
        
        public IntPtr RemixWindow => remixWindow;
        public int WindowWidth => windowWidth;
        public int WindowHeight => windowHeight;
        
        #region Win32 API Declarations
        
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
        
        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);
        
        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
        
        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
        
        [DllImport("user32.dll")]
        private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);
        
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
        
        [DllImport("user32.dll")]
        private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
        
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
        
        // Constants
        private const int IDC_ARROW = 32512;
        private const uint CS_HREDRAW = 0x0002;
        private const uint CS_VREDRAW = 0x0001;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_APPWINDOW = 0x00040000;
        private const int GWL_EXSTYLE = -20;
        private const int SW_SHOW = 5;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint PM_REMOVE = 0x0001;
        private const uint QS_ALLINPUT = 0x04FF;
        private const uint MWMO_INPUTAVAILABLE = 0x0004;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_TIMEOUT = 0x00000102;
        
        #endregion
        
        public RemixWindowManager(ManualLogSource logger, RemixAPI.remixapi_Interface remixInterface)
        {
            this.logger = logger;
            
            // Cache delegates
            if (remixInterface.dxvk_CreateD3D9 != IntPtr.Zero)
            {
                createD3D9Func = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_dxvk_CreateD3D9>(
                    remixInterface.dxvk_CreateD3D9);
            }
            
            if (remixInterface.Startup != IntPtr.Zero)
            {
                startupFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Startup>(
                    remixInterface.Startup);
            }
        }
        
        /// <summary>
        /// Store window dimensions for later creation
        /// </summary>
        public void SetWindowDimensions(int width, int height)
        {
            windowWidth = width > 0 ? width : 1920;
            windowHeight = height > 0 ? height : 1080;
            logger.LogInfo($"Window dimensions set to {windowWidth}x{windowHeight}");
        }
        
        /// <summary>
        /// Window procedure callback
        /// </summary>
        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_PAINT:
                    // CRITICAL: Must handle WM_PAINT or Windows marks window as frozen
                    PAINTSTRUCT ps;
                    BeginPaint(hWnd, out ps);
                    EndPaint(hWnd, ref ps);
                    return IntPtr.Zero;
                
                case WM_ERASEBKGND:
                    return new IntPtr(1);
                    
                case WM_NCHITTEST:
                    IntPtr result = DefWindowProcW(hWnd, msg, wParam, lParam);
                    return result;
            }
            
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
        
        /// <summary>
        /// Create Remix window on render thread
        /// </summary>
        public bool CreateRemixWindow()
        {
            logger.LogInfo($"Creating Remix window ({windowWidth}x{windowHeight}) on render thread...");
            
            IntPtr hInstance = GetModuleHandleW(null);
            
            // Register window class if needed
            if (!windowClassRegistered)
            {
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
                    logger.LogError($"Failed to register window class, error: {error}");
                    return false;
                }
                
                windowClassRegistered = true;
                logger.LogInfo("Window class registered successfully");
            }
            
            // Create window
            string windowTitle = $"{Application.productName} - RTX Remix - {BuildInfo.GitHash}";
            remixWindow = CreateWindowExW(
                WS_EX_APPWINDOW,
                WINDOW_CLASS_NAME,
                windowTitle,
                WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                100, 100, windowWidth, windowHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero
            );
            
            if (remixWindow == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                logger.LogError($"Failed to create Remix window, error: {error}");
                return false;
            }
            
            logger.LogInfo($"Remix window created: {remixWindow}");
            ShowWindow(remixWindow, SW_SHOW);
            
            // Call Remix Startup
            logger.LogInfo("Calling Remix Startup...");
            var startupInfo = new RemixAPI.remixapi_StartupInfo
            {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_STARTUP_INFO,
                pNext = IntPtr.Zero,
                hwnd = remixWindow,
                disableSrgbConversionForOutput = 0,
                forceNoVkSwapchain = 0,
                editorModeEnabled = 0
            };
            
            var result = startupFunc(ref startupInfo);
            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                logger.LogError($"Remix Startup failed: {result}");
                DestroyWindow(remixWindow);
                remixWindow = IntPtr.Zero;
                return false;
            }
            
            logger.LogInfo("Remix Startup succeeded!");
            return true;
        }
        
        /// <summary>
        /// Pump Windows messages to keep window responsive
        /// </summary>
        public void PumpWindowsMessages()
        {
            MSG msg;
            while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        
        /// <summary>
        /// Wait for messages with timeout (for frame rate limiting)
        /// </summary>
        public bool WaitForMessages(uint milliseconds)
        {
            uint result = MsgWaitForMultipleObjectsEx(0, null, milliseconds, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            return result == WAIT_OBJECT_0; // Returns true if messages are available
        }
        
        /// <summary>
        /// Destroy Remix window
        /// </summary>
        public void DestroyRemixWindow()
        {
            if (remixWindow != IntPtr.Zero)
            {
                DestroyWindow(remixWindow);
                remixWindow = IntPtr.Zero;
                logger.LogInfo("Remix window destroyed");
            }
        }
    }
}
