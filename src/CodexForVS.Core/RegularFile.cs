using System;
using System.ComponentModel;

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexForVS.Core;

// Verify the opened handle as well as the path. A junction substituted between the
// path check and File.OpenRead must not turn repository inspection into a different read.
internal static class RegularFile
{
    internal static FileStream OpenRead(string path)
    {
        FileStream stream = File.OpenRead(path);
        try
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle,out FileInfo info))
                throw new IOException("Could not verify file identity.",new Win32Exception(Marshal.GetLastWin32Error()));
            if (info.Links != 1 || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked files require manual handling: " + path);
            VerifyPath(stream.SafeFileHandle,path);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    internal static SafeFileHandle PinDirectory(string path)
    {
        const uint backupSemantics = 0x02000000, openReparsePoint = 0x00200000, readAttributes = 0x80;
        SafeFileHandle handle = CreateFile(path,readAttributes,FileShare.Read | FileShare.Write,IntPtr.Zero,FileMode.Open,backupSemantics | openReparsePoint,IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle,out FileInfo info) ||
                (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != (uint)FileAttributes.Directory)
                throw new IOException("Cannot secure the target directory: " + path);
            VerifyPath(handle,path);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void VerifyPath(SafeFileHandle handle,string path)
    {
        StringBuilder location = new StringBuilder(512);
        uint length = GetFinalPathNameByHandle(handle,location,(uint)location.Capacity,0);
        if (length >= location.Capacity && length <= 32768)
        {
            location.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandle(handle,location,(uint)location.Capacity,0);
        }
        if (length == 0 || length >= location.Capacity || !string.Equals(Normalize(location.ToString()),Normalize(Path.GetFullPath(path)),StringComparison.OrdinalIgnoreCase))
            throw new IOException("The opened path resolves to a different location: " + path);
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path,uint access,FileShare sharing,IntPtr security,FileMode creation,uint flags,IntPtr template);

    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
        return path.StartsWith(@"\\?\",StringComparison.Ordinal) ? path.Substring(4) : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;
        internal uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
    }

    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfo info);

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder buffer,uint capacity,uint flags);
}
