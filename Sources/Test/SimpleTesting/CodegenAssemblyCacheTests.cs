using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

        private static string UniqueProbeSource(string tag)
            => $"namespace Microsoft.StreamProcessing {{ public static class CodegenCacheProbe_{tag} {{ public static int F() => 42; }} }}";

        private static string UniqueProbeTypeName(string tag) => $"Microsoft.StreamProcessing.CodegenCacheProbe_{tag}";

        private static Assembly CompileForTest(string source, string typeFullName, out string errorMessages)
        {
            Type resolved = Transformer.CompileSourceCode(source, Array.Empty<Assembly>(), a => a.GetType(typeFullName), out errorMessages);
            return resolved?.Assembly;
        }

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

                    Assembly a1 = CompileForTest(ValidSource, "Microsoft.StreamProcessing.CodegenCacheProbe", out string err1);
                    Assert.IsNotNull(a1, err1);
                    Assert.IsTrue(string.IsNullOrEmpty(err1), err1);
                    Assert.IsTrue(Transformer.CodegenAssemblyCacheMisses >= 1, "first compile should emit");
                    long hitsAfterFirst = Transformer.CodegenAssemblyCacheHits;

                    Assembly a2 = CompileForTest(ValidSource, "Microsoft.StreamProcessing.CodegenCacheProbe", out string err2);
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

                    Assembly a1 = CompileForTest(ValidSource, "Microsoft.StreamProcessing.CodegenCacheProbe", out string err1);
                    Assert.IsNotNull(a1, err1);
                    Assert.IsTrue(string.IsNullOrEmpty(err1), err1);
                    Assert.IsTrue(Transformer.CodegenAssemblyCacheMisses >= 1, "first compile should emit");
                    long hitsAfterFirst = Transformer.CodegenAssemblyCacheHits;

                    Assembly a2 = CompileForTest(ValidSource, "Microsoft.StreamProcessing.CodegenCacheProbe", out string err2);
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
        public void Corrupt_preseeded_disk_cache_entry_is_replaced_by_fresh_emit()
        {
            string tag = Guid.NewGuid().ToString("N");
            string source = UniqueProbeSource(tag);
            string typeName = UniqueProbeTypeName(tag);

            string dir = Path.Combine(Path.GetTempPath(), "TrillCodegenCorrupt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (new ConfigModifier().CodegenAssemblyCachePath(dir).GenerateDebugInfo(false).Modify())
                {
                    Transformer.PrepareCompileSourceCodeFingerprintInputs(
                        source,
                        Array.Empty<Assembly>(),
                        includeIgnoreAccessChecksAssembly: true,
                        includeDebugInfo: false,
                        out List<MetadataReference> refs,
                        out CSharpCompilationOptions options,
                        out SyntaxTree tree);
                    Assert.IsTrue(
                        Transformer.TryComputeFingerprint(
                            tree.GetRoot().ToFullString(),
                            tree.Options,
                            refs,
                            options,
                            includeIgnoreAccessChecksAssembly: true,
                            emitPortablePdb: false,
                            out _,
                            out string fingerprint),
                        "fingerprint inputs should match CompileSourceCode (debug off)");

                    string cacheDll = Path.Combine(dir, fingerprint + ".dll");
                    File.WriteAllBytes(cacheDll, Encoding.UTF8.GetBytes("not a PE – corrupt cache seed"));

                    Assembly asm = CompileForTest(source, typeName, out string err);
                    Assert.IsNotNull(asm, err);
                    Assert.IsTrue(string.IsNullOrEmpty(err), err);

                    byte[] pe = File.ReadAllBytes(cacheDll);
                    Assert.IsTrue(pe.Length > 64, "emit should overwrite corrupt file with a real assembly");
                    Assert.AreEqual((byte)'M', pe[0]);
                    Assert.AreEqual((byte)'Z', pe[1]);
                }
            }
            finally
            {
                TryDeleteDirectory(dir);
            }
        }

        [TestMethod]
        public void TypeLoadException_from_resolveType_on_cached_assembly_triggers_retry_emit()
        {
            string tag = Guid.NewGuid().ToString("N");
            string source = UniqueProbeSource(tag);
            string typeName = UniqueProbeTypeName(tag);

            string dir = Path.Combine(Path.GetTempPath(), "TrillCodegenRetry_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                using (new ConfigModifier().CodegenAssemblyCachePath(dir).GenerateDebugInfo(false).Modify())
                {
                    Assembly seed = CompileForTest(source, typeName, out string seedErr);
                    Assert.IsNotNull(seed, seedErr);
                    Assert.IsTrue(string.IsNullOrEmpty(seedErr), seedErr);

                    int resolveInvocations = 0;
                    Type resolved = Transformer.CompileSourceCode(
                        source,
                        Array.Empty<Assembly>(),
                        a =>
                        {
                            resolveInvocations++;
                            if (resolveInvocations == 1)
                            {
                                throw new TypeLoadException("simulated: resolveType fails on assembly loaded from disk cache");
                            }

                            return a.GetType(typeName);
                        },
                        out string err,
                        includeIgnoreAccessChecksAssembly: true);

                    Assert.IsNotNull(resolved, err);
                    Assert.AreEqual(2, resolveInvocations, "resolveType should run once for cached load and again after cache discard + emit");
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

                    CompileForTest(srcA, "Microsoft.StreamProcessing.A", out string errA);
                    Assert.IsTrue(string.IsNullOrEmpty(errA), errA);
                    int countAfterA = Directory.GetFiles(dir, "*.dll").Length;

                    CompileForTest(srcB, "Microsoft.StreamProcessing.B", out string errB);
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
