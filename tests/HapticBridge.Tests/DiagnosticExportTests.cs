using System.IO;
using HapticBridge.Core.Services;
using HapticBridge.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HapticBridge.Tests;

[TestClass]
public sealed class DiagnosticExportTests
{
    [TestMethod]
    public async Task SaveWritesOnlyTheReviewedReportAndRecoversAfterAFailedWrite()
    {
        var probe = new DiagnosticProbe();
        var model = new DiagnosticsViewModel(probe);
        Assert.IsFalse(model.CanSave);
        await model.LoadReportAsync();
        Assert.IsTrue(model.CanSave);
        var folder = Directory.CreateTempSubdirectory();
        string file = Path.Combine(folder.FullName, "Haptics report – 音频.txt");
        try
        {
            await model.SaveReportAsync(folder.FullName);
            StringAssert.Contains(model.ExportStatusMessage, "failed");
            Assert.IsTrue(model.CanSave);
            await File.WriteAllTextAsync(file, "Earlier report");
            await model.SaveReportAsync(file);
            Assert.AreEqual(model.ReportContent, await File.ReadAllTextAsync(file));
            Assert.AreEqual(1, probe.Reads);
            Assert.HasCount(1, folder.GetFiles());
            probe.Fail = true;
            await model.LoadReportAsync();
            Assert.IsFalse(model.CanSave);
            await model.SaveReportAsync(file);
            Assert.AreEqual("Snapshot 1", await File.ReadAllTextAsync(file));
        }
        finally { folder.Delete(recursive: true); }
    }

    private sealed class DiagnosticProbe : IDiagnosticsService
    {
        public int Reads { get; private set; }
        public bool Fail { get; set; }
        public Task<string> GenerateDiagnosticReportTextAsync() => Fail
            ? throw new IOException("Device unavailable") : Task.FromResult($"Snapshot {++Reads}");
        public Task<string> GenerateDiagnosticReportJsonAsync() => throw new NotSupportedException();
        public Task<string> ExportDiagnosticReportTextAsync(string exportPath) => throw new NotSupportedException();
    }
}
