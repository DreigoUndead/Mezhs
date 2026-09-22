using Mezhs.Log.Data;
using Mezhs.Log.Shared;

var shared = new LogShared();
return new Commands(new LogData(shared), shared).Run();
