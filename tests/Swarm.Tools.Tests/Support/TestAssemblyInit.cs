using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Swarm.Tools.Tests.Support;

/// <summary>Process-wide setup for the test assembly.</summary>
internal static class TestAssemblyInit
{
    /// <summary>
    /// Raises the thread-pool minimum. xUnit starts many blocking tests (process waits, sleeps) at once, and the
    /// pool's slow thread injection then delays timer callbacks (slot heartbeats) and output-reader callbacks by
    /// seconds, which made timing tests flaky.
    /// </summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "Test assembly: process-wide pool sizing must happen before any test runs.")]
    internal static void Init()
    {
        var threads = Math.Max(64, Environment.ProcessorCount * 4);
        ThreadPool.SetMinThreads(threads, threads);
    }
}
