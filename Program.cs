using NetClone.Snapshot;

class Program {
    static void Main(string[] args)
    {
        Console.WriteLine("Criando snapshot de C:\\ ...");
        var opts = new SnapshotOptions(new[] { @"C:\" });

        using var snap = new SnapshotManager(opts);
        snap.CreateSnapshot();

        Console.WriteLine("Snapshot criado:");
        Console.WriteLine("  " + snap.Root);
    }
}