namespace SubathonManager.Tests;

[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialCollectionDefinition
{
}

[CollectionDefinition("SequentialParallel", DisableParallelization = false)]
public class SequentialParallelCollectionDefinition
{
}