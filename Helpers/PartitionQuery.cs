using System;
using System.Management;

namespace NetClone.Disk
{
    public sealed record PartitionInfo(string DeviceId, ulong StartingOffset, ulong Size, string Type, bool Bootable);

    public static class PartitionQuery
    {
        /// <summary>
        /// Obtém info da partição associada a uma letra (ex.: "C:\").
        /// </summary>
        public static PartitionInfo GetPartitionForDriveLetter(string driveRoot)
        {
            if (driveRoot.Length == 2 && driveRoot[1] == ':') driveRoot += "\\";
            if (!driveRoot.EndsWith("\\")) driveRoot += "\\";
            string driveLetter = driveRoot.Substring(0, 2); // "C:"

            using var q1 = new ManagementObjectSearcher(
                "ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + driveLetter + "'} " +
                "WHERE AssocClass = Win32_LogicalDiskToPartition");

            foreach (ManagementObject part in q1.Get())
            {
                string deviceId = (string)part["DeviceID"];
                ulong offset = (ulong)(Convert.ToUInt64(part["StartingOffset"]));
                ulong size = (ulong)(Convert.ToUInt64(part["Size"]));
                string type = (string)part["Type"];
                bool boot = false;
                try { boot = part["BootPartition"] is bool b && b; } catch { }

                return new PartitionInfo(deviceId, offset, size, type, boot);
            }

            throw new InvalidOperationException($"Não foi possível localizar partição para {driveRoot}.");
        }
    }
}