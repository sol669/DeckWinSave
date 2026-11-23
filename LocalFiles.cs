using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GameSavePreview;

internal static class LocalFiles
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        internal int Size;
        internal nint Owner, Instance;
        internal string Filter;
        internal nint CustomFilter;
        internal int MaxCustomFilter, FilterIndex;
        internal nint File;
        internal int MaxFile;
        internal nint FileTitle;
        internal int MaxFileTitle;
        internal string? InitialDir, Title;
        internal uint Flags;
        internal ushort FileOffset, FileExtension;
        internal string? DefaultExtension;
        internal nint CustomData, Hook, Template, Reserved;
        internal int Reserved2, FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName options);

    internal static string[] Pick(nint hwnd, string? directory, bool executable)
    {
        const int capacity = 65536;
        var buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            var empty = new byte[capacity * sizeof(char)];
            Marshal.Copy(empty, 0, buffer, empty.Length);
            var settings = new OpenFileName
            {
                Size = Marshal.SizeOf<OpenFileName>(), Owner = hwnd, File = buffer, MaxFile = capacity,
                InitialDir = directory, Filter = executable ? "EXE\0*.exe\0\0" : "Files\0*.*\0\0",
                Flags = 0x00080000 | 0x1000 | 0x800 | 0x8 | (executable ? 0u : 0x200u)
            };
            if (!GetOpenFileName(ref settings)) return [];
            var paths = new List<string>();
            for (int offset = 0; offset < capacity;)
            {
                var part = Marshal.PtrToStringUni(buffer + offset * sizeof(char)) ?? "";
                if (part.Length == 0) break;
                paths.Add(part);
                offset += part.Length + 1;
            }
            return paths.Count < 2 ? paths.ToArray() : paths.Skip(1).Select(file => Path.Combine(paths[0], file)).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static string RelativeSave(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        SaveFolder.Validate(relative);
        if (Transfers.Ignored(relative)) throw new IOException("Файлы .bak исключаются автоматически.");
        return relative;
    }

    internal static void RemoveLegacyAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("DeckWinSave", throwOnMissingValue: false);
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        catch (IOException) { }
    }

    internal static string GuidePath(bool english) =>
        Path.Combine(AppContext.BaseDirectory, "Docs", english ? "Connection-EN.txt" : "Connection-RU.txt");
}
