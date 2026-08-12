using BenchmarkDotNet.Running;

BenchmarkRunner.Run<SimpleMappingBenchmark>();
BenchmarkRunner.Run<CollectionMappingBenchmark>();
BenchmarkRunner.Run<NestedMappingBenchmark>();
BenchmarkRunner.Run<ProjectionBenchmark>();