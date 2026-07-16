using Xunit;

// IntegrationTests boots Program in-process, and Program intentionally applies
// process-wide MIDTERM_* environment variables. Keep this small integration
// assembly serial so environment-contract tests cannot race the app fixture.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
