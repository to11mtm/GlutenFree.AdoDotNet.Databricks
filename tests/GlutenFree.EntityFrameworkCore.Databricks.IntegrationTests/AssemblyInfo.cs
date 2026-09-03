using Xunit;

// These suites all read and write one shared Delta table. Databricks uses optimistic
// concurrency, and an interactive transaction takes a table-level read scope, so a save running
// concurrently with another suite's teardown DELETE fails with DELTA_CONCURRENT_DELETE_READ.
// The tests are network-bound anyway, so putting every class in one collection (which
// serializes them) costs little and removes the flake.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
