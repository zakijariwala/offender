using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Offender.Native;

/// <summary>
/// Just enough COM to write a .lnk file.
///
/// Windows has blocked programmatic taskbar pinning since Windows 8, so the honest way to
/// make an app pinnable is to put a shortcut in the Start Menu and let the user pin it.
///
/// Both vtables are declared in full, including the methods we never call: COM dispatches
/// by slot index, so omitting an entry would silently shift every method after it onto the
/// wrong implementation. Signatures use raw pointers to keep the generated marshalling
/// trivial and NativeAOT-safe.
/// </summary>
[GeneratedComInterface]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal unsafe partial interface IShellLinkW
{
    [PreserveSig] int GetPath(char* pszFile, int cch, void* pfd, uint fFlags);
    [PreserveSig] int GetIDList(void** ppidl);
    [PreserveSig] int SetIDList(void* pidl);
    [PreserveSig] int GetDescription(char* pszName, int cch);
    [PreserveSig] int SetDescription(char* pszName);
    [PreserveSig] int GetWorkingDirectory(char* pszDir, int cch);
    [PreserveSig] int SetWorkingDirectory(char* pszDir);
    [PreserveSig] int GetArguments(char* pszArgs, int cch);
    [PreserveSig] int SetArguments(char* pszArgs);
    [PreserveSig] int GetHotkey(ushort* pwHotkey);
    [PreserveSig] int SetHotkey(ushort wHotkey);
    [PreserveSig] int GetShowCmd(int* piShowCmd);
    [PreserveSig] int SetShowCmd(int iShowCmd);
    [PreserveSig] int GetIconLocation(char* pszIconPath, int cch, int* piIcon);
    [PreserveSig] int SetIconLocation(char* pszIconPath, int iIcon);
    [PreserveSig] int SetRelativePath(char* pszPathRel, uint dwReserved);
    [PreserveSig] int Resolve(nint hwnd, uint fFlags);
    [PreserveSig] int SetPath(char* pszFile);
}

[GeneratedComInterface]
[Guid("0000010b-0000-0000-C000-000000000046")]
internal unsafe partial interface IPersistFile
{
    [PreserveSig] int GetClassID(Guid* pClassID);
    [PreserveSig] int IsDirty();
    [PreserveSig] int Load(char* pszFileName, uint dwMode);
    [PreserveSig] int Save(char* pszFileName, int fRemember);
    [PreserveSig] int SaveCompleted(char* pszFileName);
    [PreserveSig] int GetCurFile(char** ppszFileName);
}

internal static unsafe partial class ShellLink
{
    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint COINIT_APARTMENTTHREADED = 2;

    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");

    private static readonly StrategyBasedComWrappers Wrappers = new();

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* rclsid, nint pUnkOuter, uint dwClsContext, Guid* riid, nint* ppv);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    /// <summary>
    /// Writes a .lnk pointing at <paramref name="targetPath"/>. Returns false on any
    /// failure -- this is a convenience action, never worth taking the process down for.
    /// </summary>
    public static bool Create(string linkPath, string targetPath, string description)
    {
        // The process may already be in an apartment; S_FALSE and RPC_E_CHANGED_MODE both
        // mean "already initialised", which is fine for our purposes.
        CoInitializeEx(0, COINIT_APARTMENTTHREADED);

        nint punk = 0;
        try
        {
            Guid clsid = CLSID_ShellLink;
            Guid iid = IID_IShellLinkW;
            if (CoCreateInstance(&clsid, 0, CLSCTX_INPROC_SERVER, &iid, &punk) != 0 || punk == 0)
                return false;

            var link = (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(punk, CreateObjectFlags.None);

            string? workingDir = Path.GetDirectoryName(targetPath);

            fixed (char* target = targetPath)
            fixed (char* desc = description)
            fixed (char* dir = workingDir ?? "")
            fixed (char* icon = targetPath)
            {
                if (link.SetPath(target) != 0) return false;
                link.SetDescription(desc);
                if (workingDir is not null) link.SetWorkingDirectory(dir);
                link.SetIconLocation(icon, 0);
            }

            // QueryInterface for the persistence side of the same object.
            var persist = (IPersistFile)link;
            fixed (char* path = linkPath)
                return persist.Save(path, 1) == 0;
        }
        catch (COMException) { return false; }
        catch (InvalidCastException) { return false; }
        finally
        {
            if (punk != 0) Marshal.Release(punk);
        }
    }
}
