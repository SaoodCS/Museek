using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

// IShellLinkW preserves Unicode paths; WScript.Shell's TargetPath getter can lose them.
public static class MuseekInstallerShellLink
{
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path,
            int characterCount, IntPtr findData, uint flags);
    }

    public static string GetTarget(string shortcutPath)
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("00021401-0000-0000-C000-000000000046"), true));
        try
        {
            ((IPersistFile)instance).Load(shortcutPath, 0);
            var target = new StringBuilder(32768);
            Marshal.ThrowExceptionForHR(((IShellLinkW)instance).GetPath(target, target.Capacity,
                IntPtr.Zero, 4)); // SLGP_RAWPATH: read only; do not resolve or launch the target.
            return target.ToString();
        }
        finally { if (instance != null) Marshal.FinalReleaseComObject(instance); }
    }
}
