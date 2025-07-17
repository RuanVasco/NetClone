using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetClone.Disk
{
    public enum PartitionStyle
    {
        Mbr,
        Gpt,
        Raw
    }

    public sealed record PartitionEntry(
        ulong Offset,
        ulong Length,
        string Type,    // GPT Type GUID string, or "MBR-0x07" etc.
        string? Name,
        uint? MbrType,
        Guid? GptType,
        Guid? GptId,
        bool Bootable);

    public sealed record DiskLayout(
        PartitionStyle Style,
        ulong DiskLength,
        IReadOnlyList<PartitionEntry> Partitions);

    public static class DiskLayoutReader
    {
        private const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;

        [StructLayout(LayoutKind.Sequential)]
        private struct DRIVE_LAYOUT_INFORMATION_GPT
        {
            public Guid DiskId;
            public long StartingUsableOffset;
            public long UsableLength;
            public int MaxPartitionCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DRIVE_LAYOUT_INFORMATION_MBR
        {
            public int Signature;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DRIVE_LAYOUT_INFORMATION_UNION
        {
            [FieldOffset(0)]
            public DRIVE_LAYOUT_INFORMATION_MBR Mbr;

            [FieldOffset(0)]
            public DRIVE_LAYOUT_INFORMATION_GPT Gpt;
        }

        private enum PARTITION_STYLE : int
        {
            MBR = 0,
            GPT = 1,
            RAW = 2
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PARTITION_INFORMATION_MBR
        {
            public byte PartitionType;
            [MarshalAs(UnmanagedType.I1)]
            public bool BootIndicator;
            [MarshalAs(UnmanagedType.I1)]
            public bool RecognizedPartition;
            public int HiddenSectors;
            public Guid PartitionId; // not in old header, extended by Alpha?
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PARTITION_INFORMATION_GPT
        {
            public Guid PartitionType;
            public Guid PartitionId;
            public long Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 36)]
            public string Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PARTITION_INFORMATION_EX
        {
            public PARTITION_STYLE PartitionStyle;
            public long StartingOffset;
            public long PartitionLength;
            public int PartitionNumber;
            [MarshalAs(UnmanagedType.I1)]
            public bool RewritePartition;
            [MarshalAs(UnmanagedType.I1)]
            public bool IsServicePartition;
            public PARTITION_INFORMATION_EX_UNION Info;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PARTITION_INFORMATION_EX_UNION
        {
            [FieldOffset(0)]
            public PARTITION_INFORMATION_MBR Mbr;

            [FieldOffset(0)]
            public PARTITION_INFORMATION_GPT Gpt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DRIVE_LAYOUT_INFORMATION_EX
        {
            public PARTITION_STYLE PartitionStyle;
            public int PartitionCount;
            public DRIVE_LAYOUT_INFORMATION_UNION Info;
            // Followed in memory by PARTITION_INFORMATION_EX[PartitionCount]
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        public static DiskLayout Get(SafeFileHandle physicalHandle, ulong diskLength)
        {
            // buffer grande o bastante para até ~128 partições
            int bufSize = Marshal.SizeOf<DRIVE_LAYOUT_INFORMATION_EX>() + 128 * Marshal.SizeOf<PARTITION_INFORMATION_EX>();
            IntPtr buffer = Marshal.AllocHGlobal(bufSize);

            try
            {
                int ret;
                if (!DeviceIoControl(physicalHandle, IOCTL_DISK_GET_DRIVE_LAYOUT_EX,
                    IntPtr.Zero, 0, buffer, bufSize, out ret, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao obter layout do disco.");
                }

                // Lê header
                var layout = Marshal.PtrToStructure<DRIVE_LAYOUT_INFORMATION_EX>(buffer);
                var ptr = buffer + Marshal.SizeOf<DRIVE_LAYOUT_INFORMATION_EX>();

                var parts = new List<PartitionEntry>(layout.PartitionCount);

                for (int i = 0; i < layout.PartitionCount; i++)
                {
                    var p = Marshal.PtrToStructure<PARTITION_INFORMATION_EX>(ptr);
                    ptr += Marshal.SizeOf<PARTITION_INFORMATION_EX>();

                    if (p.PartitionLength <= 0)
                        continue; // slot vazio

                    switch (p.PartitionStyle)
                    {
                        case PARTITION_STYLE.GPT:
                            parts.Add(new PartitionEntry(
                                Offset: (ulong)p.StartingOffset,
                                Length: (ulong)p.PartitionLength,
                                Type: p.Info.Gpt.PartitionType.ToString(),
                                Name: p.Info.Gpt.Name?.TrimEnd('\0'),
                                MbrType: null,
                                GptType: p.Info.Gpt.PartitionType,
                                GptId: p.Info.Gpt.PartitionId,
                                Bootable: false   // GPT não usa flag bootável MBR tradicional
                            ));
                            break;

                        case PARTITION_STYLE.MBR:
                            parts.Add(new PartitionEntry(
                                Offset: (ulong)p.StartingOffset,
                                Length: (ulong)p.PartitionLength,
                                Type: $"MBR-0x{p.Info.Mbr.PartitionType:X2}",
                                Name: null,
                                MbrType: p.Info.Mbr.PartitionType,
                                GptType: null,
                                GptId: null,
                                Bootable: p.Info.Mbr.BootIndicator
                            ));
                            break;

                        default:
                            break;
                    }
                }

                PartitionStyle style = layout.PartitionStyle switch
                {
                    PARTITION_STYLE.GPT => PartitionStyle.Gpt,
                    PARTITION_STYLE.MBR => PartitionStyle.Mbr,
                    _ => PartitionStyle.Raw
                };

                return new DiskLayout(style, diskLength, parts);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
