// The tests share Docker, pen:latest and the host's ports, so they run one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

// The pen-tools images the run builds are shared by every class and removed once, at the end.
[assembly: AssemblyFixture(typeof(Pen.SmokeTests.Harness.ToolsImageSweep))]
