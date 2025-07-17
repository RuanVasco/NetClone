using System;
using System.Management;
using System.Text.RegularExpressions;

namespace NetClone.Disk
{
    /// <summary>
    /// Resolve uma letra de unidade (ex.: "C:\") ou caminho de volume GUID
    /// (ex.: "\\?\Volume{GUID}\") para o respectivo disco físico
    /// (ex.: "\\.\PhysicalDrive0").
    /// </summary>
    public static class DiskMapper
    {
        public static string GetPhysicalDriveForVolume(string driveRoot)
        {
            if (string.IsNullOrWhiteSpace(driveRoot))
                throw new ArgumentException("driveRoot vazio.", nameof(driveRoot));

            // Aceita caminho GUID tipo \\?\Volume{...}\
            if (IsVolumeGuidPath(driveRoot))
            {
                // Tenta resolver volume GUID -> letra e reusar fluxo padrão.
                string? letter = TryGetDriveLetterFromVolumeGuid(driveRoot);
                if (!string.IsNullOrEmpty(letter))
                {
                    driveRoot = letter;
                }
                else
                {
                    // Não tem letra; resolve direto via Win32_Volume -> Partition -> DiskDrive
                    return GetPhysicalDriveFromVolumeGuidWmi(driveRoot);
                }
            }

            // Normaliza "C:" -> "C:\"
            if (driveRoot.Length == 2 && driveRoot[1] == ':') driveRoot += "\\";
            if (!driveRoot.EndsWith("\\")) driveRoot += "\\";

            string driveLetter = driveRoot.Substring(0, 2); // "C:"

            // LogicalDisk -> Partition
            using var q1 = new ManagementObjectSearcher(
                "ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + driveLetter + "'} " +
                "WHERE AssocClass = Win32_LogicalDiskToPartition");

            foreach (ManagementObject part in q1.Get())
            {
                string partitionDeviceId = (string)part["DeviceID"]; // "Disk #0, Partition #3"

                // Partition -> DiskDrive
                using var q2 = new ManagementObjectSearcher(
                    "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + partitionDeviceId + "'} " +
                    "WHERE AssocClass = Win32_DiskDriveToDiskPartition");

                foreach (ManagementObject drive in q2.Get())
                {
                    string pnp = (string)drive["DeviceID"]; // "\\.\PHYSICALDRIVE0"
                    return NormalizePhysicalDrivePath(pnp);
                }
            }

            throw new InvalidOperationException($"Não foi possível mapear {driveRoot} para um PhysicalDrive.");
        }

        // ------------------------------------------------------------------

        private static bool IsVolumeGuidPath(string value) =>
            value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase);

        private static string NormalizePhysicalDrivePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var m = Regex.Match(path, @"^\\\\\.\\physicaldrive(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success)
                return $@"\\.\PhysicalDrive{m.Groups[1].Value}";
            return path;
        }

        private static string? TryGetDriveLetterFromVolumeGuid(string volumeGuidPath)
        {
            string vg = volumeGuidPath.TrimEnd('\\');

            using var q = new ManagementObjectSearcher(
                "SELECT DeviceID, DriveLetter FROM Win32_Volume WHERE DeviceID='" + vg.Replace("'", "''") + "'");

            foreach (ManagementObject vol in q.Get())
            {
                if (vol["DriveLetter"] is string letter && !string.IsNullOrEmpty(letter))
                    return letter + @"\";
            }
            return null;
        }

        private static string GetPhysicalDriveFromVolumeGuidWmi(string volumeGuidPath)
        {
            string vg = volumeGuidPath.TrimEnd('\\');

            using var q = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_Volume WHERE DeviceID='" + vg.Replace("'", "''") + "'");

            foreach (ManagementObject vol in q.Get())
            {
                // Volume -> Partition
                using var q1 = new ManagementObjectSearcher(
                    "ASSOCIATORS OF {Win32_Volume.DeviceID='" + vg.Replace("'", "''") + "'} " +
                    "WHERE AssocClass = Win32_VolumeToDiskPartition");

                foreach (ManagementObject part in q1.Get())
                {
                    string partitionDeviceId = (string)part["DeviceID"];

                    // Partition -> DiskDrive
                    using var q2 = new ManagementObjectSearcher(
                        "ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" + partitionDeviceId + "'} " +
                        "WHERE AssocClass = Win32_DiskDriveToDiskPartition");

                    foreach (ManagementObject drive in q2.Get())
                    {
                        string pnp = (string)drive["DeviceID"];
                        return NormalizePhysicalDrivePath(pnp);
                    }
                }
            }

            throw new InvalidOperationException($"Não foi possível mapear Volume GUID {volumeGuidPath} para PhysicalDrive.");
        }
    }
}
