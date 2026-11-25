using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class ThirdPartyImportBackupTests
{
    [Test]
    public void PreservedBackupKeepsPriorRootAndClosureAfterDispose()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "XREngine", "UnitTests", "scratch",
            "import-backup-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(scratch, "item.asset");
        string closure = Path.Combine(scratch, "item");
        string closureFile = Path.Combine(closure, "part.asset");
        string? backupRoot = null;
        IDisposable? backup = null;
        try
        {
            Directory.CreateDirectory(closure);
            File.WriteAllText(target, "previous root");
            File.WriteAllText(closureFile, "previous closure");

            Type backupType = typeof(ThirdPartyAssetImportService).GetNestedType(
                "ThirdPartyImportBackup", BindingFlags.NonPublic)!;
            backup = (IDisposable)Activator.CreateInstance(backupType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [target], culture: null)!;
            backupRoot = (string)backupType.GetField("_backupRoot",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backup)!;

            File.WriteAllText(target, "replacement root");
            File.WriteAllText(closureFile, "replacement closure");
            backupType.GetMethod("PreserveForRecovery",
                BindingFlags.Instance | BindingFlags.Public)!.Invoke(backup, null);
            backup.Dispose();
            backup = null;

            File.ReadAllText(Path.Combine(backupRoot, "root.asset")).ShouldBe("previous root");
            File.ReadAllText(Path.Combine(backupRoot, "closure", "part.asset"))
                .ShouldBe("previous closure");
            File.ReadAllText(target).ShouldBe("replacement root");
        }
        finally
        {
            backup?.Dispose();
            if (backupRoot is not null && Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }
}
