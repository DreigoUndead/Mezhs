using Mezhs.Log.Shared;
using Mezhs.Log.Sql;

var shared = new LogShared();
return new Commands(new LogSql(shared), shared).Run();
