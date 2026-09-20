namespace SubathonManager.Tests;

[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialCollectionDefinition {
}

[CollectionDefinition("SequentialParallel", DisableParallelization = true)]
public class SequentialParallelCollectionDefinition {
}

[CollectionDefinition("GlobalState", DisableParallelization = true)]
public class GlobalStateCollection {
}

[CollectionDefinition("ServicesTests", DisableParallelization = true)]
public class ServicesTestsCollection {
}

[CollectionDefinition("CurrencyServiceTests", DisableParallelization = true)]
public class CurrencyServiceTestsCollection {
}

[CollectionDefinition("WorkingDirectory", DisableParallelization = true)]
public class WorkingDirectoryCollection {
}