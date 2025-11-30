using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using XREngine;

namespace XREngine.UnitTests.Core;

[TestFixture]
public class XRProjectTests
{
    [Test]
    public void CreateNew_CreatesStandardStructure()
    {
        string tempRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "ProjectCreation", Guid.NewGuid().ToString("N"));
        string projectFolder = Path.Combine(tempRoot, "SampleProjectRoot");
        Directory.CreateDirectory(projectFolder);

        try
        {
            XRProject project = XRProject.CreateNew(projectFolder, "SampleProject");
            string projectFile = Path.Combine(projectFolder, $"SampleProject.{XRProject.ProjectExtension}");
            File.Exists(projectFile).ShouldBeTrue();

            Directory.Exists(Path.Combine(projectFolder, XRProject.AssetsDirectoryName)).ShouldBeTrue();
            Directory.Exists(Path.Combine(projectFolder, XRProject.IntermediateDirectoryName)).ShouldBeTrue();
            Directory.Exists(Path.Combine(projectFolder, XRProject.BuildDirectoryName)).ShouldBeTrue();
            Directory.Exists(Path.Combine(projectFolder, XRProject.PackagesDirectoryName)).ShouldBeTrue();
            Directory.Exists(Path.Combine(projectFolder, XRProject.ConfigDirectoryName)).ShouldBeTrue();

            project.GetUnexpectedRootEntries().ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, true);
        }
    }
}
