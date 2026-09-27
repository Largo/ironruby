/* ****************************************************************************
 *
 * The Windows answers to the questions File and File::Stat ask of the kernel,
 * taken from what MRI's Windows build (win32/win32.c) answers.
 *
 * Off Unix a File::Stat is a FileInfo/DirectoryInfo with no stat(2) result
 * behind it, so everything that needs the file system's own view of a file -
 * its identity (volume serial number and file index, which are what dev and
 * ino are there), its link count, a lock on it, a second name for it - comes
 * from here.
 *
 * ***************************************************************************/

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IronRuby.Builtins {

    internal static class WindowsFiles {

        #region kernel32

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OVERLAPPED {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint Offset;
            public uint OffsetHigh;
            public IntPtr EventHandle;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool LockFileEx(SafeFileHandle handle, uint flags, uint reserved,
            uint lengthLow, uint lengthHigh, ref OVERLAPPED overlapped);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool UnlockFileEx(SafeFileHandle handle, uint reserved,
            uint lengthLow, uint lengthHigh, ref OVERLAPPED overlapped);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint Reserved0;      // the reparse tag, when FILE_ATTRIBUTE_REPARSE_POINT is set
            public uint Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateFileName;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindFirstFileW(string name, out WIN32_FIND_DATA data);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);

        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        private const uint IO_REPARSE_TAG_AF_UNIX = 0x80000023;

        private const uint FILE_READ_ATTRIBUTES = 0x0080;
        private const uint FILE_SHARE_ALL = 0x00000007;    // read | write | delete
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;   // needed to open a directory

        private const uint LOCKFILE_FAIL_IMMEDIATELY = 0x00000001;
        private const uint LOCKFILE_EXCLUSIVE_LOCK = 0x00000002;

        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_NOT_SAME_DEVICE = 17;
        private const int ERROR_LOCK_VIOLATION = 33;
        private const int ERROR_NOT_SUPPORTED = 50;
        private const int ERROR_ALREADY_EXISTS = 183;
        private const int ERROR_FILE_EXISTS = 80;
        private const int ERROR_INVALID_NAME = 123;
        private const int ERROR_NOT_LOCKED = 158;
        private const int ERROR_IO_PENDING = 997;
        private const int ERROR_TOO_MANY_LINKS = 1142;

        #endregion

        #region identity

        /// <summary>
        /// The volume serial number, the 64-bit file index and the hard link count of a file or
        /// directory: MRI's st_dev, st_ino and st_nlink on Windows. False when it cannot be opened.
        /// </summary>
        internal static bool TryGetFileId(string/*!*/ path, out uint volume, out ulong index, out uint links) {
            volume = 0;
            index = 0;
            links = 0;
            using (var handle = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero)) {

                BY_HANDLE_FILE_INFORMATION info;
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out info)) {
                    return false;
                }
                volume = info.VolumeSerialNumber;
                index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                links = info.NumberOfLinks;
                return true;
            }
        }

        #endregion

        #region socket files

        /// <summary>
        /// Whether <paramref name="path"/> is where an AF_UNIX socket is bound: Windows makes that
        /// a reparse point tagged IO_REPARSE_TAG_AF_UNIX, and MRI's stat reports it as S_IFSOCK.
        /// </summary>
        internal static bool IsUnixSocket(string/*!*/ path) {
            WIN32_FIND_DATA data;
            IntPtr find = FindFirstFileW(path, out data);
            if (find == IntPtr.Zero || find == new IntPtr(-1)) {
                return false;
            }
            FindClose(find);
            return (data.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 && data.Reserved0 == IO_REPARSE_TAG_AF_UNIX;
        }

        #endregion

        #region mode

        /// <summary>
        /// win32.c's fileattr_to_unixmode: readable always, writable unless read-only, a directory
        /// executable, a .bat/.cmd/.com/.exe executable, and the read and execute bits copied to
        /// group and other - so a writable file is 0100644 and a directory 040755, where the old
        /// answer (owner bits only) made every file look 0600 and world_readable? nil.
        /// </summary>
        internal static int Mode(FileSystemInfo/*!*/ info) {
            const int S_IFREG = 0x8000, S_IFDIR = 0x4000, S_IFCHR = 0x2000, S_IREAD = 0x100, S_IWRITE = 0x80, S_IEXEC = 0x40;

            if (!(info is FileInfo) && !(info is DirectoryInfo)) {
                // NUL, the console, a pipe: a character device, readable and writable by all.
                return S_IFCHR | 0x1B6 /* 0666 */;
            }

            int mode = S_IREAD;
            if ((info.Attributes & FileAttributes.ReadOnly) == 0) {
                mode |= S_IWRITE;
            }

            if (info is DirectoryInfo) {
                mode |= S_IFDIR | S_IEXEC;
            } else if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && IsUnixSocket(info.FullName)) {
                mode |= 0xC000; // S_IFSOCK
            } else {
                mode |= S_IFREG;
                string extension = info.Extension;
                if (String.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)) {
                    mode |= S_IEXEC;
                }
            }

            mode |= (mode & 0x140 /* 0500 */) >> 3;
            mode |= (mode & 0x140 /* 0500 */) >> 6;
            return mode;
        }

        #endregion

        #region flock

        /// <summary>
        /// flock(2) as win32.c's flock_winnt does it: LockFileEx / UnlockFileEx over the whole
        /// (64-bit) range, shared or exclusive. Returns 0, or the errno: EWOULDBLOCK when the lock
        /// is held elsewhere and <paramref name="operation"/> did not ask to wait. Waiting is done by
        /// the caller in managed sleeps, so that the thread stays interruptible and says "sleep".
        /// Note that unlike flock(2) these locks are mandatory: while one handle holds an exclusive
        /// lock, reads and writes through other handles fail. That is MRI's behaviour too.
        /// </summary>
        internal static int TryLock(SafeFileHandle/*!*/ handle, int operation) {
            const int LOCK_SH = 1, LOCK_EX = 2, LOCK_UN = 8;
            const int EWOULDBLOCK = 11, EINVAL = 22;

            var overlapped = new OVERLAPPED();
            bool ok;
            switch (operation & ~4 /* LOCK_NB */) {
                case LOCK_SH:
                    ok = LockFileEx(handle, LOCKFILE_FAIL_IMMEDIATELY, 0, UInt32.MaxValue, UInt32.MaxValue, ref overlapped);
                    break;
                case LOCK_EX:
                    ok = LockFileEx(handle, LOCKFILE_EXCLUSIVE_LOCK | LOCKFILE_FAIL_IMMEDIATELY, 0, UInt32.MaxValue, UInt32.MaxValue, ref overlapped);
                    break;
                case LOCK_UN:
                    ok = UnlockFileEx(handle, 0, UInt32.MaxValue, UInt32.MaxValue, ref overlapped);
                    if (!ok && Marshal.GetLastWin32Error() == ERROR_NOT_LOCKED) {
                        // unlocking what is not locked is not an error for flock(2)
                        return 0;
                    }
                    break;
                default:
                    return EINVAL;
            }
            if (ok) {
                return 0;
            }
            int error = Marshal.GetLastWin32Error();
            return (error == ERROR_LOCK_VIOLATION || error == ERROR_IO_PENDING) ? EWOULDBLOCK : Errno(error);
        }

        #endregion

        #region link

        /// <summary>link(2): CreateHardLink. Returns 0 or the errno.</summary>
        internal static int HardLink(string/*!*/ existing, string/*!*/ newName) {
            return CreateHardLinkW(newName, existing, IntPtr.Zero) ? 0 : Errno(Marshal.GetLastWin32Error());
        }

        #endregion

        /// <summary>The Linux errno for a Win32 error, as far as these calls produce them.</summary>
        internal static int Errno(int win32Error) {
            switch (win32Error) {
                case ERROR_FILE_NOT_FOUND:
                case ERROR_PATH_NOT_FOUND: return 2;       // ENOENT
                case ERROR_ACCESS_DENIED: return 13;       // EACCES
                case ERROR_NOT_SAME_DEVICE: return 18;     // EXDEV
                case ERROR_ALREADY_EXISTS:
                case ERROR_FILE_EXISTS: return 17;         // EEXIST
                case ERROR_TOO_MANY_LINKS: return 31;      // EMLINK
                case ERROR_NOT_SUPPORTED: return 95;       // EOPNOTSUPP
                case ERROR_INVALID_NAME: return 22;        // EINVAL
                case ERROR_LOCK_VIOLATION: return 11;      // EWOULDBLOCK
                default: return 22;                        // EINVAL
            }
        }
    }
}
