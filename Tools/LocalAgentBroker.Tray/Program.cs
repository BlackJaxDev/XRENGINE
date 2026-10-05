using XREngine.LocalAgentBroker.Shared;

namespace XREngine.LocalAgentBroker.Tray;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            string repositoryRoot = ParseRepositoryRoot(args);
            string mutexName = BrokerUiInstanceName.Create(repositoryRoot);
            using var instanceMutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
            if (!createdNew)
                return 0;

            ApplicationConfiguration.Initialize();
            try
            {
                Application.Run(new TrayApplicationContext(repositoryRoot, showHistory: args.Length == 3));
            }
            finally
            {
                instanceMutex.ReleaseMutex();
            }
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Local Agent Broker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string ParseRepositoryRoot(string[] args)
    {
        if (args.Length is not (2 or 3)
            || !string.Equals(args[0], "--repo-root", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(args[1])
            || (args.Length == 3 && !string.Equals(args[2], "--show-history", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The tray companion requires --repo-root <path> [--show-history].");
        }

        string root = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(root, "AGENTS.md")))
            throw new ArgumentException($"Repository root '{root}' does not contain AGENTS.md.");
        return root;
    }
}
