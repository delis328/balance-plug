using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BalancePlug;

internal static class DesktopNative
{
    private const int ExtendedStyle = -20;
    private const long Transparent = 0x20, ToolWindow = 0x80, NoActivate = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    internal static void SetClickThrough(IntPtr handle, bool enabled)
    {
        var style = GetWindowLongPtr(handle, ExtendedStyle).ToInt64() | ToolWindow | NoActivate;
        style = enabled ? style | Transparent : style & ~Transparent;
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(handle, ExtendedStyle, new IntPtr(style));
        if (previous == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) throw new Win32Exception();
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }

    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static bool IsAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
        return key?.GetValue("BalancePlug") is string;
    }
    internal static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException();
            if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(executable), "BalancePlug", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请使用发布目录中的 BalancePlug.exe 开启自启");
            key.SetValue("BalancePlug", $"\"{executable}\"");
        }
        else key.DeleteValue("BalancePlug", throwOnMissingValue: false);
    }
}

