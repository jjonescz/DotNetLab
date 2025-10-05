using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DotNetLab.UnitTests")]

// WorkerApi contains source-generated JSON serialization which needs access to some internal setters in ICompiler models.
[assembly: InternalsVisibleTo("DotNetLab.WorkerApi")]
