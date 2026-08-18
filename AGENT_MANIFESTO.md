# MEŽS Agent Manifesto

## 1. Purpose

MEŽS should evolve from a chat interface into a **policy-controlled agent platform**.

The existing MEŽS functionality should remain usable independently. Agent functionality is an extension built around it, not a replacement for it.

The system should allow chats to:

- execute commands on the host machine;
- use purpose-built console applications;
- react to external events such as WhatsApp messages;
- create and process reminders and schedules;
- spawn other chats;
- maintain persistent logs and domain-specific memory;
- operate unattended when policy allows it;
- remain understandable and auditable when something goes wrong.

The long-term goal is not to build one giant intelligent application. It is to build a small orchestration layer around many simple, explicit tools.

## 2. Core Principle: Existing MEŽS Remains Generic

The existing MEŽS API, integrations and ordinary chat functionality should remain general-purpose.

Agent capabilities must not become a requirement for running normal MEŽS.

A user should still be able to run:

- the existing MEŽS API;
- the existing generic web interface;
- ChatGPT, Gemini, Grok or other integrations;

without installing or enabling shell execution, WhatsApp, reminders or other agent capabilities.

The agent system is an additional layer that consumes MEŽS.

## 3. Web Architecture

The current web interface should remain a standalone generic chat application.

Its reusable React functionality should be extracted into a shared web library.

The intended structure is:

```text
MEŽS Shared Web Components
        │
        ├── MEŽS Web
        │     generic chat UI
        │     no agent dependencies
        │
        └── MEŽS Agent Dashboard
              agent management
              schedules
              listeners
              policies
              execution history
              agent chat inspection
```

Shared components may include:

- chat rendering;
- message rendering;
- composer;
- file handling;
- connection selectors;
- common styling and controls.

The Agent Dashboard can reuse the ordinary chat components without introducing agent concepts into the generic MEŽS Web application.

**Agents are an extension, not a modification.**

## 4. Agent Chats

An agent chat is a normal MEŽS chat with additional orchestration information.

Important properties include:

- policy;
- source;
- execution state;
- source-specific metadata;
- command history;
- completion state.

Possible sources include:

- manual;
- assistant / child agent;
- scheduled;
- reminder;
- WhatsApp listener;
- Jira listener;
- future event sources.

These source names should not define the behavior themselves.

The **policy** defines behavior.

## 5. Policy-Driven Behavior

MEŽS should have one configuration system, centered around its YAML configuration.

Policies may define things such as:

- initial instructions;
- whether autonomous completion is required;
- allowed commands;
- denied commands;
- required successful commands;
- event wake-up rules;
- source-specific behavior;
- completion rules;
- execution limits.

The model may be informed about these rules in its prompt, but the model must never be trusted to enforce them.

**Policy enforcement happens in code.**

For command permissions:

- nothing should be implicitly allowed;
- `deny` overrides `allow`;
- unrestricted execution must be explicitly configured.

## 6. DONE Is a Lifecycle Signal

`DONE` has special meaning for autonomous chats.

It means:

> I claim that the work assigned to this execution is complete.

A policy may require `DONE`.

A policy may also require evidence before accepting `DONE`.

Example:

A WhatsApp agent may require at least one successful WhatsApp send operation.

If the model replies:

```text
DONE
```

without having sent the required message, the Agent should reject completion and return a policy error into the chat.

The model can then correct its actions and eventually issue `DONE` again.

Completion therefore depends on **what actually happened**, not on what the model says happened.

## 7. Execution History Is Truth

Every executable action must create an execution record.

The system should know:

- what command was requested;
- which chat requested it;
- which execution caused it;
- when it started;
- when it finished;
- exit status;
- result;
- parent execution;
- source of the original event.

If the model says:

> I sent the WhatsApp message.

that is irrelevant for policy enforcement.

If the execution history contains a successful `whatsapp send`, then it happened.

**Execution history is evidence.**

## 8. Shell Execution

The first generic agent capability should be shell execution.

The assistant may produce a clearly delimited block such as:

```text
<SH
dotnet test
SH>
```

or equivalent final syntax.

The Agent extracts the contents and passes them to the configured shell.

The shell contents should be treated as **opaque text**.

MEŽS should not attempt to implement another Bash, CMD or PowerShell parser.

This allows shell commands to naturally contain:

- pipes;
- redirection;
- multiline commands;
- Python;
- shell scripts;
- existing CLI programs;
- quoted arguments;
- compound expressions.

Nested MEŽS command blocks should not be supported. Invalid structure should produce a parse error instead of attempting clever recovery.

Multiple shell blocks may exist in one assistant response.

They should be executed deterministically in order.

## 9. Do Not Rewrite Shell Commands

The Agent should avoid modifying the shell command generated by the model.

This is particularly important for execution context.

Instead, context should be provided **out of band**.

When the Agent creates a child shell process, it provides process-level environment variables through `ProcessStartInfo.Environment`.

For example:

```text
MEZHS_EXECUTION_ID
MEZHS_PARENT_EXECUTION_ID
MEZHS_CHAT_ID
MEZHS_CORRELATION_ID
MEZHS_SOURCE
```

Exact fields remain an implementation detail.

The command itself remains unchanged.

Child processes inherit the shell environment naturally on both Windows and Linux.

Applications unaware of MEŽS simply ignore these environment variables.

Applications built using the MEŽS console framework can recognize them automatically.

## 10. Parallel Execution and Context Isolation

A single Agent service may process many chats simultaneously.

It must never mutate its own global process environment to set chat context.

Instead, every shell execution gets its own `ProcessStartInfo`.

Therefore:

```text
Chat A
  -> Shell process A
       MEZHS_EXECUTION_ID=A123

Chat B
  -> Shell process B
       MEZHS_EXECUTION_ID=B456
```

These environments are independent even though both shells were launched by the same Agent process.

This is the normal OS process model and provides natural execution-context isolation.

## 11. MEŽS Console Framework

MEŽS should have a reusable C# console application framework.

The purpose is to make new agent-callable applications extremely cheap to build.

Applications should mostly consist of business methods.

The framework should provide:

- command discovery using reflection;
- command attributes or equivalent metadata;
- argument parsing;
- argument binding;
- type conversion;
- validation;
- automatic help;
- standard error handling;
- execution-context discovery;
- optional machine-readable output.

A new utility application should not repeatedly implement argument parsing and help screens.

## 12. CLI Syntax Should Stay Human-Friendly

Agents should preferably call utilities using ordinary command-line syntax rather than constructing JSON command objects.

For example:

```text
reminder add "Transfer wine batch 12" --after 4d
```

is preferable to requiring a large JSON structure.

The same commands should be pleasant for:

- humans;
- shell scripts;
- agents.

Structured formats remain useful for **results**, where machine interpretation is valuable.

## 13. Type Conversion and Help

Every CLI argument initially arrives as text.

The console framework therefore needs a type-binding system.

Built-in converters should eventually support:

- string;
- integer types;
- floating-point types;
- boolean;
- enums;
- nullable values;
- DateTime / DateTimeOffset;
- TimeSpan or duration;
- arrays / enumerable values;
- possibly paths and other common semantic types.

Converters should know both:

1. how to parse a value;
2. how to describe their accepted format.

The help system should derive its documentation from exactly the same converter used for parsing.

Therefore the documentation and implementation cannot silently drift apart.

For example:

```text
startDate : DateTime
Format: yyyy-MM-dd HH:mm
Example: 2026-08-18 14:30
```

Array syntax should initially remain deliberately simple.

## 14. Help Is Part of the Machine Interface

Every framework application should automatically expose help.

Help should be able to describe:

- available commands;
- command descriptions;
- required parameters;
- optional parameters;
- types;
- formats;
- examples.

The Agent should therefore be able to discover how unfamiliar applications work instead of having every command permanently encoded into its prompt.

The framework itself should provide the help command.

## 15. Execution Context

Applications built on the console framework should automatically detect when they are running underneath MEŽS.

The application author should not need to explicitly add parameters such as:

```text
--chat-id
--execution-id
--source
```

to every business command.

The framework reads execution context from inherited environment variables and makes it available through an execution-context abstraction.

The business method therefore remains simple.

The same binary can still be executed manually from a terminal, in which case no MEŽS execution context exists.

## 16. Causality and Audit Trail

Every execution should belong to a causal chain.

Conceptually:

```text
WhatsApp message
    ↓
WhatsApp listener event
    ↓
Agent execution
    ↓
Shell execution
    ↓
log application
    ↓
created reminder
```

Each level should know its parent execution and overall correlation identifier.

This makes it possible to answer:

> Why does this record exist?

or:

> What caused this message to be sent?

without reconstructing the answer from timestamps and guesses.

Operating-system process parent IDs may be useful diagnostic information, but MEŽS's own execution identifiers are the authoritative lineage.

## 17. Applications Own Capabilities, Chats Own Reasoning

Applications should generally remain simple and deterministic.

The LLM provides flexible reasoning.

For example, a logging application does not necessarily need C# logic saying:

> If yesterday's chicken egg count is missing, average yesterday and today.

Instead, it may return the rules associated with that log after performing an operation.

The chat can then reason about the rule and decide what additional commands are necessary.

This preserves an important distinction:

**Tools perform explicit operations.  
Agents interpret flexible rules and orchestrate tools.**

## 18. Persistent Logs as Explicit Memory

Chat history should not be treated as the only long-term memory of the system.

MEŽS should have explicit logging applications.

Two initial forms are envisioned:

### Text Log

Human-readable persistent information backed by a text file.

Useful for:

- notes;
- activity logs;
- diaries;
- simple historical records;
- manually inspectable domain data.

### SQL Log

Structured persistent information backed by SQLite.

Useful for:

- measurements;
- production records;
- aggregations;
- searching;
- statistical analysis;
- structured history.

Both should use the common console framework.

## 19. Named Domain Logs

Logs should be able to represent specific domains.

Examples:

```text
wine-production
egg-harvest
greenhouse
server-maintenance
personal-notes
```

Each log owns:

- its data;
- its rules file.

For example:

```text
wine-production/
    data.txt
    rules.txt
```

or:

```text
egg-harvest/
    data.sqlite
    rules.txt
```

The exact storage layout is not yet important.

The conceptual ownership is.

## 20. Plain-Language Rules Belong Beside the Data

Each domain log may have a plain-language rules file.

These rules may be extremely specific.

Example for egg production:

> After inserting a row, check whether the previous day has an entry. If it does not, calculate the average between the closest previous known day and the newly inserted value and insert an estimated row for the missing day.

Example for wine production:

> A newly started wine should normally be transferred after three to five days. If a transfer is logged before the reminder runs, cancel the pending first-transfer reminder.

These rules do not need to become C# algorithms.

They are domain knowledge intended for agent reasoning.

## 21. Tool Results May Return Rules

A log command should not necessarily return only:

```text
OK
```

It may return:

```text
Record added successfully.

Rules for this log:
...
```

The simplest initial implementation may simply return the complete contents of the log's rules file after relevant commands such as `add`.

The agent then determines whether those rules imply further work.

Examples:

- insert a missing record;
- create a reminder;
- cancel an existing reminder;
- run another query;
- notify someone.

This creates a useful feedback loop:

```text
agent
  ↓
log add
  ↓
result + domain rules
  ↓
agent reasoning
  ↓
additional actions
```

## 22. Rules Should Remain Inspectable

Plain-language rules should be stored in ordinary files that a human can open, read and edit.

The architecture should prefer transparent behavior over invisible prompt engineering.

If an agent performs a surprising action, it should be possible to inspect:

- the event;
- the chat;
- the commands;
- their results;
- the applicable policy;
- the domain rules.

The system should be explainable by looking at its artifacts.

## 23. Reminders Are Events

A reminder should not require an entirely separate agent architecture.

A reminder is a scheduled future event.

When its time arrives, it wakes or creates an agent execution according to its policy.

Conceptually:

```text
Reminder
    time
    policy
    payload
    source context
    status
```

The same event-processing model used for WhatsApp can process reminders.

## 24. Domain Rules Can Create and Cancel Reminders

Reminders may be derived from domain activity.

Example:

The user logs:

> Started blackcurrant wine batch 7.

The wine agent sees the wine rules and creates a reminder for the appropriate transfer window.

Later the user logs:

> Transferred batch 7.

The rules indicate that the outstanding reminder has already been satisfied.

The agent removes or completes the reminder before it triggers.

This gives the desired interaction:

**The human records reality.  
The system derives what needs to happen next.**

The human should not be required to manually maintain every corresponding reminder.

## 25. WhatsApp Architecture

WhatsApp should be divided into three layers.

### WhatsApp Gateway

A Node.js application owns communication with WhatsApp.

It should remain a relatively thin integration layer.

Its responsibility is sending and receiving WhatsApp information.

It should not contain MEŽS agent reasoning.

### WhatsApp Console Application

A console utility exposes WhatsApp operations through the common command framework.

Examples may include:

```text
whatsapp message send ...
whatsapp message get-last --count 10
```

The exact syntax is not yet final.

From the Agent's perspective, WhatsApp is simply another CLI capability.

### WhatsApp Listener

A listener watches configured WhatsApp conversations and generates events according to its policy.

The listener decides whether a message should wake an agent.

## 26. WhatsApp Listener Configuration Is the Unit of Behavior

There should not be one universal "WhatsApp behavior."

Each configured listener has its own policy.

A listener may correspond to:

- a WhatsApp group;
- a direct conversation;
- another future WhatsApp target.

Examples:

A home-assistant group may wake on essentially every human message.

A machine-management group may react only to messages targeted at a particular machine or at all machines.

A direct conversation may have different rules again.

Thus:

**The policy attaches to the listener instance, not to WhatsApp globally.**

## 27. Agent Identity and WhatsApp Prefixes

Sender identity cannot always be used to prevent feedback loops.

In particular, the WhatsApp account used by MEŽS may also be the user's own account.

Therefore outgoing agent messages need an explicit identity marker.

Each target machine or listener may define a unique identity or name.

The WhatsApp console utility automatically prefixes outgoing messages using that identity.

For example:

```text
[HOME] Watering reminder has been created.
```

or some future configurable syntax.

The LLM does not manually manage the prefix.

The WhatsApp layer applies it automatically.

Listeners can then recognize messages produced by themselves and avoid responding to them again.

For multi-agent groups, prefixes can also act as addressing identities.

## 28. Targeting Multiple Machines

A group may contain several MEŽS-controlled machines or agents.

Messages may target:

- one specific machine;
- several machines;
- all machines.

The exact addressing syntax remains open.

Each listener should determine whether the incoming event is intended for it before spawning work.

This prevents every agent in a shared group from reacting to every message.

## 29. External Events Must Be Idempotent

A previous failure mode demonstrated the danger of repeatedly processing the same incoming event.

Therefore:

**An external event must never create unlimited duplicate agent executions.**

WhatsApp message handling must record processing progress durably.

The CLI/listener abstraction should guarantee deterministic message retrieval.

A listener should know which WhatsApp message it has already processed and must not repeatedly spawn agents for that same message.

The precise implementation may use a small database/checkpoint structure, but the invariant is more important than the storage technology.

## 30. WhatsApp Context Retrieval

A single WhatsApp message may not contain enough context for an agent.

When an agent wakes because of a message, it should normally receive several recent messages.

The WhatsApp console utility should support something equivalent to:

```text
message get-last --count 10
```

and possibly later:

- offset;
- before;
- after;
- pagination or another history mechanism.

A policy determines the default amount of context.

If the agent needs more, it can explicitly request more through the WhatsApp CLI.

The initial prompt does not need to contain the entire lifetime of the group.

## 31. One Active Execution Per Listener Instance

By default, a WhatsApp listener instance should allow only one active agent execution at a time.

Suppose a user sends:

> Log X.

While the agent is working, the user sends:

> No, I meant X plus one.

The second event should not start a parallel agent that races the first one.

Instead:

```text
message 1
    ↓
execution 1 running

message 2 arrives
    ↓
queued

execution 1 finishes
    ↓
execution 2 starts with updated context
```

The second execution can inspect what execution 1 actually accomplished and make the correction.

This provides deterministic behavior and avoids overlapping writes and contradictory replies.

The unit of serialization is the **configured listener instance**, commonly a WhatsApp group or direct chat.

## 32. Event-Driven Agents, Not Permanently Running Agents

Chats are persistent.

Agent executions are temporary.

An agent wakes because something happened:

- a human sent a message;
- a reminder became due;
- a schedule fired;
- Jira changed;
- another agent spawned it;
- a manual user started it.

It reasons, executes commands, and eventually becomes idle or completes.

The system should therefore be designed around:

```text
persistent conversation
+
discrete executions
+
events that wake executions
```

rather than one permanently running thread per agent.

## 33. Child Agents

One agent may eventually create another agent execution.

A child agent should use the same infrastructure as every other agent.

Its source is simply another agent execution.

The causal chain must preserve:

- parent execution;
- originating conversation/event;
- child policy;
- resulting commands.

There should not be a separate architectural mechanism specifically for "agent spawning."

## 34. Schedules

Recurring schedules are another event source.

Examples:

- every Monday;
- every morning;
- every hour;
- one particular future time.

When fired, the schedule creates an event and invokes the configured policy.

Scheduling should eventually be visible and manageable from the Agent Dashboard.

## 35. Agent Dashboard

The Agent Dashboard should eventually provide a clear operational view of the whole system.

Likely concepts include:

- active executions;
- queued executions;
- completed executions;
- failed executions;
- policies;
- listeners;
- reminders;
- schedules;
- child agents;
- execution history;
- causal chains;
- chats;
- command results.

This dashboard is primarily an operational tool.

The ordinary MEŽS web interface remains a generic chat application.

## 36. Safety and Predictability

Agent execution should prefer boring, explicit behavior over clever implicit behavior.

Examples:

- malformed action block → error;
- nested action block → error;
- denied command → policy error;
- unmet DONE requirement → policy error;
- duplicate external event → ignored;
- simultaneous events for one serialized listener → queued;
- unknown CLI parameter → binding error with generated help.

The system should explain failures back to the agent whenever the agent has a reasonable chance of correcting them.

## 37. Avoid Building a Fake Shell Sandbox

Allow/deny command matching is policy enforcement, not a true security sandbox.

A model with unrestricted shell access effectively has the privileges of the account running MEŽS Agent.

MEŽS should not pretend that complicated blacklist logic can make arbitrary shell execution perfectly safe.

Sensitive capabilities should increasingly become explicit structured tools:

```text
whatsapp
jira
reminder
log
```

rather than relying on arbitrary shell scripting for everything.

## 38. Small Tools, Powerful Orchestration

The system should favor small applications with narrow responsibilities.

Examples:

```text
WhatsApp
Reminder
LogText
LogSql
Jira
Files
MachineStatus
```

The agent provides intelligence by composing these tools.

This should make new capabilities easy to add without continuously expanding one central agent codebase.

## 39. Transparent State Over Hidden Intelligence

MEŽS should prefer state that can be inspected.

Important information should live in:

- YAML configuration;
- ordinary rule files;
- text logs;
- SQLite databases;
- execution history;
- chat history.

The system should avoid making critical behavior depend on invisible or ephemeral assumptions inside prompts.

## 40. End-State Mental Model

At the mature stage, MEŽS can be thought of as:

```text
                    Humans / External Systems
                              │
        ┌─────────────────────┼─────────────────────┐
        │                     │                     │
     WhatsApp              Scheduler              Jira
        │                     │                     │
        └─────────────── events / listeners ───────┘
                              │
                              ▼
                      MEŽS Agent Engine
                  policy + execution lifecycle
                              │
              ┌───────────────┼────────────────┐
              │               │                │
            MEŽS API       Shell Runner      Reminders
              │               │
              │               ▼
              │        Console Applications
              │
        AI integrations      ├─ WhatsApp
                             ├─ LogText
                             ├─ LogSql
                             ├─ Jira
                             └─ future tools
```

The Agent is not the place where every feature is implemented.

The Agent decides **what to do next**.

Policies decide **what it may do and when it may finish**.

Tools decide **how specific operations are performed**.

Events decide **when an agent should wake**.

Logs and databases remember **what actually happened**.

Execution context explains **why it happened**.

And MEŽS chat remains the reasoning interface connecting all of these pieces.

## 41. Guiding Principles

The architecture should continue to be judged against these principles:

1. **Existing MEŽS remains independently useful.**
2. **Agent functionality is additive.**
3. **Policies are configuration, not scattered special cases.**
4. **The model is never trusted to enforce its own permissions.**
5. **Actual execution history outranks model claims.**
6. **Tools remain small and explicit.**
7. **CLI interfaces should work naturally for humans and agents.**
8. **Execution context travels outside the command text.**
9. **External events must be idempotent.**
10. **A listener is serialized by default.**
11. **Agents wake for events rather than run forever.**
12. **Domain knowledge belongs beside domain data.**
13. **Plain-language rules are valid executable knowledge when interpreted by an agent.**
14. **Humans record reality; agents derive follow-up actions.**
15. **Important state must be inspectable and auditable.**
16. **Prefer deterministic failure over clever recovery.**
17. **Build reusable infrastructure only where repeated patterns actually exist.**
18. **Do not prematurely lock down abstractions we have not yet learned enough about.**

This manifesto describes the intended destination. Individual implementation details are expected to evolve as MEŽS is built.
