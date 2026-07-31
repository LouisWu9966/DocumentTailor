using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;
using WpfDataObject = System.Windows.IDataObject;

namespace DocumentTailor.Services;

public sealed class OutlookDropService : IDisposable
{
    private const string DescriptorUnicode = "FileGroupDescriptorW";
    private const string DescriptorAnsi = "FileGroupDescriptor";
    private const string FileContents = "FileContents";
    private const int FileDescriptorUnicodeSize = 592;
    private const int FileDescriptorAnsiSize = 332;
    private const int FileNameOffset = 72;
    private const int MaxFileNameCharacters = 260;
    private const int StorageCreateMode = 0x00001012;

    private readonly string _temporaryRoot;
    private readonly string _sessionDirectory;
    private bool _disposed;

    public OutlookDropService()
    {
        _temporaryRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(), "DocumentTailor", "OutlookDrop"));
        _sessionDirectory = Path.Combine(
            _temporaryRoot, $"{Environment.ProcessId}_{Guid.NewGuid():N}");
    }

    public static bool HasVirtualFiles(WpfDataObject data) =>
        data.GetDataPresent(DescriptorUnicode, false) ||
        data.GetDataPresent(DescriptorAnsi, false);

    public IReadOnlyList<string> ExtractMsgFiles(WpfDataObject data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var descriptors = ReadDescriptors(data);
        if (descriptors.Count == 0)
            throw new InvalidDataException("拖曳內容沒有可讀取的虛擬檔案。");
        if (!data.GetDataPresent(FileContents, false))
            throw new NotSupportedException(
                "目前的 Outlook 沒有提供信件檔案內容。請使用 Classic Outlook，或先將信件另存成 .msg 再匯入。");
        if (data is not ComDataObject comData)
            throw new NotSupportedException(
                "無法取得 Outlook 虛擬檔案介面。請先將信件另存成 .msg 再匯入。");

        Directory.CreateDirectory(_sessionDirectory);
        var output = new List<string>();
        for (var index = 0; index < descriptors.Count; index++)
        {
            var originalName = descriptors[index];
            if (!Path.GetExtension(originalName).Equals(".msg", StringComparison.OrdinalIgnoreCase))
                continue;

            var safeName = SafeFileName(originalName, index);
            var path = UniquePath(_sessionDirectory, safeName);
            WriteVirtualFile(comData, index, path);
            output.Add(path);
        }

        if (output.Count == 0)
            throw new NotSupportedException(
                "拖曳內容不是 Outlook .msg 信件。請拖曳郵件項目，而不是郵件中的附件。");
        return output;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var fullSession = Path.GetFullPath(_sessionDirectory);
        var rootWithSeparator = _temporaryRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (fullSession.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(fullSession))
        {
            try
            {
                Directory.Delete(fullSession, true);
            }
            catch (IOException)
            {
                // Windows or an antivirus may briefly retain a dropped MSG handle.
            }
            catch (UnauthorizedAccessException)
            {
                // Temporary files are non-critical and can be cleared by the OS later.
            }
        }
    }

    internal static IReadOnlyList<string> ReadDescriptors(WpfDataObject data)
    {
        var unicode = data.GetDataPresent(DescriptorUnicode, false);
        var format = unicode ? DescriptorUnicode : DescriptorAnsi;
        if (!data.GetDataPresent(format, false)) return [];

        var raw = data.GetData(format, false);
        var bytes = raw switch
        {
            MemoryStream stream => ReadAllBytes(stream),
            byte[] value => value,
            _ => throw new InvalidDataException("Outlook 檔案描述格式無法讀取。")
        };
        if (bytes.Length < sizeof(int))
            throw new InvalidDataException("Outlook 檔案描述資料不完整。");

        var count = BitConverter.ToUInt32(bytes, 0);
        if (count > 1000)
            throw new InvalidDataException("Outlook 拖曳項目數量異常。");
        var descriptorSize = unicode ? FileDescriptorUnicodeSize : FileDescriptorAnsiSize;
        var expectedLength = sizeof(int) + checked((int)count * descriptorSize);
        if (bytes.Length < expectedLength)
            throw new InvalidDataException("Outlook 檔案描述資料長度不足。");

        var names = new List<string>((int)count);
        for (var index = 0; index < count; index++)
        {
            var offset = sizeof(int) + index * descriptorSize + FileNameOffset;
            var name = unicode
                ? Encoding.Unicode.GetString(
                    bytes, offset, MaxFileNameCharacters * sizeof(char))
                : Encoding.Default.GetString(bytes, offset, MaxFileNameCharacters);
            var terminator = name.IndexOf('\0');
            names.Add(terminator >= 0 ? name[..terminator] : name);
        }
        return names;
    }

    private static byte[] ReadAllBytes(MemoryStream stream)
    {
        var originalPosition = stream.Position;
        stream.Position = 0;
        var bytes = stream.ToArray();
        stream.Position = Math.Min(originalPosition, stream.Length);
        return bytes;
    }

    private static string SafeFileName(string originalName, int index)
    {
        var name = Path.GetFileName(originalName);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(name) ||
            !Path.GetExtension(name).Equals(".msg", StringComparison.OrdinalIgnoreCase))
            name = $"Outlook信件_{index + 1}.msg";
        return name;
    }

    private static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            path = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(path)) return path;
        }
    }

    private static void WriteVirtualFile(ComDataObject data, int index, string outputPath)
    {
        var format = new FORMATETC
        {
            cfFormat = (short)DataFormats.GetDataFormat(FileContents).Id,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            ptd = IntPtr.Zero,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_ISTORAGE | TYMED.TYMED_HGLOBAL
        };
        data.GetData(ref format, out var medium);
        try
        {
            using var output = new FileStream(
                outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            switch (medium.tymed)
            {
                case TYMED.TYMED_ISTREAM:
                    CopyComStream(medium.unionmember, output);
                    break;
                case TYMED.TYMED_ISTORAGE:
                    CopyComStorage(medium.unionmember, output);
                    break;
                case TYMED.TYMED_HGLOBAL:
                    CopyGlobalMemory(medium.unionmember, output);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Outlook 使用了不支援的資料傳輸格式：{medium.tymed}。");
            }
        }
        catch
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static void CopyComStream(IntPtr streamPointer, Stream output)
    {
        var stream = (ComIStream)Marshal.GetObjectForIUnknown(streamPointer);
        var readPointer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            var buffer = new byte[81_920];
            while (true)
            {
                Marshal.WriteInt32(readPointer, 0);
                stream.Read(buffer, buffer.Length, readPointer);
                var read = Marshal.ReadInt32(readPointer);
                if (read <= 0) break;
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(readPointer);
            Marshal.FinalReleaseComObject(stream);
        }
    }

    private static void CopyGlobalMemory(IntPtr handle, Stream output)
    {
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
            throw new InvalidDataException("無法鎖定 Outlook 信件內容。");
        try
        {
            var remaining = GlobalSize(handle).ToInt64();
            var offset = 0L;
            var buffer = new byte[81_920];
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                Marshal.Copy(IntPtr.Add(pointer, checked((int)offset)), buffer, 0, count);
                output.Write(buffer, 0, count);
                offset += count;
                remaining -= count;
            }
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static void CopyComStorage(IntPtr storagePointer, Stream output)
    {
        var source = (IStorage)Marshal.GetObjectForIUnknown(storagePointer);
        ILockBytes? lockBytes = null;
        IStorage? destination = null;
        try
        {
            lockBytes = CreateILockBytesOnHGlobal(IntPtr.Zero, true);
            destination = StgCreateDocfileOnILockBytes(lockBytes, StorageCreateMode, 0);
            source.CopyTo(0, null, IntPtr.Zero, destination);
            destination.Commit(0);
            lockBytes.Stat(out var statistics, 1);

            var buffer = new byte[81_920];
            var position = 0L;
            while (position < statistics.cbSize)
            {
                var count = (int)Math.Min(buffer.Length, statistics.cbSize - position);
                var read = new int[1];
                lockBytes.ReadAt(position, buffer, count, read);
                if (read[0] <= 0) break;
                output.Write(buffer, 0, read[0]);
                position += read[0];
            }
        }
        finally
        {
            if (destination is not null) Marshal.FinalReleaseComObject(destination);
            if (lockBytes is not null) Marshal.FinalReleaseComObject(lockBytes);
            Marshal.FinalReleaseComObject(source);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern ILockBytes CreateILockBytesOnHGlobal(
        IntPtr globalMemory, bool deleteOnRelease);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern IStorage StgCreateDocfileOnILockBytes(
        ILockBytes lockBytes, int mode, int reserved);

    [ComImport]
    [Guid("0000000B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStorage
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        ComIStream CreateStream(
            [MarshalAs(UnmanagedType.BStr)] string name, int mode, int reserved1, int reserved2);

        [return: MarshalAs(UnmanagedType.Interface)]
        ComIStream OpenStream(
            [MarshalAs(UnmanagedType.BStr)] string name, IntPtr reserved1, int mode, int reserved2);

        [return: MarshalAs(UnmanagedType.Interface)]
        IStorage CreateStorage(
            [MarshalAs(UnmanagedType.BStr)] string name, int mode, int reserved1, int reserved2);

        [return: MarshalAs(UnmanagedType.Interface)]
        IStorage OpenStorage(
            [MarshalAs(UnmanagedType.BStr)] string name, IntPtr priority,
            int mode, IntPtr exclude, int reserved);

        void CopyTo(int excludeCount, [MarshalAs(UnmanagedType.LPArray)] Guid[]? exclude,
            IntPtr excludeNames, [MarshalAs(UnmanagedType.Interface)] IStorage destination);
        void MoveElementTo(
            [MarshalAs(UnmanagedType.BStr)] string name,
            [MarshalAs(UnmanagedType.Interface)] IStorage destination,
            [MarshalAs(UnmanagedType.BStr)] string newName, int flags);
        void Commit(int flags);
        void Revert();
        void EnumElements(int reserved1, IntPtr reserved2, int reserved3,
            [MarshalAs(UnmanagedType.Interface)] out object enumerator);
        void DestroyElement([MarshalAs(UnmanagedType.BStr)] string name);
        void RenameElement(
            [MarshalAs(UnmanagedType.BStr)] string oldName,
            [MarshalAs(UnmanagedType.BStr)] string newName);
        void SetElementTimes(
            [MarshalAs(UnmanagedType.BStr)] string name,
            FILETIME creationTime, FILETIME accessTime, FILETIME modificationTime);
        void SetClass(ref Guid classId);
        void SetStateBits(int stateBits, int mask);
        void Stat(out STATSTG statistics, int flag);
    }

    [ComImport]
    [Guid("0000000A-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ILockBytes
    {
        void ReadAt(long offset,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer,
            int count, [MarshalAs(UnmanagedType.LPArray)] int[] bytesRead);
        void WriteAt(long offset, IntPtr buffer, int count,
            [MarshalAs(UnmanagedType.LPArray)] int[] bytesWritten);
        void Flush();
        void SetSize(long size);
        void LockRegion(long offset, long count, int lockType);
        void UnlockRegion(long offset, long count, int lockType);
        void Stat(out STATSTG statistics, int flag);
    }
}
