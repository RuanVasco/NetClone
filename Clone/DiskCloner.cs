using System;
using System.IO;
using Microsoft.Win32.SafeHandles;
using NetClone.Disk;
using NetClone.IO;
using NetClone.Snapshot;
using DiscUtils;
using DiscUtils.Streams;
using DiscUtils.Vhdx;

namespace NetClone.Clone
{
    /// <summary>
    /// Clona o disco físico que contém o volumeRoot (ex: "C:\") para um VHDX bootável.
    /// Estratégia:
    ///   1) Copia o disco físico inteiro raw para o VHDX.
    ///   2) Regrava a partição do volumeRoot com dados do snapshot VSS (consistência).
    /// Requer execução como Administrador.
    /// </summary>
    public static class DiskCloner
    {
        public static void CloneSystemDiskToVhdx(string volumeRoot, string vhdxPath)
        {
            // ---- 1. Descobrir o disco físico ----
            string physical = DiskMapper.GetPhysicalDriveForVolume(volumeRoot);
            Console.WriteLine($"[*] Disco físico origem: {physical}");

            // ---- 1b. Dados da partição do volumeRoot (offset/size) pelo WMI ----
            var osPart = PartitionQuery.GetPartitionForDriveLetter(volumeRoot);
            Console.WriteLine($"[*] Partição do OS: {osPart.DeviceId}  offset=0x{osPart.StartingOffset:X}  size={osPart.Size:N0}");

            // ---- 2. Abrir disco físico e medir tamanho ----
            using SafeFileHandle physHandle = RawDevice.OpenRead(physical);
            long diskLength = RawDevice.GetDeviceLength(physHandle);
            Console.WriteLine($"[*] Tamanho do disco físico: {diskLength:N0} bytes");

            // ---- 3. Criar snapshot do volumeRoot ----
            using var snap = new SnapshotManager(new SnapshotOptions(new[] { volumeRoot }));
            snap.CreateSnapshot();
            string snapDevice = snap.Root;
            Console.WriteLine($"[*] Snapshot VSS de {volumeRoot}: {snapDevice}");

            // ---- 4. Criar VHDX dinâmico ----
            Console.WriteLine($"[*] Criando VHDX destino: {vhdxPath}");
            using var outStream = File.Create(vhdxPath);
            using var vhdx = DiscUtils.Vhdx.Disk.InitializeDynamic(outStream, Ownership.None, diskLength); 
            Stream dstDisk = vhdx.Content; // stream do disco virtual inteiro

            // ---- 5. Copiar disco físico bruto -> VHDX ----
            Console.WriteLine("[*] Copiando disco físico completo...");
            using (Stream srcDisk = RawDevice.AsStream(physHandle))
            {
                CopyStreamWithProgress(srcDisk, dstDisk, diskLength, "Disco");
            }

            // ---- 6. Patch: sobrescrever partição do OS com dados consistentes do snapshot ----
            Console.WriteLine("[*] Regravando partição do OS a partir do snapshot...");
            using (var snapHandle = RawDevice.OpenRead(snapDevice))
            using (Stream snapStream = RawDevice.AsStream(snapHandle))
            {
                PatchPartitionFromSnapshot(
                    snapshotStream: snapStream,
                    dstDisk: dstDisk,
                    targetOffset: (long)osPart.StartingOffset,
                    targetLength: (long)osPart.Size,
                    volumeRoot: volumeRoot);
            }

            Console.WriteLine($"[✓] Clone concluído: {vhdxPath}");
        }

        /// <summary>
        /// Copia todo o snapshot para o offset da partição alvo dentro do VHDX.
        /// Ajusta tamanho caso snapshot seja menor que partição (preenche com zeros).
        /// </summary>
        private static void PatchPartitionFromSnapshot(Stream snapshotStream, Stream dstDisk,
                                                       long targetOffset, long targetLength,
                                                       string volumeRoot,
                                                       int bufSize = 1024 * 1024)
        {
            // tamanho real do volume
            long volSize = new DriveInfo(volumeRoot).TotalSize;

            if (volSize > targetLength)
                throw new InvalidOperationException("Volume maior que partição destino — layout inconsistente.");

            // Posiciona destino no início da partição
            dstDisk.Seek(targetOffset, SeekOrigin.Begin);

            // Copia volumeSize bytes do snapshot
            byte[] buffer = new byte[bufSize];
            long remaining = volSize;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(bufSize, remaining);
                int read = snapshotStream.Read(buffer, 0, toRead);
                if (read <= 0) throw new IOException("EOF inesperado lendo snapshot.");
                dstDisk.Write(buffer, 0, read);
                remaining -= read;
            }

            // Se a partição destino é maior que o volume (espacinho não usado), zera o restante
            long pad = targetLength - volSize;
            if (pad > 0)
            {
                Array.Clear(buffer, 0, buffer.Length);
                while (pad > 0)
                {
                    int toWrite = (int)Math.Min(buffer.Length, pad);
                    dstDisk.Write(buffer, 0, toWrite);
                    pad -= toWrite;
                }
            }
        }

        /// <summary>
        /// Identifica a partição do volumeRoot no layout original.
        /// Critério: partição cujo tamanho está próximo ao tamanho do volume e cujo tipo sugere dados (NTFS/BasicData).
        /// </summary>
        private static PartitionEntry? FindPartitionForVolume(DiskLayout layout, string volumeRoot)
        {
            ulong volSize = (ulong)new DriveInfo(volumeRoot).TotalSize;

            PartitionEntry? best = null;
            ulong bestDelta = ulong.MaxValue;

            foreach (var p in layout.Partitions)
            {
                // ignore partições muito pequenas (EFI, MSR)
                if (p.Length < (50UL * 1024 * 1024)) // < 50MB => provavelmente EFI/MSR
                    continue;

                ulong delta = p.Length > volSize ? p.Length - volSize : volSize - p.Length;
                if (delta < bestDelta)
                {
                    best = p;
                    bestDelta = delta;
                }
            }

            return best;
        }

        private static void CopyStreamWithProgress(Stream src, Stream dst, long length,
                                                   string reportLabel,
                                                   int bufSize = 4 * 1024 * 1024)
        {
            byte[] buffer = new byte[bufSize];
            long copied = 0;
            int lastPct = -1;

            while (copied < length)
            {
                int toRead = (int)Math.Min(bufSize, length - copied);
                int read = src.Read(buffer, 0, toRead);
                if (read <= 0) break;
                dst.Write(buffer, 0, read);
                copied += read;

                int pct = (int)(copied * 100 / length);
                if (pct != lastPct && pct % 5 == 0) // report a cada 5%
                {
                    Console.WriteLine($"   {reportLabel}: {pct}%");
                    lastPct = pct;
                }
            }
        }
    }
}
