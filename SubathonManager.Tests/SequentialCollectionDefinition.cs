namespace SubathonManager.Tests;

[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialCollectionDefinition
{
}

[CollectionDefinition("SequentialParallel", DisableParallelization = false)]
public class SequentialParallelCollectionDefinition
{
}

[CollectionDefinition("SharedEventBusTests", DisableParallelization = true)] // slowdown but might fix the eventbus issue in websocket consumers
public class SharedEventBusTestsCollection { }

[CollectionDefinition("NonParallel", DisableParallelization = true)]
public class NonParallelCollection { }