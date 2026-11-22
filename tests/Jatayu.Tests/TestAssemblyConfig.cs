// ==================================================================================================
// SPRINT 3a/5a: Test Assembly Configuration
// Disables parallel execution across test classes to protect AllocationTracker and singleton profiler state.
// ==================================================================================================

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
