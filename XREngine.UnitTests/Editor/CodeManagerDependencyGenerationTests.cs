using NUnit.Framework;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class CodeManagerDependencyGenerationTests
{
    [Test]
    public void ReadEnginePackageReferencesFromDeps_UsesRuntimeEnginePackageDependencies()
    {
        string tempDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string depsPath = Path.Combine(tempDirectory, "XREngine.Editor.deps.json");

        File.WriteAllText(depsPath, """
            {
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "XREngine/0.1.0-dev": {
                    "dependencies": {
                      "MagicPhysX": "1.0.0",
                      "OpenVR.NET": "0.8.5.0",
                      "Silk.NET.Core": "2.23.0",
                      "XREngine.Data": "0.1.0-dev"
                    }
                  },
                  "XREngine.Data/0.1.0-dev": {
                    "dependencies": {
                      "YamlDotNet": "16.3.0"
                    }
                  },
                  "Silk.NET.Core/2.23.0": {
                    "dependencies": {
                      "Microsoft.DotNet.PlatformAbstractions": "3.1.6",
                      "Microsoft.Extensions.DependencyModel": "9.0.9"
                    }
                  },
                  "XREngine.Editor/1.0.0": {
                    "dependencies": {
                      "Microsoft.Build": "18.3.3"
                    }
                  }
                }
              },
              "libraries": {
                "MagicPhysX/1.0.0": {
                  "type": "package"
                },
                "Microsoft.Build/18.3.3": {
                  "type": "package"
                },
                "Microsoft.DotNet.PlatformAbstractions/3.1.6": {
                  "type": "package"
                },
                "Microsoft.Extensions.DependencyModel/9.0.9": {
                  "type": "package"
                },
                "OpenVR.NET/0.8.5.0": {
                  "type": "reference"
                },
                "Silk.NET.Core/2.23.0": {
                  "type": "package"
                },
                "YamlDotNet/16.3.0": {
                  "type": "package"
                }
              }
            }
            """);

        (string name, string version)[] references = CodeManager.ReadEnginePackageReferencesFromDeps(depsPath);

        Assert.Multiple(() =>
        {
            Assert.That(references, Does.Contain(("MagicPhysX", "1.0.0")));
            Assert.That(references, Does.Contain(("Microsoft.DotNet.PlatformAbstractions", "3.1.6")));
            Assert.That(references, Does.Contain(("Microsoft.Extensions.DependencyModel", "9.0.9")));
            Assert.That(references, Does.Contain(("Silk.NET.Core", "2.23.0")));
            Assert.That(references, Does.Contain(("YamlDotNet", "16.3.0")));
            Assert.That(references.Select(reference => reference.name), Does.Not.Contain("Microsoft.Build"));
            Assert.That(references.Select(reference => reference.name), Does.Not.Contain("OpenVR.NET"));
        });
    }
}
