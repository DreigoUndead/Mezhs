# MEŽS Executor Manifesto

## 1. Purpose

MEŽS needs one durable execution mechanism for host shell/process work that survives the lifetime of the caller that requested it.

The immediate driver is the Agent system:

- Agent shell execution must not depend on the Agent API process staying alive;
- the Agent must be able to restart its own API process;
- the Agent UI must be able to inspect, kill and restart executions;
- shell execution history must remain available after API crashes/restarts;
- execution state must have one source of truth rather than being duplicated in Agent API memory/storage and a separate runner;
- the implementation must remain simple, auditable and independent from Agent business logic.

The chosen component is **`Mezhs.Executor`**.

`Mezhs.Executor` is a CLI application, not an HTTP API and not a long-lived central supervisor.

It stores durable execution state in SQLite through the shared LogSql/SQLite foundation, and uses short-lived CLI/controller invocations plus independently running execution-owner processes.

The design intentionally avoids a central daemon. A central daemon would itself become a single point of failure and would need its own lifetime, discovery, recovery and control protocol.

The core model is instead:

```text
caller
  |
  | Execute(...)
  v
Mezhs.Executor
  |
  +-- create durable execution row
  +-- spawn independent Mezhs.Executor Run <id>
  +-- return database PK immediately

caller may now exit or crash

Mezhs.Executor Run <id>
  |
  +-- atomically claim execution
  +-- start host shell/process
  +-- heartbeat
  +-- observe requested state changes
  +-- enforce timeout
  +-- persist result
  +-- exit

future caller
  |
  +-- Get / Wait / Kill / Restart / List
       against the same durable SQL record
```

The system should be understandable from its database rows and process behavior without requiring a hidden in-memory coordinator.

---

## 2. Core Invariant

The foundational invariant is:

> **One durable execution row represents one actual detached shell/process execution, and at most one `Run` process may own that execution at a time.**

The execution row is the durable source of truth.

The running `Mezhs.Executor Run <id>` process is the temporary runtime owner of that row.

The caller that requested the execution is not the owner of its lifetime.

This means:

- the caller may exit immediately after `Execute` returns;
- the Agent API may crash after starting a shell execution;
- a different process may later query the execution;
- there is no central supervisor process whose crash invalidates unrelated executions;
- unrelated executions are isolated from one another;
- one execution-owner crash affects one execution, not the entire execution subsystem.

---

## 3. What Counts as an Execution

An Executor execution is a **real host shell/process execution**.

It is not a high-level Agent conversation turn.

It is not an Agent reasoning cycle.

It is not a chat message.

For the Agent command protocol:

```text
<SH>
command 1
command 2
command 3
</SH>
```

is one Executor execution.

If an Agent response contains ten separate `<SH>...</SH>` blocks, those are ten independent Executor executions.

The Executor must not split one SH block into separate execution records per line.

The shell itself owns the semantics inside the block.

In particular, on Windows `cmd.exe` does not automatically stop a multiline block merely because an earlier command returns a non-zero exit code. If fail-fast behavior is desired, the shell text itself must express it (`&&`, `exit /b`, etc.) or shell semantics must be redesigned separately. Executor must not silently invent per-line control flow.

This distinction is important:

> **Executor records actual shell/process invocations, not conceptual Agent work.**

Agent conversation progress should be reconstructed from durable chat/messages plus the execution records referenced by those messages.

---

## 4. Independence and Dependency Direction

Executor should remain as independent from Agent/API business logic as practical.

The intended dependency direction is:

```text
Mezhs.Agent.Api
      |
      +------> Mezhs.Executor
      |
      +------> shared LogSql / SQLite foundation

Mezhs.Executor
      |
      +------> Mezhs.Console
      |
      +------> shared LogSql / SQLite foundation
      |
      +------> OS process APIs

Mezhs.Executor  -X->  Mezhs.Agent.Api
```

The important rule is:

> **The parent/consumer may know the child/tool; the child/tool must not know the parent application.**

Agent API is allowed to reference/use public Executor types.

Executor must not reference Agent API, Agent models, Agent services, Agent policies or Agent Web.

Executor may receive MEŽS execution context through environment variables because environment is part of the execution boundary, not an Agent API dependency.

Executor should remain usable directly by a human from a terminal.

---

## 5. Executor Is a CLI Application, Not an API

`Mezhs.Executor` should use the existing `Mezhs.Console` command framework where doing so remains natural.

It is intentionally a CLI application.

Its human-facing output should therefore be readable by a human first and machine-parseable second.

Do not introduce an HTTP server, local REST API, named-pipe API, daemon discovery protocol or service-control socket merely to query execution state.

SQLite is the durable coordination mechanism.

A caller can always launch the CLI and ask it for the current state.

---

## 6. Proposed Command Surface

The first implementation should remain small.

The intended public behavior is approximately:

```csharp
int Execute(
    string command,
    string? directory = null,
    int timeoutSeconds = 86400);

Execution Get(int id);

IReadOnlyList<Execution> List(
    string? chatId = null,
    int limit = 200);

Execution Wait(
    int id,
    int timeoutSeconds = 60);

Execution Kill(int id);

int Restart(int id);
```

There is also an internal/runtime command used by the independently spawned process:

```csharp
void Run(int id);
```

The exact internal command name (`Run`, `ExecuteAndWait`, etc.) is implementation detail. Its contract is what matters:

- it accepts an execution ID only;
- it does not accept arbitrary command text;
- it loads the execution definition from durable storage;
- it atomically claims the execution;
- it owns that execution until terminal state.

### Why command text is not passed to `Run`

This is deliberate.

The detached process should be launched as something conceptually equivalent to:

```text
Mezhs.Executor Run 123
```

not:

```text
Mezhs.Executor Run "some very complicated quoted shell command ..."
```

The execution definition already exists in SQL before the detached process starts.

This removes quoting differences from the detachment boundary and makes the durable row authoritative.

---

## 7. `Execute` Semantics

`Execute` is the asynchronous entry point.

Conceptually:

```text
Execute(command, directory, timeout)

1. validate request
2. resolve effective working directory
3. snapshot relevant execution context/environment
4. INSERT execution row
5. obtain SQLite-generated integer PK
6. spawn independent Executor runtime process:
       Mezhs.Executor Run <id>
7. return <id>
```

The integer primary key generated by SQLite **is the execution ID**.

Do not generate a GUID or separate execution identifier.

Example:

```text
> Mezhs.Executor Execute "dotnet build"
381
```

The command returns after the independent runtime process has been successfully started.

It does not wait for the shell command to finish.

If spawning the runtime process fails after the row has been created, the row must be moved to an appropriate terminal failure state rather than being left falsely runnable forever.

### Working directory

If `directory` is null, `Execute` should resolve the caller's current working directory immediately and persist the resolved value.

The later `Run` process must use the stored value rather than inheriting/guessing its own working directory.

### Execution timeout

The default execution timeout should be deliberately large.

The timeout exists to prevent accidentally unbounded executions from becoming permanent simply because nothing ever checks them.

At the same time, intentionally long-lived services may need an explicit way to run without a finite timeout. The implementation may use a clearly documented sentinel such as `timeoutSeconds = 0` for no timeout, but the default should remain a large finite safeguard.

Do not confuse execution timeout with `Wait` timeout.

---

## 8. `Get` Semantics

`Get(id)` returns the durable execution record.

It also performs lazy stale-state reconciliation.

If an execution says it is active but its heartbeat is stale beyond the configured stale threshold, the caller may atomically transition it to `Dead` and return the reconciled record.

There is no background watchdog.

If nobody ever asks about an abandoned execution, nobody needs to spend resources cleaning it up.

This is intentional:

> **Stale state is reconciled on observation.**

The transition must be conditional in SQL so a concurrently refreshed heartbeat cannot be overwritten by a stale reader.

Conceptually:

```sql
UPDATE Executions
SET Status = 'Dead'
WHERE Id = @id
  AND Status = 'Running'
  AND HeartbeatAt < @cutoff;
```

If zero rows are updated, reread the row because the runner may have updated it concurrently.

---

## 9. `List` Semantics

The Agent UI needs execution history, so Executor must support listing durable executions.

The initial command may support an optional MEŽS chat context filter:

```csharp
IReadOnlyList<Execution> List(string? chatId = null, int limit = 200);
```

This does not make Executor dependent on Agent API.

`MEZHS_CHAT_ID` is existing generic MEŽS execution context and is useful for audit/history correlation.

`List` should return newest-first unless existing repository conventions strongly justify another ordering.

Active rows returned by `List` should be eligible for the same lazy stale-heartbeat reconciliation as `Get` so the UI does not indefinitely display abandoned work as running.

The limit must be explicit and bounded.

---

## 10. `Wait` Semantics

`Wait(id, timeoutSeconds)` is convenience for synchronous consumers.

It repeatedly observes the durable execution state until either:

- the execution reaches a terminal state; or
- the caller's wait timeout expires.

The important distinction is:

```text
Execution timeout
    controls how long the actual shell/process is allowed to run.

Wait timeout
    controls how long this particular caller is willing to wait.
```

A `Wait` timeout must **not** kill the execution.

If the wait timeout expires, `Wait` returns the latest execution state.

A different caller can continue observing it later.

---

## 11. Heartbeat

A running execution owner should update its heartbeat approximately once per second.

Heartbeat work must be independent of command progress.

A shell command that is blocked, sleeping or producing no output must still have a healthy heartbeat as long as the Executor runtime process itself is healthy.

Because `Mezhs.Console` command methods are synchronous, `Run` may internally use a dedicated thread, timer or equivalent background mechanism while the command method blocks waiting for the child process.

The implementation mechanism is secondary to the invariant:

> **Heartbeat must continue independently while the owned process is alive.**

The stale threshold should be comfortably larger than the heartbeat period to tolerate scheduling stalls, SQLite contention and transient machine load.

A value in the rough range of 5-10 seconds is reasonable for initial implementation evidence.

Do not introduce a separate heartbeat service/watchdog.

---

## 12. Execution State Model

Keep the state machine small.

A reasonable initial model is:

```text
Created
Running
KillRequested
Completed
Failed
Killed
TimedOut
Dead
```

Meaning:

### `Created`

The durable row exists but has not yet been successfully claimed by its runtime owner.

### `Running`

A `Run` process atomically claimed the row and is responsible for the owned shell/process.

### `KillRequested`

A caller requested termination. The running owner must observe this and terminate its owned process tree.

### `Completed`

The owned process exited successfully according to its exit code semantics.

### `Failed`

Execution infrastructure failed, or the owned process exited unsuccessfully.

The shell exit code must remain available separately.

### `Killed`

The owner terminated the process because a kill request was observed.

### `TimedOut`

The owner terminated the process because the configured execution timeout expired.

### `Dead`

A later observer found stale active state/heartbeat and cannot prove a clean terminal transition.

`Dead` is detection of lost ownership, not proof that every descendant process disappeared.

Do not add extra states unless they encode a real externally meaningful lifecycle distinction.

---

## 13. SQLite Owns Concurrency

No in-memory mutex, singleton daemon lock or cross-process .NET synchronization mechanism should be added merely to coordinate execution ownership.

SQLite already provides the required transactional serialization.

The code must use it correctly.

This is unsafe:

```text
SELECT Status
-> Created

later...
UPDATE Status = Running
```

Two processes could both read `Created` before either writes.

Instead ownership must be claimed atomically:

```sql
UPDATE Executions
SET Status = 'Running',
    StartedAt = @now,
    HeartbeatAt = @now
WHERE Id = @id
  AND Status = 'Created'
RETURNING *;
```

Exactly one `Run` process may receive the claimed row.

Any other `Run <same id>` invocation receives no row and must not execute the command.

The same principle applies to:

- stale-to-dead transitions;
- kill requests;
- terminal completion;
- restart lineage creation;
- recovery/idempotency constraints.

> **SQLite is the concurrency mechanism; conditional SQL transitions are how the application uses it.**

---

## 14. Kill Semantics

`Kill(id)` should change durable state; it should not depend on direct IPC to the running process.

For an active execution:

```text
Running
   |
   | Kill(id)
   v
KillRequested
   |
   | owner observes status on heartbeat cycle
   v
terminate owned process tree
   |
   v
Killed
```

The execution owner already wakes periodically for heartbeat, so observing requested state changes there keeps the architecture small.

There is no need for:

- named-pipe cancellation;
- HTTP callbacks;
- process discovery by executable name;
- a global supervisor.

If `Kill` is requested while the row is still `Created`, implementation may terminally mark it killed before it ever starts, as long as `Run` cannot subsequently claim it.

If the execution is already terminal, `Kill` should return the existing terminal record rather than inventing a new lifecycle.

---

## 15. Restart Semantics

Restart always means **a new execution**.

Never reset/reuse the old row.

The old execution keeps its original PK and full history.

The new execution receives a new SQLite PK.

Example:

```text
Restart(381)

old execution: 381
new execution: 382
```

The new row clones the execution definition required to run again, including at least:

- command;
- working directory;
- execution timeout;
- relevant stored execution context/environment.

The new row should retain restart lineage, e.g. `RestartedFromId = 381`, if the exact schema remains simple.

`Restart` returns the **new execution ID**.

### Restart race semantics

A natural completion race should not make restart meaningless.

If execution `381` finishes normally at the same moment somebody requests `Restart(381)`, the restart request still means "run this again" and should create the new execution.

### Self-restart constraint

Self-restart deserves special care.

The primary use case includes Agent API restarting itself.

A naïve implementation can fail like this:

```text
Agent.Api process
   |
   +-- starts Executor Restart helper

old Executor owner kills Agent.Api process tree
   |
   +-- accidentally kills the Restart helper/replacement as a descendant
```

Therefore restart must not rely on a helper that belongs to the very child process tree being terminated.

The surviving execution owner (`Run` process) is the natural handoff point because it exists outside the owned child shell/process.

A robust implementation may therefore use this pattern:

```text
Restart(oldId)

1. create replacement row/newId transactionally
2. request termination of old execution
3. old execution owner observes the request
4. old owner terminates its child
5. surviving old owner starts independent Run <newId>
6. old owner finalizes old execution and exits
```

If the old execution is already terminal/dead, the `Restart` caller may start `Run <newId>` directly.

The exact implementation may differ, but the invariant is mandatory:

> **Restarting a process must not depend on a process that will be killed as part of the restart.**

This is especially important for Agent API self-restart.

---

## 16. Timeout Semantics

The execution owner enforces the configured execution timeout.

When the timeout expires:

1. terminate the owned process tree;
2. capture whatever output can safely be captured;
3. store timeout error/evidence;
4. transition to `TimedOut`;
5. stop heartbeating and exit.

Timeout is an execution-owner responsibility.

No external watchdog is required.

For ordinary Agent SH work, Agent policy/runtime may choose a tighter timeout than the Executor default.

For intentionally long-lived service processes, an explicit no-timeout mode may be supported rather than forcing every service to die after an arbitrary number of hours/days.

The no-timeout mode must be explicit, not accidental.

---

## 17. Process Ownership and Detachment

The detached execution runtime must survive the requesting caller exiting.

This is the original problem this component exists to solve.

Acceptance must therefore be demonstrated, not assumed.

Required behavior:

```text
process A calls Execute
process A receives id
process A exits completely

Run <id> and its owned child continue
```

Do not solve this with ad-hoc shell detachment such as:

- `start /b`;
- PowerShell background tricks;
- Task Scheduler as a launcher;
- arbitrary wrapper scripts.

Use direct process APIs.

Windows is the first concrete host that must work correctly because it is the current MEŽS host environment.

Cross-platform behavior may share abstractions where natural, but do not pretend Unix daemonization is solved if it has not been tested.

The exact process-creation flags should be chosen from evidence gathered by tests.

Do not add native Windows complexity merely because it exists. Add it only when the caller-exit/self-restart acceptance tests demonstrate the ordinary process API is insufficient.

---

## 18. Owned Child Process

`Run(id)` should start the actual host shell/process and remain alive while that child is alive.

The execution owner is responsible for:

- child start;
- working directory;
- environment/context;
- stdout/stderr capture;
- heartbeat;
- timeout;
- kill/restart observation;
- process-tree termination where required;
- final exit code/result persistence.

The owner must not start the child and immediately exit, leaving an unmanaged orphan.

A durable row plus a living owner is what makes execution understandable.

---

## 19. Shell Behavior

Executor becomes the single host shell mechanism used by Agent API.

The existing shell behavior should be moved/reused rather than duplicated.

On Windows this currently means using the configured host command processor (`ComSpec` / `cmd.exe`) with the existing UTF-8 handling.

On non-Windows, current behavior uses `/bin/sh`.

The migration should preserve tested shell semantics unless an intentional shell redesign is separately approved.

Executor should capture stdout and stderr and retain exit code.

The returned execution record should make those results available in a stable way suitable for both terminal use and Agent UI projection.

---

## 20. Policy Boundary

Executor does **not** enforce Agent policy.

Agent policy is an Agent API responsibility.

The required path is:

```text
model emits SH
      |
      v
Agent command interpreter
      |
      v
policy validates actual SH body
      |
      v
Mezhs.Executor Execute(...)
      |
      v
host shell
```

After migration, Agent API must not retain a second arbitrary direct `cmd.exe`, PowerShell or `/bin/sh` execution path.

This must be audited in code rather than treated as convention.

The invariant is:

> **There is one Agent path from an SH request to an OS shell, and policy validation occurs before Executor receives the approved command.**

A human directly invoking `Mezhs.Executor` from a terminal is not an Agent policy bypass. A human with OS permission can already run the host shell directly.

Agent policy constrains Agent capability; it is not a replacement for OS security.

Executor must not accept or trust fake values such as `policyApproved=true`.

---

## 21. Execution Context and Environment

MEŽS already propagates execution context through environment variables.

Executor should preserve this model.

Existing context includes values such as:

```text
MEZHS_EXECUTION_ID
MEZHS_PARENT_EXECUTION_ID
MEZHS_CHAT_ID
MEZHS_CORRELATION_ID
MEZHS_SOURCE
MEZHS_WORKSPACE
```

Agent execution may require additional durable correlation context such as the assistant message ID and executable command index so recovery can reconnect an SH block to its Executor row even if Agent API dies immediately after creation.

If new variables are needed, prefer explicit MEŽS-prefixed context variables rather than adding Agent-specific command-line parameters to `Execute`.

Executor should treat context values as execution metadata, not as business behavior.

After the Executor row receives its integer PK, the child execution environment should expose the current Executor execution ID through `MEZHS_EXECUTION_ID`.

Relevant inherited context must be persisted before detachment where it is required for:

- audit/history;
- restart;
- Agent recovery;
- downstream Console commands.

Do not casually dump every OS environment variable into human-facing logs. Environment may contain secrets.

Runtime storage is local sensitive state and must remain ignored by Git.

---

## 22. Shared LogSql / SQLite Foundation

Executor must use the shared LogSql/SQLite foundation for durable storage.

Do not create yet another independent SQLite owner inside Executor.

The storage boundary should keep generic database concerns separate from application semantics:

```text
shared LogSql / SQLite foundation
    path ownership
    connection opening
    WAL / busy timeout
    transactions
    migrations/schema mechanics
    generic SQL execution helpers

Mezhs.Executor
    execution schema
    execution state transitions
    execution queries
    process semantics
```

If the current LogSql code is not yet cleanly reusable from `main`, the implementation should extract the reusable storage mechanics required by Executor rather than copying stale branch code wholesale.

This is consistent with the existing TODO for a common SQLite/LogSql storage foundation.

Executor storage should use WAL and a reasonable busy timeout so heartbeat/read/control operations can coexist safely.

---

## 23. Executor Is the Source of Truth for Agent Shell Execution History

Once migrated, Agent API must stop maintaining a second shell execution table/state machine.

Today the Agent API stores execution records itself and the Agent Web reads those records for execution history.

That ownership must move.

The intended model becomes:

```text
chat/messages
    = conversation truth

Executor SQL execution log
    = host shell/process execution truth

Agent API
    = policy/orchestration + projection of those durable sources

Agent Web
    = UI projection through Agent API
```

The Agent UI should not display shell state from an AgentStore copy that can disagree with Executor.

If the UI says execution `381` is running, that claim should ultimately come from Executor's durable row.

If it says execution `381` exited with code 1, that evidence should come from Executor's durable row.

Agent API may map Executor records into an API-specific view, but it must not recreate a second lifecycle owner.

---

## 24. Agent Recovery After API Crash/Restart

The Executor design is only valuable to the Agent if Agent API can recover from losing its in-memory execution loop.

Eventually Agent API should be able to recover all active conversations.

The first implementation may focus on the current Agent UI/manual-chat path, but it should establish the recovery model rather than making recovery impossible.

On Agent API startup/recovery:

1. load durable chat/message state;
2. identify the unfinished/latest conversation work;
3. inspect the last assistant response and its executable command blocks;
4. reconnect each SH block to an existing Executor execution when one was already created;
5. query Executor state/result by ID or durable correlation metadata;
6. if the execution is still running, continue waiting/observing rather than launching a duplicate;
7. if terminal, feed the stored result back into the Agent loop;
8. if dead/failed, handle that terminal evidence explicitly;
9. only create a new execution when no existing execution corresponds to that SH block.

### Crash window that must be considered

There is a dangerous window:

```text
Agent API asks Executor to create execution #381
Executor successfully starts it
Agent API crashes before persisting/using "381" in its next durable message
```

Recovery must not blindly run the same SH block again.

Therefore the durable execution record needs enough correlation to identify the originating command independently of transient Agent memory.

A strong correlation key for Agent-launched SH work is conceptually:

```text
ChatId
TriggerMessageId
CommandIndex
```

The exact schema may differ, but recovery should be idempotent around this identity.

A partial unique index/conditional creation may be appropriate so two recovery paths cannot create duplicate executions for the same executable command block.

This is an application of the same rule used elsewhere:

> **Use the database constraint/transaction to enforce identity, not a hopeful read-before-write check.**

---

## 25. Agent Self-Restart

Agent API self-restart is a first-class use case, not an accidental side effect.

A valid sequence should be possible:

```text
Agent API is currently running under Executor execution 500

Agent decides/receives request to restart itself

Agent policy approves required SH/action

Executor restart infrastructure creates replacement execution 501

old Executor owner terminates Agent API process

replacement Agent API starts under execution 501

new Agent API loads durable chat/messages

new Agent API reconnects to any unfinished shell execution IDs

Agent continues from durable state
```

The new API process must not depend on the old API process remaining alive long enough to complete the restart.

The restart handoff therefore belongs outside the process being restarted.

The execution owner is the natural surviving process boundary.

---

## 26. Console Base Return Objects

Machine-readable JSON should not become the default output contract for MEŽS CLI applications.

They are CLI applications and should remain pleasant to inspect manually.

`Mezhs.Console` should gain a reusable return-object abstraction, conceptually:

```csharp
public abstract class ReturnObjectBase
{
    public override string ToString();

    public static T Parse<T>(string value)
        where T : ReturnObjectBase;

    public static IReadOnlyList<T> ParseMany<T>(string value)
        where T : ReturnObjectBase;
}
```

The exact generic/static API can be adjusted to fit C# cleanly, but the behavior is the contract.

### Human-readable object format

An object should be rendered as named properties:

```text
Id: 123
Status: Running
Command: "dotnet build"
Directory: D:\Projects\Mezhs
TimeoutSeconds: 86400
ExitCode: null
Result: null
Error: null
```

This is preferred over:

```text
[123 Running "dotnet build" ...]
```

and over JSON.

Property names make the output understandable and avoid making parsing depend on property position.

### Enumerable return format

An enumerable of return objects should use a clear separator line:

```text
Id: 123
Status: Running
Command: "dotnet build"

------------------------------------

Id: 122
Status: Completed
Command: "git status"
ExitCode: 0
```

The separator must be a Console Base constant used by both formatting and parsing.

### Value grammar

The right-hand side of each property should reuse the existing Console parser/value grammar wherever possible:

- scalar values;
- quoted strings;
- escaped quotes/backslashes;
- `null`;
- enums;
- nullable values;
- dates/times supported by the binder;
- existing collection syntax where appropriate.

Example:

```text
Command: "echo foo: bar"
```

Parsing must split the property at the first structural `:` and then parse the value using the existing value parser/binder.

### Multiline strings

Do not invent a second indentation/block-string grammar merely for output.

A multiline result can remain a quoted Console value:

```text
Result: "stdout: first line
second line
third line"
```

The existing quote/escape grammar should remain the source of truth.

### Parse by property name

Parsing must use property names, not output order.

Property order is for human readability only.

This makes adding a new property later much less destructive.

Unknown properties may be ignored for forward compatibility if doing so does not hide malformed required data.

Malformed known properties must fail clearly rather than silently falling back to defaults.

### Round-trip invariant

Console Base should guarantee:

> **For supported return-object property types, `Parse<T>(value.ToString())` recreates an equivalent object.**

This must be tested for:

- strings with spaces;
- strings containing `:`;
- literal string `"null"` versus null;
- quotes/backslashes;
- multiline strings;
- integers;
- nullable values;
- enums;
- dates/times used by Executor;
- enumerable return objects;
- separator text occurring inside a quoted multiline property value.

A naïve `Split("------------------------------------")` is not sufficient if that exact line appears inside a quoted result. Enumerable parsing must respect the existing quote grammar/top-level structure.

---

## 27. Executor Public Types

Executor should expose its return types publicly so Agent API can use the same model to parse CLI output.

Example conceptually:

```csharp
public sealed class Execution : ReturnObjectBase
{
    public int Id { get; init; }
    public ExecutionStatus Status { get; init; }
    ...
}
```

Agent API may reference the Executor project/assembly and use the public `Execution` type directly.

This dependency is intentional and follows the parent/child direction.

Do not create an additional `Executor.Contracts` project merely for architectural aesthetics unless the real dependency graph later demonstrates a need for it.

Keep the number of projects/mechanisms small.

---

## 28. Execution Record Shape

The exact physical schema may be refined during implementation, but the durable record must be sufficient for execution, audit, restart and Agent recovery.

Expected data includes approximately:

```text
Id                  INTEGER PRIMARY KEY
Status
Command
Directory
TimeoutSeconds
ProcessId
ExitCode
Result
Error
CreatedAt
StartedAt
HeartbeatAt
CompletedAt
RestartedFromId

MEŽS context as applicable:
ChatId
ParentExecutionId / correlation context
Source
TriggerMessageId
CommandIndex
other required execution-context values
```

Not every conceptual field must become a dedicated SQL column.

Use dedicated columns when the value participates in:

- lookup;
- uniqueness;
- state transitions;
- filtering;
- restart;
- UI/history queries.

Opaque/non-query metadata may remain compactly stored if the shared LogSql foundation already has a clean representation.

Do not pollute the schema with Agent business concepts that Executor never needs to query.

At the same time, do not hide correlation values in an opaque blob if Agent crash recovery needs a unique/indexed lookup on them.

Schema should follow real query/invariant needs.

---

## 29. stdout/stderr Contract

CLI output becomes a transport boundary when Agent API calls Executor.

Therefore stdout discipline matters.

For a successful Console command that returns a value:

> **stdout should contain only the formatted return value.**

Diagnostics, warnings and informational messages belong on stderr.

This preserves simple parsing:

```text
launch Executor Get 381
read stdout
Execution.Parse(stdout)
```

without brittle filtering.

The existing Console Base behavior of writing standalone-context information to stderr is compatible with this rule.

---

## 30. CLI Exit Code vs Executed Process Exit Code

Do not conflate two different exit codes.

### Executor CLI exit code

Indicates whether the Executor command itself succeeded.

Example:

```text
Executor Get 381
```

should exit successfully if it successfully retrieved the record, even if execution 381 previously failed.

### Owned process exit code

Stored inside the durable `Execution` record.

Example:

```text
Id: 381
Status: Failed
ExitCode: 1
```

The CLI query itself can still exit `0` because the query was successful.

This distinction is essential for reliable composition.

---

## 31. No Central Cleanup Service

Do not add a scanner that periodically walks all execution rows looking for stale heartbeats.

Do not add a long-lived Executor API merely to reconcile rows.

Do not add a scheduled maintenance task for normal state correctness.

`Get`, `List`, `Wait` and other real observers can perform lazy reconciliation.

If a stale record is never observed again, its stale label does not matter operationally.

Database retention/archival is a separate future concern and should not be mixed into lifecycle correctness.

---

## 32. Do Not Turn Executor Into a Generic Distributed Task Framework

Executor is generic enough to execute host shell/process work, but the first implementation should resist speculative framework features.

Do not add without demonstrated need:

- distributed nodes;
- remote execution;
- arbitrary worker pools;
- HTTP APIs;
- message brokers;
- priority queues;
- cron/scheduling;
- retry policies;
- exponential backoff frameworks;
- service profiles/groups;
- generic dependency graphs;
- health-check orchestration;
- container orchestration;
- Windows Service installation;
- process-manager YAML DSLs.

The strength of the design is its small invariant:

```text
SQL row
+
one independent owner process
+
one owned shell/process
```

Keep it that way until real use demonstrates another responsibility.

---

## 33. Relationship to Existing Agent Execution Storage

Current Agent API has an `Executions` persistence model that includes both Agent and Shell execution records and owns queued/running/cancel/completion transitions.

That model must be reconsidered during migration.

The desired end state for **shell/process execution history** is one source of truth: Executor.

Do not keep a second copied shell record merely because the old UI/API expects one.

Agent-specific conversation lifecycle that remains genuinely necessary should be represented by chat/message state or a clearly separate Agent concept, not by duplicating Executor shell state.

The migration should simplify the current Agent model rather than add Executor beside it and leave both active indefinitely.

---

## 34. Agent UI Requirements

Agent UI must be able to:

- list shell executions for a chat;
- see current status;
- see elapsed/running state;
- see command text;
- see output/error;
- see exit code;
- see terminal failure/dead/timeout state;
- kill a running execution;
- restart an execution and receive/follow the new ID.

The UI may continue polling Agent API.

Agent API may in turn invoke Executor CLI/read shared durable data as appropriate, but the status shown must ultimately be derived from Executor truth.

UI actions should not directly manipulate SQLite from the browser.

---

## 35. Restart Lineage in UI

Restart creates a new execution ID.

The UI should not pretend the old execution changed identity.

Example:

```text
381  Killed
  restarted as -> 382

382  Running
  restarted from -> 381
```

The exact visual treatment can remain simple, but durable lineage should be preserved if the schema can support it cleanly.

This makes execution history auditable and avoids erasing evidence.

---

## 36. Recovery and Idempotency Are More Important Than In-Memory Convenience

Whenever the implementation has a choice between:

```text
"we remember this in memory"
```

and:

```text
"we can prove/recover this from durable identity/state"
```

prefer the durable form for execution ownership and Agent recovery.

That does not mean every temporary detail belongs in SQL.

It means the facts required to avoid duplicate host actions after a crash must be durable.

A restarted Agent must not execute a destructive SH block twice merely because its previous process died after launching the first copy.

---

## 37. Engineering Sanity Check

This architecture is a **refactor of ownership**, not merely a local repair.

### Is this the right place?

Yes.

Host process lifetime should not be owned by Agent API, PowerShell smoke scripts, Visual Studio launch profiles or arbitrary callers.

Executor is a sibling execution mechanism with one narrow responsibility.

### Does another mechanism already own this?

Current Agent shell code directly owns `ProcessStartInfo`, output capture, timeout and process-tree termination.

That behavior should migrate/reuse into Executor rather than being duplicated.

Current AgentStore owns shell execution history.

That shell history should migrate to the Executor log rather than coexist as a second truth.

### Is there a semantic owner?

Yes.

The durable execution row is the semantic owner of state.

The claimed `Run` process is the runtime owner while active.

### Does this require async everywhere?

No.

Public CLI commands can remain synchronous to fit Console Base.

`Run` may internally use threads/tasks/timers where genuine concurrent waiting is required for heartbeat, output and child process monitoring.

### What can break?

Key risks include:

- caller exit accidentally terminating the detached runtime;
- duplicate `Run` claims;
- lost association between Agent SH block and execution ID after API crash;
- stale heartbeat races;
- stdout protocol contamination;
- restart helper being killed with the process it is restarting;
- child process-tree leaks;
- timeout/kill races with natural completion;
- SQLite write contention from frequent heartbeats;
- Console return-object parsing bugs around multiline output/quotes/separators;
- accidental retention of a direct Agent shell path that bypasses Executor.

These must be tested explicitly.

### Does it increase coupling?

Agent API gains an intentional dependency on Executor.

Executor gains no Agent dependency.

This is one-way parent/child coupling and is acceptable.

### Is it bloated?

It should not be.

The design explicitly rejects a daemon/API, IPC system, scheduler, generic process manager and watchdog.

### Can it be simpler?

SQLite replaces central in-memory coordination.

Lazy stale reconciliation replaces a watchdog.

The same executable replaces a separate worker program.

Environment context replaces a large Agent-specific command signature.

Console human-readable formatting replaces a separate JSON transport contract.

### What invariant makes it correct?

One row, one owner, atomic claim, durable state.

### Is it hard to misuse?

It should be, provided:

- `Run` accepts only an existing ID;
- claims are conditional/atomic;
- Agent has no second direct shell path;
- stdout results are deterministic;
- restart creates a new row rather than mutating history.

---

## 38. Implementation Sequence

The next coding task should stay on the same `executor-foundation` review branch and should roughly proceed in this order, adjusting only when implementation evidence demands it.

### Phase 1: Console Base return-object foundation

- add `ReturnObjectBase` or equivalent reusable abstraction;
- implement human-readable `Property: value` formatting;
- implement typed parsing using existing Console parser/binder semantics;
- implement enumerable separator formatting/parsing;
- add strong round-trip tests including multiline/separator edge cases;
- ensure stdout/stderr behavior remains deterministic.

### Phase 2: Shared LogSql/SQLite reuse

- identify/extract the reusable SQLite/LogSql ownership required by Executor;
- do not copy stale branch code wholesale;
- establish WAL/busy timeout/transaction behavior;
- keep generic database mechanics independent from Agent semantics.

### Phase 3: Executor durable model

- add `Mezhs.Executor` project;
- define `Execution` public return type;
- create schema/migrations;
- implement integer PK IDs;
- implement atomic claim/state transitions;
- implement Get/List/Wait lazy stale reconciliation.

### Phase 4: Process runtime

- move/reuse shell invocation semantics from current Agent Shell implementation;
- implement Execute -> detached Run <id>;
- implement heartbeat;
- implement timeout;
- implement output capture;
- implement KillRequested observation/process-tree termination;
- verify caller-exit independence.

### Phase 5: Restart

- implement new-row restart semantics;
- preserve lineage;
- solve self-restart handoff so replacement survives termination of old child tree;
- test Agent-API-like long-lived child restart.

### Phase 6: Agent integration

- route SH exclusively through Executor after policy validation;
- remove direct shell process start from Agent API;
- remove/replace duplicate AgentStore shell execution persistence;
- map Executor public execution records into Agent API/Web views;
- preserve command-result evidence behavior;
- add kill/restart UI/API wiring.

### Phase 7: Recovery

- recover unfinished UI/manual Agent conversation on API startup;
- reconnect existing SH blocks to execution rows using durable ID/correlation;
- wait/read terminal result instead of rerunning completed/in-progress shell work;
- establish idempotent correlation constraints;
- later generalize from current UI/manual path to all active Agent conversations.

---

## 39. Acceptance Tests

The feature is not done merely because unit tests pass.

The acceptance suite must reproduce the lifecycle problems this design exists to solve.

### Detached execution survives caller exit

1. process A invokes `Execute` for a command that stays alive long enough to inspect;
2. A receives ID;
3. A exits completely;
4. process B invokes `Get(id)`;
5. execution remains healthy/running;
6. result eventually completes normally.

### Atomic claim

1. create one `Created` execution;
2. launch two `Run <same id>` processes concurrently;
3. exactly one owns/executes the command;
4. the command side effect occurs exactly once.

### Heartbeat/dead reconciliation

1. start execution;
2. verify heartbeat advances;
3. terminate runtime owner abnormally;
4. do nothing for a while;
5. verify no global watchdog is required;
6. call `Get`/`List`;
7. verify stale active row becomes `Dead` conditionally.

### Kill

1. start long-running execution;
2. call `Kill(id)`;
3. verify durable state becomes requested;
4. owner observes request;
5. owned process tree terminates;
6. final state becomes `Killed`.

### Restart

1. start long-running execution;
2. call `Restart(id)`;
3. receive new ID;
4. verify old history remains;
5. old owned process stops;
6. replacement starts from stored definition;
7. replacement has different PK;
8. lineage is visible.

### Self-restart

Use a child process that behaves like Agent API:

1. it is running under Executor;
2. from inside that application context trigger Executor restart of its own execution;
3. old application exits/is terminated;
4. restart infrastructure survives;
5. replacement process starts successfully;
6. no dependency on old application remains.

### Console return-object round trip

Test `ToString`/Parse for all Executor return types and edge values.

### Agent crash recovery

1. Agent launches SH execution;
2. execution row exists and runs;
3. kill/restart Agent API before result is incorporated;
4. new Agent API loads durable conversation;
5. finds existing execution rather than launching duplicate;
6. obtains result/status;
7. continues Agent conversation.

### Policy boundary

Audit/test that Agent API shell execution after migration has no direct host shell start path outside Executor.

---

## 40. Non-Goals for the First Implementation

The first Executor implementation is not intended to provide:

- remote execution;
- a generic service manager UI;
- automatic crash restart policies;
- distributed workers;
- arbitrary scheduling;
- an HTTP control API;
- a central daemon;
- permanent background watchdogs;
- automatic cleanup/retention policy;
- process health probing beyond owner heartbeat;
- application-specific readiness checks;
- a generic dependency graph between services;
- cross-machine locking;
- a replacement for OS user/security permissions.

These can only be introduced later if real use demonstrates they are necessary.

---

## 41. Definition of Done

The Executor foundation is complete when all of the following are true:

- `Mezhs.Executor` exists as a CLI application;
- execution IDs are SQLite integer primary keys;
- `Execute` creates durable execution and returns the ID without waiting;
- independently spawned runtime survives caller exit;
- runtime atomically claims one row;
- runtime executes the host shell/process using preserved semantics;
- runtime heartbeats roughly once per second;
- execution timeout is enforced;
- `Get` and `List` lazily reconcile stale active executions to `Dead`;
- `Wait` observes without accidentally killing;
- `Kill` operates through durable requested state and owner observation;
- `Restart` creates a new ID and preserves history;
- self-restart handoff survives termination of the application being restarted;
- Executor uses shared LogSql/SQLite infrastructure rather than a duplicate SQLite owner;
- Console Base supports human-readable typed return-object serialization/parsing;
- Agent API validates policy before Executor invocation;
- Agent API no longer directly starts arbitrary host shells;
- Agent shell execution history has Executor as its only source of truth;
- Agent UI can inspect and control Executor-backed shell executions;
- at least the current Agent UI/manual conversation can recover after Agent API restart without duplicating an already-started SH execution;
- strong lifecycle/integration tests demonstrate these invariants;
- the full diff has been reviewed against `.agents` sanity questions;
- the review branch remains unmerged until explicit post-review approval.

---

## 42. Guiding Principle

The Executor should stay boring.

Its job is not to understand why an Agent wants something executed.

Its job is to make one host execution durable, observable and independently owned.

The architecture should remain reducible to:

```text
one SQL row
    +
one independent owner process
    +
one owned shell/process
```

Everything else should exist only when required to preserve that invariant or to make the result safely usable by MEŽS.
