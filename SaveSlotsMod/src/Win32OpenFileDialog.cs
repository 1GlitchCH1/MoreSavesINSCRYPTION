using System;
using System.Runtime.InteropServices;

namespace SaveSlotsMod
{
    /// <summary>
    /// Диалог выбора файла через Win32 API (comdlg32.dll).
    /// Не требует сборки System.Windows.Forms — работает через P/Invoke.
    /// </summary>
    internal static class Win32OpenFileDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAMEW
        {
            public int    lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int    nMaxCustFilter;
            public int    nFilterIndex;
            public string lpstrFile;
            public int    nMaxFile;
            public IntPtr lpstrFileTitle;
            public int    nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int    Flags;
            public short  nFileOffset;
            public short  nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr pvReserved;
            public int    dwReserved;
            public int    FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

        private const int OFN_FILEMUSTEXIST    = 0x00001000;
        private const int OFN_PATHMUSTEXIST    = 0x00000800;
        private const int OFN_EXPLORER         = 0x00080000;
        private const int OFN_LONGNAMES         = 0x00200000;
        private const int OFN_NODEREFERENCELINKS = 0x00100000;

        /// <summary>
        /// Показывает диалог выбора файла. Возвращает путь или null, если отменено.
        /// </summary>
        public static string? Show(string title, string filter)
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize      = Marshal.SizeOf(typeof(OPENFILENAMEW)),
                hwndOwner        = IntPtr.Zero,
                hInstance        = IntPtr.Zero,
                lpstrFilter      = filter,
                lpstrCustomFilter = IntPtr.Zero,
                nMaxCustFilter   = 0,
                nFilterIndex     = 1,
                lpstrFile        = new string('\0', 260),
                nMaxFile         = 260,
                lpstrFileTitle   = IntPtr.Zero,
                nMaxFileTitle    = 0,
                lpstrInitialDir  = null!,
                lpstrTitle       = title,
                Flags            = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST |
                                   OFN_EXPLORER | OFN_LONGNAMES | OFN_NODEREFERENCELINKS,
                lpstrDefExt      = null!,
                lCustData        = IntPtr.Zero,
                lpfnHook         = IntPtr.Zero,
                lpTemplateName   = IntPtr.Zero,
                pvReserved       = IntPtr.Zero,
                dwReserved       = 0,
                FlagsEx          = 0
            };

            if (GetOpenFileNameW(ref ofn))
            {
                int idx = ofn.lpstrFile.IndexOf('\0');
                string result = idx >= 0 ? ofn.lpstrFile.Substring(0, idx) : ofn.lpstrFile;
                return string.IsNullOrEmpty(result) ? null : result;
            }
            return null;
        }
    }
}
