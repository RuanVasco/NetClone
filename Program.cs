using NetClone.Clone;

class Program {
    static void Main(string[] args) {
        string outVhdx = Path.Combine(AppContext.BaseDirectory, "SystemClone.vhdx");
        DiskCloner.CloneSystemDiskToVhdx(@"C:\", outVhdx);
    }
}