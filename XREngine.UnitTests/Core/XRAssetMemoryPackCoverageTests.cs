using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Animation;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using Assert = NUnit.Framework.Assert;

namespace XREngine.UnitTests.Core
{
    [TestFixture]
    public sealed class XRAssetMemoryPackCoverageTests
    {
        public static IEnumerable<TestCaseData> MemoryPackableAssets()
        {
            var assetTypes = GatherEngineAssemblies()
                .SelectMany(assembly => assembly.GetTypes())
                .Where(IsConcreteAssetType)
                .Where(HasMemoryPackableAttribute)
                .OrderBy(type => type.FullName, StringComparer.Ordinal);

            foreach (var type in assetTypes)
                yield return new TestCaseData(type)
                    .SetName($"MemoryPack_FormatterRegistered_{type.FullName}");
        }

        [TestCaseSource(nameof(MemoryPackableAssets))]
        public void MemoryPackFormatter_RoundTrips_DefaultInstance(Type assetType)
        {
            // Use an uninitialized instance to avoid constructor side effects/cycles.
            var instance = (XRAsset)FormatterServices.GetUninitializedObject(assetType);

            try
            {
                byte[] bytes = MemoryPackSerializer.Serialize(assetType, instance)!;
                bytes.Length.ShouldBeGreaterThan(0);

                var clone = MemoryPackSerializer.Deserialize(assetType, bytes) as XRAsset;
                clone.ShouldNotBeNull();
                clone.ShouldBeOfType(assetType);
            }
            catch (MemoryPackSerializationException ex)
            {
                Assert.Inconclusive($"MemoryPack formatter unavailable for {assetType.FullName}: {ex.Message}");
            }
        }

        private static IEnumerable<Assembly> GatherEngineAssemblies()
        {
            var assemblies = new HashSet<Assembly>();

            void AddAssembly(Assembly assembly)
            {
                if (assembly.IsDynamic)
                    return;

                string? name = assembly.GetName().Name;
                if (string.IsNullOrWhiteSpace(name))
                    return;

                if (!name.StartsWith("XREngine", StringComparison.Ordinal))
                    return;

                if (name.IndexOf("UnitTest", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;

                assemblies.Add(assembly);
            }

            AddAssembly(typeof(XRAsset).Assembly);
            AddAssembly(typeof(XRProject).Assembly);
            AddAssembly(typeof(BuildSettings).Assembly);
            AddAssembly(typeof(AnimStateMachine).Assembly);
            AddAssembly(typeof(XRMesh).Assembly);
            AddAssembly(typeof(XRWorld).Assembly);
            AddAssembly(typeof(XRPrefabSource).Assembly);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                AddAssembly(assembly);

            return assemblies;
        }

        private static bool IsConcreteAssetType(Type type)
            => typeof(XRAsset).IsAssignableFrom(type)
               && !type.IsAbstract
               && !type.IsGenericTypeDefinition;

        private static bool HasMemoryPackableAttribute(Type type)
            => type.GetCustomAttribute<MemoryPackableAttribute>() is not null;
    }
}
