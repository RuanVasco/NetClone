using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetClone.IO
{
    public static class RawDevice
    {
        private const uint GENERIC_READ  = 0x80000000;
        private const uint FILE_SHARE_READ   = 0x00000001;
        private const uint FILE_SHARE_WRITE  = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING     = 3;
        private const uint FILE_FLAG_SEQUENTIAL_SCAN   = 0x08000000;
        private const uint FILE_FLAG_BACKUP_SEMANTICS  = 0x02000000;
        // (para máximo desempenho poderíamos adicionar NO_BUFFERING, mas exige alinhamento)

        private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

        [StructLayout(LayoutKind.Sequential)]
        private struct GET_LENGTH_INFORMATION
        {
            public long Length;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            out GET_LENGTH_INFORMATION lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        /// <summary>
        /// Abre um caminho de device (ex.: \\.\PhysicalDrive0 ou \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy8) para leitura.
        /// </summary>
        public static SafeFileHandle OpenRead(string devicePath)
        {
            var handle = CreateFile(
                devicePath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_SEQUENTIAL_SCAN | FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Falha ao abrir device '{devicePath}'.");

            return handle;
        }

        public static long GetDeviceLength(SafeFileHandle handle)
        {
            GET_LENGTH_INFORMATION info;
            int ret;
            if (!DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO,
                                 IntPtr.Zero, 0,
                                 out info, Marshal.SizeOf<GET_LENGTH_INFORMATION>(),
                                 out ret, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao obter tamanho do device.");
            }
            return info.Length;
        }

        /// <summary>
        /// FileStream conveniência. Não chame .Length no stream — use GetDeviceLength().
        /// </summary>
        public static FileStream AsStream(SafeFileHandle handle, int bufferSize = 1024 * 1024)
            => new FileStream(handle, FileAccess.Read, bufferSize, isAsync: false);
    }
}
