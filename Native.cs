using System;
using System.Runtime.InteropServices;

namespace VoiceChannelGrabber
{
    // Source: https://stackoverflow.com/questions/13656846/how-to-programmatic-disable-c-sharp-console-applications-quick-edit-mode
    public static class Native
    {
        private enum StdHandle : int
        {
            STD_INPUT_HANDLE = -10,
            STD_OUTPUT_HANDLE = -11,
            STD_ERROR_HANDLE = -12,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        private enum ConsoleMode : uint
        {
            ENABLE_ECHO_INPUT = 0x0004,
            ENABLE_EXTENDED_FLAGS = 0x0080,
            ENABLE_INSERT_MODE = 0x0020,
            ENABLE_LINE_INPUT = 0x0002,
            ENABLE_MOUSE_INPUT = 0x0010,
            ENABLE_PROCESSED_INPUT = 0x0001,
            ENABLE_QUICK_EDIT_MODE = 0x0040,
            ENABLE_WINDOW_INPUT = 0x0008,
            ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200,

            //screen buffer handle
            ENABLE_PROCESSED_OUTPUT = 0x0001,
            ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002,
            ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004,
            DISABLE_NEWLINE_AUTO_RETURN = 0x0008,
            ENABLE_LVB_GRID_WORLDWIDE = 0x0010
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        public static void SetQuickEditMode(bool Enable)
        {
            IntPtr consoleHandle = GetStdHandle((int)StdHandle.STD_INPUT_HANDLE);
            UInt32 consoleMode;

            GetConsoleMode(consoleHandle, out consoleMode);
            if (Enable)
                consoleMode |= ((uint)ConsoleMode.ENABLE_QUICK_EDIT_MODE);
            else
                consoleMode &= ~((uint)ConsoleMode.ENABLE_QUICK_EDIT_MODE);

            consoleMode |= ((uint)ConsoleMode.ENABLE_EXTENDED_FLAGS);

            SetConsoleMode(consoleHandle, consoleMode);
        }
    }
}
