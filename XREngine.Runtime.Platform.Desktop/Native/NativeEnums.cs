namespace XREngine.Native
{
    public enum EGetWindow_Cmd : uint
    {
        GW_HWNDFIRST = 0,
        GW_HWNDLAST = 1,
        GW_HWNDNEXT = 2,
        GW_HWNDPREV = 3,
        GW_OWNER = 4,
        GW_CHILD = 5,
        GW_ENABLEDPOPUP = 6
    }
    public enum EVirtualKey
    {
        VK_LBUTTON = 0x01,
        //Left mouse button

        VK_RBUTTON = 0x02,
        //Right mouse button

        VK_CANCEL = 0x03,
        //Control-break processing

        VK_MBUTTON = 0x04,
        //Middle mouse button (three-button mouse)

        VK_XBUTTON1 = 0x05,
        //X1 mouse button

        VK_XBUTTON2 = 0x06,
        //X2 mouse button

        NONE1 = 0x07,
        //Undefined

        VK_BACK = 0x08,
        //BACKSPACE key

        //VK_TAB
        //0x09
        //TAB key

        //-
        //0x0A-0B
        //Reserved

        //VK_CLEAR
        //0x0C
        //CLEAR key

        //VK_RETURN
        //0x0D
        //ENTER key

        //-
        //0x0E-0F
        //Undefined

        //VK_SHIFT
        //0x10
        //SHIFT key

        //VK_CONTROL
        //0x11
        //CTRL key

        //VK_MENU
        //0x12
        //ALT key

        //VK_PAUSE
        //0x13
        //PAUSE key

        //VK_CAPITAL
        //0x14
        //CAPS LOCK key

        //VK_KANA
        //0x15
        //IME Kana mode

        //VK_HANGUEL
        //0x15
        //IME Hanguel mode (maintained for compatibility; use VK_HANGUL)

        //VK_HANGUL
        //0x15
        //IME Hangul mode

        //-
        //0x16
        //Undefined

        //VK_JUNJA
        //0x17
        //IME Junja mode

        //VK_FINAL
        //0x18
        //IME final mode

        //VK_HANJA
        //0x19
        //IME Hanja mode

        //VK_KANJI
        //0x19
        //IME Kanji mode

        //-
        //0x1A
        //Undefined

        //VK_ESCAPE
        //0x1B
        //ESC key

        //VK_CONVERT
        //0x1C
        //IME convert

        //VK_NONCONVERT
        //0x1D
        //IME nonconvert

        //VK_ACCEPT
        //0x1E
        //IME accept

        //VK_MODECHANGE
        //0x1F
        //IME mode change request

        //VK_SPACE
        //0x20
        //SPACEBAR

        //VK_PRIOR
        //0x21
        //PAGE UP key

        //VK_NEXT
        //0x22
        //PAGE DOWN key

        //VK_END
        //0x23
        //END key

        //VK_HOME
        //0x24
        //HOME key

        //VK_LEFT
        //0x25
        //LEFT ARROW key

        //VK_UP
        //0x26
        //UP ARROW key

        //VK_RIGHT
        //0x27
        //RIGHT ARROW key

        //VK_DOWN
        //0x28
        //DOWN ARROW key

        //VK_SELECT
        //0x29
        //SELECT key

        //VK_PRINT
        //0x2A
        //PRINT key

        //VK_EXECUTE
        //0x2B
        //EXECUTE key

        //VK_SNAPSHOT
        //0x2C
        //PRINT SCREEN key

        //VK_INSERT
        //0x2D
        //INS key

        //VK_DELETE
        //0x2E
        //DEL key

        //VK_HELP
        //0x2F
        //HELP key

        //0x30
        //0 key

        //0x31
        //1 key

        //0x32
        //2 key

        //0x33
        //3 key

        //0x34
        //4 key

        //0x35
        //5 key

        //0x36
        //6 key

        //0x37
        //7 key

        //0x38
        //8 key

        //0x39
        //9 key

        //-
        //0x3A-40
        //Undefined

        //0x41
        //A key

        //0x42
        //B key

        //0x43
        //C key

        //0x44
        //D key

        //0x45
        //E key

        //0x46
        //F key

        //0x47
        //G key

        //0x48
        //H key

        //0x49
        //I key

        //0x4A
        //J key

        //0x4B
        //K key

        //0x4C
        //L key

        //0x4D
        //M key

        //0x4E
        //N key

        //0x4F
        //O key

        //0x50
        //P key

        //0x51
        //Q key

        //0x52
        //R key

        //0x53
        //S key

        //0x54
        //T key

        //0x55
        //U key

        //0x56
        //V key

        //0x57
        //W key

        //0x58
        //X key

        //0x59
        //Y key

        //0x5A
        //Z key

        //VK_LWIN
        //0x5B
        //Left Windows key (Natural keyboard)

        //VK_RWIN
        //0x5C
        //Right Windows key (Natural keyboard)

        //VK_APPS
        //0x5D
        //Applications key (Natural keyboard)

        //-
        //0x5E
        //Reserved

        //VK_SLEEP
        //0x5F
        //Computer Sleep key

        //VK_NUMPAD0
        //0x60
        //Numeric keypad 0 key

        //VK_NUMPAD1
        //0x61
        //Numeric keypad 1 key

        //VK_NUMPAD2
        //0x62
        //Numeric keypad 2 key

        //VK_NUMPAD3
        //0x63
        //Numeric keypad 3 key

        //VK_NUMPAD4
        //0x64
        //Numeric keypad 4 key

        //VK_NUMPAD5
        //0x65
        //Numeric keypad 5 key

        //VK_NUMPAD6
        //0x66
        //Numeric keypad 6 key

        //VK_NUMPAD7
        //0x67
        //Numeric keypad 7 key

        //VK_NUMPAD8
        //0x68
        //Numeric keypad 8 key

        //VK_NUMPAD9
        //0x69
        //Numeric keypad 9 key

        //VK_MULTIPLY
        //0x6A
        //Multiply key

        //VK_ADD
        //0x6B
        //Add key

        //VK_SEPARATOR
        //0x6C
        //Separator key

        //VK_SUBTRACT
        //0x6D
        //Subtract key

        //VK_DECIMAL
        //0x6E
        //Decimal key

        //VK_DIVIDE
        //0x6F
        //Divide key

        //VK_F1
        //0x70
        //F1 key

        //VK_F2
        //0x71
        //F2 key

        //VK_F3
        //0x72
        //F3 key

        //VK_F4
        //0x73
        //F4 key

        //VK_F5
        //0x74
        //F5 key

        //VK_F6
        //0x75
        //F6 key

        //VK_F7
        //0x76
        //F7 key

        //VK_F8
        //0x77
        //F8 key

        //VK_F9
        //0x78
        //F9 key

        //VK_F10
        //0x79
        //F10 key

        //VK_F11
        //0x7A
        //F11 key

        //VK_F12
        //0x7B
        //F12 key

        //VK_F13
        //0x7C
        //F13 key

        //VK_F14
        //0x7D
        //F14 key

        //VK_F15
        //0x7E
        //F15 key

        //VK_F16
        //0x7F
        //F16 key

        //VK_F17
        //0x80
        //F17 key

        //VK_F18
        //0x81
        //F18 key

        //VK_F19
        //0x82
        //F19 key

        //VK_F20
        //0x83
        //F20 key

        //VK_F21
        //0x84
        //F21 key

        //VK_F22
        //0x85
        //F22 key

        //VK_F23
        //0x86
        //F23 key

        //VK_F24
        //0x87
        //F24 key

        //-
        //0x88-8F
        //Unassigned

        //VK_NUMLOCK
        //0x90
        //NUM LOCK key

        //VK_SCROLL
        //0x91
        //SCROLL LOCK key

        //0x92-96
        //OEM specific

        //-
        //0x97-9F
        //Unassigned

        //VK_LSHIFT
        //0xA0
        //Left SHIFT key

        //VK_RSHIFT
        //0xA1
        //Right SHIFT key

        //VK_LCONTROL
        //0xA2
        //Left CONTROL key

        //VK_RCONTROL
        //0xA3
        //Right CONTROL key

        //VK_LMENU
        //0xA4
        //Left MENU key

        //VK_RMENU
        //0xA5
        //Right MENU key

        //VK_BROWSER_BACK
        //0xA6
        //Browser Back key

        //VK_BROWSER_FORWARD
        //0xA7
        //Browser Forward key

        //VK_BROWSER_REFRESH
        //0xA8
        //Browser Refresh key

        //VK_BROWSER_STOP
        //0xA9
        //Browser Stop key

        //VK_BROWSER_SEARCH
        //0xAA
        //Browser Search key

        //VK_BROWSER_FAVORITES
        //0xAB
        //Browser Favorites key

        //VK_BROWSER_HOME
        //0xAC
        //Browser Start and Home key

        //VK_VOLUME_MUTE
        //0xAD
        //Volume Mute key

        //VK_VOLUME_DOWN
        //0xAE
        //Volume Down key

        //VK_VOLUME_UP
        //0xAF
        //Volume Up key

        //VK_MEDIA_NEXT_TRACK
        //0xB0
        //Next Track key

        //VK_MEDIA_PREV_TRACK
        //0xB1
        //Previous Track key

        //VK_MEDIA_STOP
        //0xB2
        //Stop Media key

        //VK_MEDIA_PLAY_PAUSE
        //0xB3
        //Play/Pause Media key

        //VK_LAUNCH_MAIL
        //0xB4
        //Start Mail key

        //VK_LAUNCH_MEDIA_SELECT
        //0xB5
        //Select Media key

        //VK_LAUNCH_APP1
        //0xB6
        //Start Application 1 key

        //VK_LAUNCH_APP2
        //0xB7
        //Start Application 2 key

        //-
        //0xB8-B9
        //Reserved

        //VK_OEM_1
        //0xBA
        //Used for miscellaneous characters; it can vary by keyboard.

        //For the US standard keyboard, the ';:' key

        //VK_OEM_PLUS
        //0xBB
        //For any country/region, the '+' key

        //VK_OEM_COMMA
        //0xBC
        //For any country/region, the ',' key

        //VK_OEM_MINUS
        //0xBD
        //For any country/region, the '-' key

        //VK_OEM_PERIOD
        //0xBE
        //For any country/region, the '.' key

        //VK_OEM_2
        //0xBF
        //Used for miscellaneous characters; it can vary by keyboard.

        //For the US standard keyboard, the '/?' key

        //VK_OEM_3
        //0xC0
        //Used for miscellaneous characters; it can vary by keyboard.

        //For the US standard keyboard, the '`~' key

        //-
        //0xC1-D7
        //Reserved

        //-
        //0xD8-DA
        //Unassigned

        //VK_OEM_4
        //0xDB
        //Used for miscellaneous characters; it can vary by keyboard.

        //For the US standard keyboard, the '[{' key

        //VK_OEM_5
        //0xDC
        //Used for miscellaneous characters; it can vary by keyboard.

        //For the US standard keyboard, the '\|' key

        //VK_OEM_6
        //0xDD
        //Used for miscellaneous characters; it can vary by keyboard.

        //For the US standard keyboard, the ']}' key

        //VK_OEM_7
        //0xDE
        //Used for miscellaneous characters; it can vary by keyboard.

        //For the US standard keyboard, the 'single-quote/double-quote' key

        //VK_OEM_8
        //0xDF
        //Used for miscellaneous characters; it can vary by keyboard.

        //-
        //0xE0
        //Reserved

        //0xE1
        //OEM specific

        //VK_OEM_102
        //0xE2
        //Either the angle bracket key or the backslash key on the RT 102-key keyboard

        //0xE3-E4
        //OEM specific

        //VK_PROCESSKEY
        //0xE5
        //IME PROCESS key

        //0xE6
        //OEM specific

        //VK_PACKET
        //0xE7
        //Used to pass Unicode characters as if they were keystrokes. The VK_PACKET key is the low word of a 32-bit Virtual Key value used for non-keyboard input methods. For more information, see Remark in KEYBDINPUT, SendInput, WM_KEYDOWN, and WM_KEYUP

        //-
        //0xE8
        //Unassigned

        //0xE9-F5
        //OEM specific

        //VK_ATTN
        //0xF6
        //Attn key

        //VK_CRSEL
        //0xF7
        //CrSel key

        //VK_EXSEL
        //0xF8
        //ExSel key

        //VK_EREOF
        //0xF9
        //Erase EOF key

        //VK_PLAY
        //0xFA
        //Play key

        //VK_ZOOM
        //0xFB
        //Zoom key

        //VK_NONAME
        //0xFC
        //Reserved

        //VK_PA1
        //0xFD
        //PA1 key

        //VK_OEM_CLEAR
        //0xFE
        //Clear key
    }

    public enum EHitTestValues
    {
        HTERROR = -2,
        HTTRANSPARENT = -1,
        HTNOWHERE = 0,
        HTCLIENT = 1,
        HTCAPTION = 2,
        HTSYSMENU = 3,
        HTGROWBOX = 4,
        HTMENU = 5,
        HTHSCROLL = 6,
        HTVSCROLL = 7,
        HTMINBUTTON = 8,
        HTMAXBUTTON = 9,
        HTLEFT = 10,
        HTRIGHT = 11,
        HTTOP = 12,
        HTTOPLEFT = 13,
        HTTOPRIGHT = 14,
        HTBOTTOM = 15,
        HTBOTTOMLEFT = 16,
        HTBOTTOMRIGHT = 17,
        HTBORDER = 18,
        HTOBJECT = 19,
        HTCLOSE = 20,
        HTHELP = 21
    }

    public enum EWindowsMessage
    {
        WM_NULL = 0x0000,
        WM_CREATE = 0x0001,
        WM_DESTROY = 0x0002,
        WM_MOVE = 0x0003,
        WM_SIZE = 0x0005,
        WM_ACTIVATE = 0x0006,
        WM_SETFOCUS = 0x0007,
        WM_KILLFOCUS = 0x0008,
        WM_ENABLE = 0x000A,
        WM_SETREDRAW = 0x000B,
        WM_SETTEXT = 0x000C,
        WM_GETTEXT = 0x000D,
        WM_GETTEXTLENGTH = 0x000E,
        WM_PAINT = 0x000F,
        WM_CLOSE = 0x0010,

        WM_QUIT = 0x0012,
        WM_ERASEBKGND = 0x0014,
        WM_SYSCOLORCHANGE = 0x0015,
        WM_SHOWWINDOW = 0x0018,

        WM_ACTIVATEAPP = 0x001C,

        WM_SETCURSOR = 0x0020,
        WM_MOUSEACTIVATE = 0x0021,
        WM_GETMINMAXINFO = 0x24,
        WM_WINDOWPOSCHANGING = 0x0046,
        WM_WINDOWPOSCHANGED = 0x0047,

        WM_CONTEXTMENU = 0x007B,
        WM_STYLECHANGING = 0x007C,
        WM_STYLECHANGED = 0x007D,
        WM_DISPLAYCHANGE = 0x007E,
        WM_GETICON = 0x007F,
        WM_SETICON = 0x0080,

        // non client area
        WM_NCCREATE = 0x0081,
        WM_NCDESTROY = 0x0082,
        WM_NCCALCSIZE = 0x0083,
        WM_NCHITTEST = 0x84,
        WM_NCPAINT = 0x0085,
        WM_NCACTIVATE = 0x0086,

        WM_GETDLGCODE = 0x0087,

        WM_SYNCPAINT = 0x0088,

        // non client mouse
        WM_NCMOUSEMOVE = 0x00A0,
        WM_NCLBUTTONDOWN = 0x00A1,
        WM_NCLBUTTONUP = 0x00A2,
        WM_NCLBUTTONDBLCLK = 0x00A3,
        WM_NCRBUTTONDOWN = 0x00A4,
        WM_NCRBUTTONUP = 0x00A5,
        WM_NCRBUTTONDBLCLK = 0x00A6,
        WM_NCMBUTTONDOWN = 0x00A7,
        WM_NCMBUTTONUP = 0x00A8,
        WM_NCMBUTTONDBLCLK = 0x00A9,

        // keyboard
        WM_KEYDOWN = 0x0100,
        WM_KEYUP = 0x0101,
        WM_CHAR = 0x0102,

        WM_SYSCOMMAND = 0x0112,

        // menu
        WM_INITMENU = 0x0116,
        WM_INITMENUPOPUP = 0x0117,
        WM_MENUSELECT = 0x011F,
        WM_MENUCHAR = 0x0120,
        WM_ENTERIDLE = 0x0121,
        WM_MENURBUTTONUP = 0x0122,
        WM_MENUDRAG = 0x0123,
        WM_MENUGETOBJECT = 0x0124,
        WM_UNINITMENUPOPUP = 0x0125,
        WM_MENUCOMMAND = 0x0126,

        WM_CHANGEUISTATE = 0x0127,
        WM_UPDATEUISTATE = 0x0128,
        WM_QUERYUISTATE = 0x0129,

        // mouse
        WM_MOUSEFIRST = 0x0200,
        WM_MOUSEMOVE = 0x0200,
        WM_LBUTTONDOWN = 0x0201,
        WM_LBUTTONUP = 0x0202,
        WM_LBUTTONDBLCLK = 0x0203,
        WM_RBUTTONDOWN = 0x0204,
        WM_RBUTTONUP = 0x0205,
        WM_RBUTTONDBLCLK = 0x0206,
        WM_MBUTTONDOWN = 0x0207,
        WM_MBUTTONUP = 0x0208,
        WM_MBUTTONDBLCLK = 0x0209,
        WM_MOUSEWHEEL = 0x020A,
        WM_MOUSELAST = 0x020D,

        WM_PARENTNOTIFY = 0x0210,
        WM_ENTERMENULOOP = 0x0211,
        WM_EXITMENULOOP = 0x0212,

        WM_NEXTMENU = 0x0213,
        WM_SIZING = 0x0214,
        WM_CAPTURECHANGED = 0x0215,
        WM_MOVING = 0x0216,

        WM_ENTERSIZEMOVE = 0x0231,
        WM_EXITSIZEMOVE = 0x0232,

        WM_MOUSELEAVE = 0x02A3,
        WM_MOUSEHOVER = 0x02A1,
        WM_NCMOUSEHOVER = 0x02A0,
        WM_NCMOUSELEAVE = 0x02A2,

        WM_MDIACTIVATE = 0x0222,
        WM_HSCROLL = 0x0114,
        WM_VSCROLL = 0x0115,

        WM_SYSMENU = 0x313,

        WM_PRINT = 0x0317,
        WM_PRINTCLIENT = 0x0318,
    }

    public enum ESystemCommands
    {
        SC_SIZE = 0xF000,
        SC_MOVE = 0xF010,
        SC_MINIMIZE = 0xF020,
        SC_MAXIMIZE = 0xF030,
        SC_MAXIMIZE2 = 0xF032,	// fired from double-click on caption
        SC_NEXTWINDOW = 0xF040,
        SC_PREVWINDOW = 0xF050,
        SC_CLOSE = 0xF060,
        SC_VSCROLL = 0xF070,
        SC_HSCROLL = 0xF080,
        SC_MOUSEMENU = 0xF090,
        SC_KEYMENU = 0xF100,
        SC_ARRANGE = 0xF110,
        SC_RESTORE = 0xF120,
        SC_RESTORE2 = 0xF122,	// fired from double-click on caption
        SC_TASKLIST = 0xF130,
        SC_SCREENSAVE = 0xF140,
        SC_HOTKEY = 0xF150,

        SC_DEFAULT = 0xF160,
        SC_MONITORPOWER = 0xF170,
        SC_CONTEXTHELP = 0xF180,
        SC_SEPARATOR = 0xF00F
    }

    [Flags]
    public enum EWindowStyle
    {
        WS_OVERLAPPED = 0x00000000,
        WS_POPUP = -2147483648, //0x80000000,
        WS_CHILD = 0x40000000,
        WS_MINIMIZE = 0x20000000,
        WS_VISIBLE = 0x10000000,
        WS_DISABLED = 0x08000000,
        WS_CLIPSIBLINGS = 0x04000000,
        WS_CLIPCHILDREN = 0x02000000,
        WS_MAXIMIZE = 0x01000000,
        WS_CAPTION = 0x00C00000,
        WS_BORDER = 0x00800000,
        WS_DLGFRAME = 0x00400000,
        WS_VSCROLL = 0x00200000,
        WS_HSCROLL = 0x00100000,
        WS_SYSMENU = 0x00080000,
        WS_THICKFRAME = 0x00040000,
        WS_GROUP = 0x00020000,
        WS_TABSTOP = 0x00010000,
        WS_MINIMIZEBOX = 0x00020000,
        WS_MAXIMIZEBOX = 0x00010000,
        WS_TILED = WS_OVERLAPPED,
        WS_ICONIC = WS_MINIMIZE,
        WS_SIZEBOX = WS_THICKFRAME,
        WS_TILEDWINDOW = WS_OVERLAPPEDWINDOW,
        WS_OVERLAPPEDWINDOW = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX,
        WS_POPUPWINDOW = (WS_POPUP | WS_BORDER | WS_SYSMENU),
        WS_CHILDWINDOW = (WS_CHILD)
    }

    public enum EShowWindowEnum
    {
        Hide = 0,
        ShowNormal = 1,
        ShowMinimized = 2,
        ShowMaximized = 3,
        Maximize = 3,
        ShowNormalNoActivate = 4,
        Show = 5,
        Minimize = 6,
        ShowMinNoActivate = 7,
        ShowNoActivate = 8,
        Restore = 9,
        ShowDefault = 10,
        ForceMinimized = 11
    };

    [Flags]
    public enum EAnimateWindowFlags : uint
    {
        AW_HOR_POSITIVE = 0x00000001,
        AW_HOR_NEGATIVE = 0x00000002,
        AW_VER_POSITIVE = 0x00000004,
        AW_VER_NEGATIVE = 0x00000008,
        AW_CENTER = 0x00000010,
        AW_HIDE = 0x00010000,
        AW_ACTIVATE = 0x00020000,
        AW_SLIDE = 0x00040000,
        AW_BLEND = 0x00080000
    }
}
