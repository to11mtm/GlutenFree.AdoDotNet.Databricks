using Xunit;

// Same reason as the REST integration project: every suite shares one Delta table, and
// Databricks' optimistic concurrency fails a save that overlaps another suite's teardown DELETE.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
