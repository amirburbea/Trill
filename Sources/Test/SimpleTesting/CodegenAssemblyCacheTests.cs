using System;
using System.IO;
using System.Reflection;
using Microsoft.StreamProcessing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SimpleTesting
{
    /// <summary>
    /// Disk cache settings are applied with <see cref="ConfigModifier"/> inside a <c>using</c>; dispose restores prior
    /// <see cref="Config.CodegenAssemblyCachePath"/> and <see cref="Config.CodegenOptions.GenerateDebugInfo"/> so other tests see default Trill behavior.
    /// <see cref="TestCleanup"/> clears the cache path and counters in case a future test forgets the modifier.
    /// </summary>
    [TestClass]
    public sealed class CodegenAssemblyCacheTests
    {
        private const string ValidSource =
            "namespace Microsoft.StreamProcessing { public static class CodegenCacheProbe { public static int F() => 42; } }";

        [TestCleanup]
        public void TestCleanup()
        {
            Config.CodegenAssemblyCachePath = null;
            Transformer.CodegenAssemblyCacheHits = 0;
            Transformer.CodegenAssemblyCacheMisses = 0;
        }

        [TestMethod]
        public void ConfigModifier_restores_codegen_cache_path_and_generate_debug_info()
        {
            string priorPath = Config.CodegenAssemblyCachePath;
            bool priorGenerateDebugInfo = Config.CodegenOptions.GenerateDebugInfo;
            string dir = Path.Combine(Path.GetTempPath(), "TrillCodegenRestore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (new ConfigModifier().CodegenAssemblyCachePath(dir).GenerateDebugInfo(false).Modify())
                {
                    Assert.AreEqual(dir, Config.CodegenAssemblyCachePath);
                    Assert.IsFalse(Config.CodegenOptions.GenerateDebugInfo);
                }

                Assert.AreEqual(priorPath, Config.CodegenAssemblyCachePath);
                Assert.AreEqual(priorGenerateDebugInfo, Config.CodegenOptions.GenerateDebugInfo);
            }
            finally
            {
                TryDeleteDirectory(dir);
            }
        }

        [TestMethod]
        public void Second_compile_with_same_source_hits_disk_cache()
        {
            string dir = Path.Combine(Path.GetTempPath(), "TrillCodegenTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (new ConfigModifier().CodegenAssemblyCachePath(dir).GenerateDebugInfo(false).Modify())
                {
                    Transformer.CodegenAssemblyCacheHits = 0;
                    Transformer.CodegenAssemblyCacheMisses = 0;

                    Assembly a1 = Transformer.CompileSourceCode(ValidSource, Array.Empty<Assembly>(), out string err1);
                    Assert.IsNotNull(a1, err1);
                    Assert.IsTrue(string.IsNullOrEmpty(err1), err1);
                    Assert.IsTrue(Transformer.CodegenAssemblyCacheMisses >= 1, "first compile should emit");
                    long hitsAfterFirst = Transformer.CodegenAssemblyCacheHits;

                    Assembly a2 = Transformer.CompileSourceCode(ValidSource, Array.Empty<Assembly>(), out string err2);
                    Assert.IsNotNull(a2, err2);
                    Assert.IsTrue(string.IsNullOrEmpty(err2), err2);
                    Assert.IsTrue(Transformer.CodegenAssemblyCacheHits > hitsAfterFirst, "second compile should load from disk");
                }
            }
            finally
            {
                TryDeleteDirectory(dir);
            }
        }

        [TestMethod]
        public void Second_compile_with_same_source_hits_disk_cache_when_generate_debug_info_enabled()
        {
            string dir = Path.Combine(Path.GetTempPath(), "TrillCodegenDebug_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (new ConfigModifier().CodegenAssemblyCachePath(dir).GenerateDebugInfo(true).Modify())
                {
                    Transformer.CodegenAssemblyCacheHits = 0;
                    Transformer.CodegenAssemblyCacheMisses = 0;

                    Assembly a1 = Transformer.CompileSourceCode(ValidSource, Array.Empty<Assembly>(), out string err1);
                    Assert.IsNotNull(a1, err1);
                    Assert.IsTrue(string.IsNullOrEmpty(err1), err1);
                    Assert.IsTrue(Transformer.CodegenAssemblyCacheMisses >= 1, "first compile should emit");
                    long hitsAfterFirst = Transformer.CodegenAssemblyCacheHits;

                    Assembly a2 = Transformer.CompileSourceCode(ValidSource, Array.Empty<Assembly>(), out string err2);
                    Assert.IsNotNull(a2, err2);
                    Assert.IsTrue(string.IsNullOrEmpty(err2), err2);
                    Assert.IsTrue(Transformer.CodegenAssemblyCacheHits > hitsAfterFirst, "second compile should load from disk");
                }
            }
            finally
            {
                TryDeleteDirectory(dir);
            }
        }

        [TestMethod]
        public void Different_source_produces_distinct_cache_files()
        {
            string dir = Path.Combine(Path.GetTempPath(), "TrillCodegenTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (new ConfigModifier().CodegenAssemblyCachePath(dir).GenerateDebugInfo(false).Modify())
                {
                    const string srcA =
                        "namespace Microsoft.StreamProcessing { public static class A { public static int F() => 1; } }";
                    const string srcB =
                        "namespace Microsoft.StreamProcessing { public static class B { public static int F() => 2; } }";

                    Transformer.CompileSourceCode(srcA, Array.Empty<Assembly>(), out string errA);
                    Assert.IsTrue(string.IsNullOrEmpty(errA), errA);
                    int countAfterA = Directory.GetFiles(dir, "*.dll").Length;

                    Transformer.CompileSourceCode(srcB, Array.Empty<Assembly>(), out string errB);
                    Assert.IsTrue(string.IsNullOrEmpty(errB), errB);
                    int countAfterB = Directory.GetFiles(dir, "*.dll").Length;

                    Assert.IsTrue(countAfterB > countAfterA, "second distinct source should add another cached assembly");
                }
            }
            finally
            {
                TryDeleteDirectory(dir);
            }
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
