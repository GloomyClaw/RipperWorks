using System.Runtime.CompilerServices;

// RF-05: test-only seams (TestOnly*, hash observers) stay internal.
// Not a ProjectReference to the test assembly — friend access only.
[assembly: InternalsVisibleTo("RipperWorks.Tests")]
// RF-06 crash-child harness (tools/Rf06CrashChild) needs TestOnlyBarrier.
[assembly: InternalsVisibleTo("Rf06CrashChild")]
