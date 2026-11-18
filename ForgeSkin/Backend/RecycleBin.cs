using System.Runtime.InteropServices;

namespace ForgeSkin.Backend;

// Files the player may want back (an installed skin taken out of a profile) go to the Windows Recycle Bin, never a permanent
// delete. No dialog: the backend has no window of its own, so a failure throws instead.
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyAborted;
        public IntPtr NameMappings;
        public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref FileOperation operation);

    const uint Delete = 3;
    const ushort AllowUndo = 0x40, NoConfirmation = 0x10, Silent = 0x4, NoErrorUi = 0x400;

    // A file or a folder with all it holds.
    public static void Send(string file)
    {
        bool There() => File.Exists(file) || Directory.Exists(file);
        if (!There()) return;
        var operation = new FileOperation { Function = Delete, From = Path.GetFullPath(file) + "\0\0", Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi };
        int code = SHFileOperationW(ref operation);
        if (code != 0 || operation.AnyAborted || There())
            throw new IOException($"{file} could not be moved to the Recycle Bin (shell error {code})");
    }
}
