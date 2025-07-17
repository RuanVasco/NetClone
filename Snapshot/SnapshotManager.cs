using Alphaleonis.Win32.Vss;

namespace NetClone.Snapshot
{
    public sealed class SnapshotOptions
    {
        public IReadOnlyList<string> Volumes { get; }
        public VssSnapshotContext Context { get; }

        public SnapshotOptions(IEnumerable<string> volumes, VssSnapshotContext context = VssSnapshotContext.Backup)
        {
            if (volumes == null) throw new ArgumentNullException(nameof(volumes));
            Volumes = volumes.Select(Normalize).ToArray();
            Context = context;
        }

        private static string Normalize(string v)
        {
            if (string.IsNullOrWhiteSpace(v))
                throw new ArgumentException("Volume inválido.", nameof(v));

            if (v.Length == 2 && v[1] == ':') v += "\\";
            if (!v.EndsWith("\\")) v += "\\";
            return v;
        }
    }

    public sealed class SnapshotResult
    {
        public Guid SnapshotId { get; }
        public string Volume { get; }
        public string DevicePath { get; }
        public VssSnapshotProperties Properties { get; }

        internal SnapshotResult(Guid id, string volume, VssSnapshotProperties props)
        {
            SnapshotId = id;
            Volume = volume;
            Properties = props;
            DevicePath = props.SnapshotDeviceObject;
        }

        public override string ToString() => $"{Volume} -> {DevicePath}";
    }

    public sealed class SnapshotManager : IDisposable
    {
        private readonly IVssBackupComponents _vss;
        private readonly SnapshotOptions _options;
        private readonly Dictionary<string, Guid> _volToSnap = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SnapshotResult> _results = new(StringComparer.OrdinalIgnoreCase);
        private Guid _setId;
        private bool _created;
        private bool _disposed;

        public IReadOnlyDictionary<string, SnapshotResult> Results => _results;

        public SnapshotManager(SnapshotOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            // Carrega a implementação VSS correta (AlphaVSS 2.x)
            var factory = VssFactoryProvider.Default.GetVssFactory();
            _vss = factory.CreateVssBackupComponents();

            // Passo 1: inicializa
            _vss.InitializeForBackup(null);

            // Passo 2: contexto (Backup, ClientAccessible, etc.)
            _vss.SetContext(_options.Context);

            // Passo 3 (recomendado): coleta metadados dos writers
            _vss.GatherWriterMetadata();

            // Passo 4: estado do backup
            // selectComponents:false → backup de volume inteiro
            // backupBootableSystemState:false → não é "system state"
            // VssBackupType.Full → backup completo
            // partialFiles:false → sem arquivos parciais
            _vss.SetBackupState(selectComponents: false,
                                backupBootableSystemState: false,
                                backupType: VssBackupType.Full,
                                partialFileSupport: false);

            try {
                _setId = _vss.StartSnapshotSet();
            }
            catch (VssSnapshotSetInProgressException) {
                _vss.AbortBackup();      
                _setId = _vss.StartSnapshotSet();
            }

            // Adiciona volumes
            foreach (var vol in _options.Volumes)
            {
                if (!_vss.IsVolumeSupported(vol))
                    throw new VssVolumeNotSupportedException(vol);

                var snapId = _vss.AddToSnapshotSet(vol);
                _volToSnap[vol] = snapId;
            }
        }

        /// <summary>
        /// Cria efetivamente os snapshots (bloqueia até conclusão).
        /// </summary>
        public void CreateSnapshot()
        {
            if (_created) return;

            // Passo 6: prepare writers (obrigatório antes de DoSnapshotSet)
            _vss.PrepareForBackup();

            // Passo 7: cria snapshot(s)
            _vss.DoSnapshotSet();

            _created = true;

            // Captura propriedades
            foreach (var kv in _volToSnap)
            {
                var props = _vss.GetSnapshotProperties(kv.Value);
                _results[kv.Key] = new SnapshotResult(kv.Value, kv.Key, props);
            }

            // Passo 8 (opcional, completa ciclo de backup)
            // Exec(_vss.BackupComplete());  // use se quiser notificar writers
        }

        /// <summary>
        /// Caminho do snapshot quando só há 1 volume. Usa Results caso haja múltiplos.
        /// </summary>
        public string Root
        {
            get
            {
                if (!_created)
                    throw new InvalidOperationException("Snapshot ainda não criado. Chame CreateSnapshot().");

                if (_results.Count != 1)
                    throw new InvalidOperationException("SnapshotSet contém múltiplos volumes; use Results.");

                return _results.Values.First().DevicePath;
            }
        }

        /// <summary>
        /// Obtém caminho do snapshot de um volume específico (ex.: @"C:\").
        /// </summary>
        public string GetSnapshotPath(string volume)
        {
            if (volume is null) throw new ArgumentNullException(nameof(volume));
            if (!_created) throw new InvalidOperationException("Snapshot ainda não criado.");

            if (volume.Length == 2 && volume[1] == ':') volume += "\\";
            if (!volume.EndsWith("\\")) volume += "\\";

            return _results[volume].DevicePath;
        }

        /// <summary>
        /// Exclui os snapshots criados. Chamado automaticamente em Dispose().
        /// </summary>
        public void Delete()
        {
            if (_disposed) return;

            foreach (var res in _results.Values)
            {
                try
                {
                    // Overload 2.x sem enum
                    _vss.DeleteSnapshot(res.SnapshotId, forceDelete: false);
                }
                catch
                {
                    // ignora falhas
                }
            }

            _disposed = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            try { Delete(); } catch { }
            GC.SuppressFinalize(this);
        }
    }
}
