using System.ComponentModel;
using System.IO;
using HapticBridge.Setup.Prerequisites;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HapticBridge.Tests;

[TestClass]
public sealed class PrerequisiteInstallerTests
{
    [TestMethod]
    public async Task InstallsOnlyApprovedMissingPackagesAndRechecksBundledDependencies()
    {
        HashSet<PrerequisiteId> installed = [PrerequisiteId.VisualCpp];
        List<PrerequisiteId> started = [];
        var setup = new PrerequisiteInstaller(
            id => (installed.Contains(id) ? PrerequisiteState.Installed : PrerequisiteState.Missing, null),
            (package, isMissing, _, _) =>
            {
                Assert.IsTrue(isMissing());
                started.Add(package.Id);
                installed.Add(package.Id);
                if (package.Id == PrerequisiteId.Apollo) installed.Add(PrerequisiteId.ViGEmBus);
                return Task.FromResult<int?>(0);
            });

        var native = setup.Inspect(SetupRoute.DualSenseHaptics);
        var external = setup.Inspect(SetupRoute.ExternalTools);
        CollectionAssert.AreEqual(new[] { "Microsoft Visual C++ x64", "Apollo", "ViGEmBus", "DS4Windows with VIIPER" },
            native.Select(item => item.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "VB-CABLE", "Voicemeeter Banana", "SoundSync" },
            external.Skip(native.Count).Select(item => item.Name).ToArray());

        await setup.InstallAsync(SetupRoute.DualSenseHaptics, external.Select(item => item.Name).ToArray(),
            new Progress<string>(), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { PrerequisiteId.Apollo, PrerequisiteId.DS4Windows }, started);
        Assert.IsTrue(setup.Inspect(SetupRoute.DualSenseHaptics).All(item => item.State == PrerequisiteState.Installed));
    }

    [TestMethod]
    public async Task KeepsRestartStateAndStopsBeforeTheNextPackageWhenWindowsCancels()
    {
        List<PrerequisiteId> started = [];
        var setup = new PrerequisiteInstaller(
            _ => (PrerequisiteState.Missing, null),
            (package, _, _, _) =>
            {
                started.Add(package.Id);
                if (package.Id == PrerequisiteId.Voicemeeter) throw new Win32Exception(1223);
                return Task.FromResult<int?>(3010);
            });
        var external = setup.Inspect(SetupRoute.ExternalTools);
        var approved = external.Skip(4).Select(item => item.Name).ToArray();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            setup.InstallAsync(SetupRoute.ExternalTools, approved, new Progress<string>(), CancellationToken.None));

        CollectionAssert.AreEqual(new[] { PrerequisiteId.VbCable, PrerequisiteId.Voicemeeter }, started);
        var result = setup.Inspect(SetupRoute.ExternalTools);
        Assert.AreEqual(PrerequisiteState.RestartRequired, result.Single(item => item.Name == "VB-CABLE").State);
        Assert.AreEqual(PrerequisiteState.Missing, result.Single(item => item.Name == "SoundSync").State);
    }

    [TestMethod]
    public async Task VerifiesExactDownloadAndPreservesExistingPortableExecutable()
    {
        var temp = Directory.CreateTempSubdirectory("HapticBridge-setup-test-");
        try
        {
            string download = Path.Combine(temp.FullName, "download");
            string executable = Path.Combine(temp.FullName, "SoundSync.exe");
            await File.WriteAllTextAsync(download, "abc");
            const string expected = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
            await PrerequisiteInstaller.VerifySha256Async(download, expected, CancellationToken.None);
            PrerequisiteInstaller.PlacePortable(download, executable);
            Assert.AreEqual("abc", await File.ReadAllTextAsync(executable));

            await File.WriteAllTextAsync(download, "changed");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                PrerequisiteInstaller.VerifySha256Async(download, expected, CancellationToken.None));
            Assert.ThrowsExactly<IOException>(() => PrerequisiteInstaller.PlacePortable(download, executable));
            Assert.AreEqual("abc", await File.ReadAllTextAsync(executable));
            Assert.HasCount(2, temp.GetFiles());
        }
        finally { temp.Delete(recursive: true); }
    }
}
