using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using QuickNotes.Desktop.Startup;

namespace QuickNotes.Desktop.Wpf;

public sealed class WindowsRunRegistry : IRunRegistry
{
    private static readonly nint HkeyCurrentUser = unchecked((nint)(int)0x80000001);
    private const int KeyRead = 0x20019;
    private const int KeySetValue = 0x0002;
    private const int RegSz = 1;
    private const int ErrorFileNotFound = 2;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string valueName)
    {
        var openResult = RegOpenKeyEx(HkeyCurrentUser, RunKey, 0, KeyRead, out var key);
        if (openResult == ErrorFileNotFound)
        {
            return null;
        }

        ThrowIfError(openResult);
        try
        {
            var size = 0;
            var queryResult = RegQueryValueEx(key, valueName, 0, out var type, null, ref size);
            if (queryResult == ErrorFileNotFound)
            {
                return null;
            }

            ThrowIfError(queryResult);
            if (type != RegSz || size <= 0)
            {
                return null;
            }

            var data = new byte[size];
            ThrowIfError(RegQueryValueEx(key, valueName, 0, out type, data, ref size));
            return Encoding.Unicode.GetString(data).TrimEnd('\0');
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    public void Write(string valueName, string value)
    {
        ThrowIfError(RegCreateKeyEx(HkeyCurrentUser, RunKey, 0, null, 0, KeySetValue, 0, out var key, out _));
        try
        {
            var data = Encoding.Unicode.GetBytes(value + "\0");
            ThrowIfError(RegSetValueEx(key, valueName, 0, RegSz, data, data.Length));
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    public void Delete(string valueName)
    {
        var openResult = RegOpenKeyEx(HkeyCurrentUser, RunKey, 0, KeySetValue, out var key);
        if (openResult == ErrorFileNotFound)
        {
            return;
        }

        ThrowIfError(openResult);
        try
        {
            var result = RegDeleteValue(key, valueName);
            if (result != ErrorFileNotFound)
            {
                ThrowIfError(result);
            }
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    private static void ThrowIfError(int error)
    {
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyEx(
        nint hKey,
        string subKey,
        int ulOptions,
        int samDesired,
        out nint phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegCreateKeyEx(
        nint hKey,
        string subKey,
        int reserved,
        string? className,
        int options,
        int samDesired,
        nint securityAttributes,
        out nint phkResult,
        out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueEx(
        nint hKey,
        string valueName,
        nint reserved,
        out int type,
        byte[]? data,
        ref int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueEx(
        nint hKey,
        string valueName,
        int reserved,
        int type,
        byte[] data,
        int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteValue(nint hKey, string valueName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(nint hKey);
}
