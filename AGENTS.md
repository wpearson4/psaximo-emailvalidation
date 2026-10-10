# AGENTS.md — Digital Warehouse Back End

## Purpose
These instructions apply to the Digital Warehouse .NET backend repository. Follow the existing solution architecture and conventions unless a change has a clear, evidence-based benefit.

## Engineering Priorities
Optimize for correctness, maintainability, security, testability, observability, performance, and clear domain boundaries.

Before changing code, inspect the surrounding implementation and dependencies. Prefer established .NET conventions and proven design patterns. Do not introduce abstractions or architectural layers merely for theoretical purity.


## Mandatory Architecture Guardrails

These rules are requirements, not suggestions. When existing code violates them, do not expand the violation. New or materially changed code MUST comply unless the user explicitly approves an exception. If a requested change conflicts with a guardrail, stop and surface the conflict rather than silently weakening the architecture.

### Dependency Direction — MUST
The dependency direction is **API/Presentation → Application → Domain**, with Infrastructure implementing interfaces required by inner layers. Domain code MUST NOT depend on API/presentation, MongoDB, HTTP, Service Bus, gRPC, file formats, or other infrastructure. Presentation and data-source concerns MUST remain separated from domain logic.

- Controllers/endpoints MUST NOT contain business rules.
- Domain and data-source code MUST NOT call presentation code.
- Infrastructure concerns MUST NOT leak into domain types for convenience.
- Cross-layer access that bypasses an established boundary is prohibited.
- A new boundary or layer MUST have a concrete responsibility; do not add layers solely for pattern purity.

### Business Logic Ownership — MUST
Every business rule MUST have one authoritative implementation. UI, API, workers, scheduled jobs, and public API entry points MUST invoke the same authoritative behavior rather than reproduce it.

For non-trivial or evolving business rules, prefer a **Domain Model** with behavior close to the data and invariants it governs. Transaction-script-style procedural logic is acceptable only for genuinely simple use cases. If conditional business logic is growing, duplicated across use cases, or becoming difficult to change, refactor toward cohesive domain behavior rather than adding more branches.

Application services MUST orchestrate use cases; they MUST NOT become a dumping ground for domain rules. A Service Layer, where present, defines the application boundary and coordinates use-case concerns such as authorization, transactions, external notifications, and domain invocation. Keep it as thin as the use case permits.

### Persistence Isolation — MUST
SQL/MongoDB access and persistence mechanics MUST be separated from domain logic. Rich domain models MUST NOT know database schemas or persistence APIs. Use the repository's established repository/mapper/gateway abstraction. Do not introduce direct persistence calls into domain entities.

When a Unit-of-Work/transaction boundary exists, writes belonging to one atomic use case MUST be coordinated through that boundary. Do not scatter independent saves throughout domain behavior.

### Distribution Boundaries — MUST
Remote calls are architectural boundaries and MUST be explicit. Do not design remote interfaces as though they were local object calls. Remote APIs SHOULD be coarse-grained and SHOULD use explicit contracts/DTOs. Avoid chatty remote interfaces and repeated per-item network calls.

Do not split components into separate processes/services merely for conceptual purity. Distribution adds latency, failure modes, operational complexity, and consistency concerns; require a concrete reason before introducing it.

### Performance Claims — MUST BE MEASURED
Do not justify complexity with speculative performance claims. For meaningful optimization, establish a baseline and measure after the change using representative data/workload. Prefer fixing query shape, network round trips, algorithms, allocations, and data access before micro-optimizing code.

### Pattern Selection — MUST BE INTENTIONAL
Patterns are the preferred vocabulary for recurring design problems. Agents MUST actively consider established design patterns before inventing custom abstractions. Pattern selection remains contextual: select a pattern because the problem and trade-offs justify it, not merely to increase pattern usage. When introducing a significant pattern, the implementation or change summary MUST state the problem it solves and why the selected pattern is appropriate.

### Design Patterns — DEFAULT DESIGN VOCABULARY
Design patterns MUST be used actively as the default vocabulary for recurring design problems. Before inventing a custom abstraction, inspect whether an established pattern expresses the intent more clearly. Use the Refactoring.Guru design-pattern catalog as the primary quick-reference guide for classic creational, structural, and behavioral patterns: https://refactoring.guru/design-patterns

Pattern use MUST remain problem-driven: **identify the design pressure first, then select the pattern**. Do not add ceremony merely so code can be labeled with a pattern. Prefer the simplest pattern that creates a meaningful boundary, removes unstable conditionals, isolates change, or makes behavior easier to extend and test.

For non-trivial new design or material refactoring, explicitly consider applicable patterns before implementation. In particular:

- **Strategy** SHOULD be the default candidate when algorithms, policies, matching rules, pricing/credit rules, validation behavior, provider behavior, or other interchangeable behavior varies. Prefer Strategy over growing `if`/`switch` trees when the variation is a real domain concept.
- **Factory Method / Abstract Factory** SHOULD be considered when object construction varies by provider, environment, domain type, or family of collaborating components. Keep construction decisions out of business workflows.
- **Builder** SHOULD be considered for complex object construction with many optional inputs or staged configuration; do not use it for simple constructors.
- **Adapter** MUST be considered when integrating an external API, SDK, legacy component, or incompatible interface. Translate external models at the boundary rather than allowing vendor concepts to spread through the application.
- **Facade** SHOULD be used to present a simpler, intentional boundary over a complex subsystem. Application/Service Layer boundaries may act as facades, but facades MUST NOT become god services.
- **Decorator** SHOULD be considered for orthogonal behavior such as logging, metrics, caching, retries, authorization, validation, or other cross-cutting behavior that can wrap a stable contract without polluting core logic.
- **Proxy** SHOULD be considered when access control, lazy access, remote access, caching, or lifecycle control must stand in front of another object. Do not disguise remote latency or failure semantics as a cheap local call.
- **Command** SHOULD be considered when a use case/request benefits from being represented explicitly, queued, retried, audited, scheduled, or handled through a pipeline. Commands MUST express intent and MUST NOT become generic bags of unstructured parameters.
- **Chain of Responsibility** SHOULD be considered for ordered validation, enrichment, filtering, middleware, or processing pipelines where handlers can independently process or pass control onward. Make ordering explicit and test it.
- **State** SHOULD be considered when behavior changes materially according to lifecycle state and conditionals are spreading across methods. Invalid state transitions MUST remain impossible or explicitly rejected.
- **Observer** SHOULD be considered for in-process notifications when multiple independent reactions follow a domain/application event. For durable or cross-process delivery, use the repository's messaging/event infrastructure rather than relying on in-memory Observer semantics.
- **Template Method** MAY be used when a stable algorithm skeleton has controlled variation, but prefer composition/Strategy when inheritance would couple implementations unnecessarily.
- **Composite** SHOULD be considered for true tree/part-whole models where clients should treat individual and grouped objects uniformly.
- **Bridge** SHOULD be considered when two dimensions of variation need to evolve independently; do not create parallel inheritance hierarchies when composition separates them more cleanly.
- **Iterator** SHOULD normally be supplied by .NET collection abstractions/LINQ rather than custom implementations unless traversal semantics are genuinely domain-specific.
- **Mediator** SHOULD be considered when many components are becoming directly coupled to one another. The mediator MUST coordinate interactions without absorbing domain behavior that belongs in the participants.
- **Memento** SHOULD be considered only when explicit snapshot/restore semantics are required. Do not use it as a substitute for proper persistence, audit history, or event modeling.
- **Visitor** SHOULD be used sparingly; consider it when operations change more often than a stable object structure and double dispatch materially simplifies the design.
- **Prototype** SHOULD be used only when cloning an existing configured object is clearer or materially cheaper than constructing one normally.
- **Singleton** is NOT a default pattern. Prefer DI-managed lifetimes. Use a true Singleton only when one process-wide instance is an actual invariant and shared mutable state/concurrency risks are understood.
- **Flyweight** SHOULD be considered only for measured memory pressure involving very large numbers of repeated immutable values.

When a pattern is introduced or materially expanded, its intent SHOULD be obvious from names and structure. Prefer domain-specific names (`EmailValidationStrategy`, `SearchCommandHandler`) over pattern-only names (`Strategy1`, `ConcreteCommand`). Comments should explain **why** the pattern is needed, not restate its mechanics.

### Pattern Review — REQUIRED FOR NON-TRIVIAL CHANGES
Before implementing a non-trivial feature/refactor, perform this lightweight review:
1. Identify the behavior likely to vary or the coupling/complexity being controlled.
2. Check the established repository design first.
3. Consider relevant patterns from Refactoring.Guru and the enterprise patterns already required by this file.
4. Select the smallest pattern or combination that solves the actual problem.
5. Reject patterns that add indirection without a concrete benefit.
6. Preserve dependency direction, domain ownership, persistence isolation, and public-contract compatibility.
7. In the completion summary, name significant patterns introduced or intentionally reused and state the design pressure they address.

A custom abstraction that duplicates a well-known pattern SHOULD be renamed/restructured to use the common pattern vocabulary when doing so improves clarity and does not create unnecessary churn.

### Architectural Violations — STOP CONDITIONS
Do not complete a change without explicitly surfacing it when the change would:
- duplicate an existing business rule;
- introduce a dependency from Domain to Presentation/Infrastructure;
- expose persistence models as public API contracts;
- add an unbounded fan-out, N+1 query, or per-record remote call on a scalable path;
- weaken authorization, company/account isolation, credit enforcement, validation, or idempotency;
- create a distributed boundary without a concrete operational requirement;
- require a breaking public-contract change that was not explicitly approved.

## Core Principles

### SOLID
Apply SOLID heavily, especially in application and domain code.

- **Single Responsibility:** classes and methods should have one cohesive reason to change.
- **Open/Closed:** prefer meaningful extension points over repeatedly growing conditional structures when real behavioral variation exists.
- **Liskov Substitution:** implementations must honor the behavioral contract of their abstractions.
- **Interface Segregation:** prefer focused interfaces; consumers should not depend on methods they do not use.
- **Dependency Inversion:** business/application logic should not be unnecessarily coupled to infrastructure details.

Do not create an interface for every class. An abstraction should have a meaningful architectural, behavioral, or testing purpose.

### DRY
Do not duplicate business rules, authorization rules, validation, calculations, mappings, constants, or persistence behavior.

However, similar-looking code is not automatically the same domain concept. Prefer temporary duplication over a wrong abstraction when concepts may evolve independently.

### Composition Over Inheritance
Favor dependency injection, strategies, policies, decorators, middleware, pipelines, and collaborating services over deep inheritance hierarchies. Use inheritance only when it accurately models the domain and produces the clearest design.


## Object-Oriented Design

Use strong object-oriented design where it improves the domain model and maintainability.

### Encapsulation
Objects should protect their invariants and expose behavior through intentional APIs.

Prefer domain objects that own valid state transitions over anemic models whose state is freely modified by unrelated services.

Avoid public setters by default when state changes require business rules. Use constructors, factory methods, or behavior methods to establish and maintain valid state.

Keep implementation details private. Expose the smallest useful public surface.

### Behavior With Data
Where appropriate, place behavior with the domain data it governs.

Prefer:

```csharp
creditAccount.Reserve(amount);
transaction.MarkCompleted(result);
purchase.CanValidateEmail();
```

over procedural code that retrieves an object's state and independently manipulates it throughout the application.

Do not force behavior into entities when it genuinely belongs to a domain service or application workflow.

### Cohesion and Coupling
Favor high cohesion and low coupling.

A class should contain behavior and data that naturally belong together. Avoid classes that coordinate unrelated concerns.

Depend on stable abstractions at meaningful boundaries. Keep domain objects independent of transport, persistence, and infrastructure concerns where practical.

### Tell, Don't Ask
Prefer asking an object to perform behavior rather than retrieving its internal state and implementing its rules elsewhere.

Avoid long chains of property inspection followed by external decision logic when the domain object can express the decision itself.

### Law of Demeter
Minimize deep navigation through object graphs.

Avoid code such as:

```csharp
order.Customer.Account.Company.Settings.CreditPolicy
```

when a meaningful domain method or service can provide the required behavior.

### Value Objects
Use value objects for concepts whose identity is defined by their value and which benefit from validation or domain behavior.

Potential examples include:

- EmailAddress
- CreditAmount
- Money
- TransactionId
- AccountId
- DatasetId
- Delimiter configuration

Use value objects when they improve correctness and expressiveness. Do not wrap every primitive merely to satisfy a pattern.

Prefer immutable value objects.

### Entities
Use entities for concepts with meaningful identity and lifecycle.

Entities should maintain their invariants and expose explicit state transitions.

Avoid exposing persistence-specific behavior from domain entities.

### Domain Services
Use domain services when important domain behavior does not naturally belong to a single entity or value object.

Domain services should express domain concepts, not become generic containers for miscellaneous business logic.

### Application Services
Application services orchestrate use cases. They should coordinate domain objects, repositories, authorization, and infrastructure boundaries without absorbing domain rules that belong in the domain model.

### Polymorphism
Use polymorphism when multiple behaviors implement a meaningful common contract.

Prefer strategy/policy objects over expanding `switch`/`if` chains when behavior genuinely varies by provider, workflow, algorithm, or policy.

Do not introduce polymorphism when a simple conditional is clearer and unlikely to grow.

### Immutability
Prefer immutable objects for values, messages, requests, results, and configuration where mutation is unnecessary.

Use records where they appropriately express immutable data/value semantics.

### Constructors and Valid State
Where practical, do not allow domain objects to exist in invalid states.

Validate required invariants at creation boundaries.

Use factories when object construction requires meaningful domain decisions or multiple coordinated steps.

### Inheritance
Favor composition over inheritance.

Use inheritance only when:
- there is a genuine substitutable "is-a" relationship;
- derived types honor the complete base contract;
- inheritance is clearer than composition.

Avoid deep inheritance hierarchies.

### Avoid Anemic Domain Models
Do not automatically model entities as property bags with all business behavior placed in large service classes.

When a rule clearly belongs to an entity/value object, keep the behavior with that domain concept.

At the same time, do not force rich-domain modeling onto simple CRUD/configuration structures that do not contain meaningful behavior.

### Avoid God Objects and God Services
A class should not become the central place for every operation in a subsystem.

Watch for:
- excessive constructor dependencies;
- many unrelated public methods;
- large switch statements;
- unrelated reasons to change;
- very large methods;
- coordination of multiple unrelated domains.

Split by cohesive responsibility when these signals appear.

### Object Boundaries
Keep transport DTOs, persistence documents, domain objects, and public API contracts conceptually separate where their responsibilities differ.

Do not make domain objects depend on HTTP, MongoDB serialization, Service Bus, or UI concerns merely for convenience.

Mapping between boundaries should be explicit and understandable.


## Complexity

### Cyclomatic Complexity
Treat high cyclomatic complexity as a design signal.

When modifying branch-heavy code:
1. understand behavior and protect it with tests;
2. use guard clauses;
3. separate independent decisions;
4. extract cohesive policies/services;
5. use strategy/policy patterns when behavior genuinely varies.

Do not chase a numeric metric at the expense of clarity.

### Big-O / Algorithmic Complexity
Consider time and space complexity for code operating on potentially large inputs.

Prefer appropriate dictionaries, hash sets, indexes, streaming, batching, pagination, projections, and database-side filtering.

Avoid accidental:
- O(n²) nested scans;
- repeated list searches inside loops;
- repeated enumeration;
- N+1 database access;
- unnecessary sorting;
- unnecessary materialization;
- loading unbounded datasets into memory.

For repeated keyed lookup, prefer a `Dictionary` or `HashSet` where the memory tradeoff is appropriate. Optimize meaningful workloads, not trivial paths.

## Architecture
Respect the solution's established boundaries. For new or materially changed code, boundary violations are not acceptable. The expected direction is:

API → Application → Domain → Infrastructure

Adapt names and implementation details to the actual repository, but preserve the dependency direction. Do not force a new architecture where equivalent boundaries already exist.

### API / Controllers
Keep controllers/endpoints thin. They should accept transport input, perform transport-level concerns, invoke application behavior, and translate results to HTTP.

Do not put substantial business logic in controllers.

### Application Layer
Application services coordinate use cases: authorization, orchestration, domain behavior, repositories/services, transactions, and application results.

Avoid giant procedural services. Split by cohesive use case/responsibility.

### Domain
Business rules must have an authoritative home. Examples include credits, purchased-data eligibility, Search capabilities, Match & Append behavior, Email Validation eligibility, and account authorization.

Do not independently reimplement the same rule in the UI, controller, worker, and public API.

## Dependency Injection
Use .NET dependency injection consistently. Prefer constructor injection.

Avoid service locators, hidden dependency resolution, and static mutable dependencies. A constructor with many unrelated dependencies is a signal to review responsibilities.

## Async, I/O Concurrency, and CPU Parallelism

Use the appropriate concurrency model for the workload.

### I/O-Bound Work — Async by Default
Use asynchronous APIs for I/O-bound activities wherever an async implementation is available and the call occurs on a scalable application path.

Examples include:
- MongoDB operations;
- HTTP calls;
- Service Bus operations;
- file and stream I/O;
- network calls;
- gRPC calls;
- external service calls.

Use `async`/`await` end-to-end rather than blocking an asynchronous operation.

Avoid:

```csharp
.Result
.Wait()
.GetAwaiter().GetResult()
```

on normal asynchronous application paths.

Do not use `Task.Run` to wrap naturally asynchronous I/O.

Propagate `CancellationToken` through controllers, application services, repositories, infrastructure clients, and long-running operations where practical.

Use async streams (`IAsyncEnumerable<T>`) when they provide meaningful streaming/backpressure benefits for large asynchronous result sets.

### Independent I/O — Concurrency Where Safe
Independent I/O operations may execute concurrently when doing so is safe and beneficial.

For example, use `Task.WhenAll` when several independent remote calls can proceed simultaneously.

Do not serialize independent I/O unnecessarily.

However, concurrency must be bounded when fan-out can become large. Do not create one unbounded task per record, file row, database item, or remote request.

Use appropriate throttling such as `SemaphoreSlim`, channels, batching, or existing concurrency controls.

Respect downstream rate limits, connection pools, database capacity, and cancellation.

### CPU-Bound Work — Parallelism Where Beneficial
For computationally expensive CPU-bound work, evaluate parallel execution when:
- the workload is sufficiently large;
- operations are independent or safely partitioned;
- ordering requirements are understood;
- shared mutable state can be avoided;
- profiling or workload characteristics justify the overhead.

Appropriate mechanisms may include:
- `Parallel.For`;
- `Parallel.ForEach`;
- `Parallel.ForEachAsync` when the workload also includes asynchronous operations;
- PLINQ where it produces clear code and measurable benefit;
- partitioned worker pipelines;
- bounded worker pools.

Do not use `Task.Run` indiscriminately in ASP.NET request handling merely to move CPU work to another thread. It does not create additional CPU capacity and can reduce server scalability.

For substantial CPU-intensive work, prefer the existing background-processing architecture when the operation should not occupy a request lifecycle.

### Degree of Parallelism
Parallelism must be bounded.

Do not assume `Environment.ProcessorCount` workers is always optimal. Consider:
- CPU saturation;
- memory usage;
- allocation rate;
- downstream I/O;
- thread-pool pressure;
- container/hosting CPU limits;
- competing application workloads.

Make degree-of-parallelism configuration explicit when workload characteristics warrant tuning.

### Thread Safety
Prefer immutable data and isolated partitions over locks.

When shared state is necessary, use appropriate thread-safe primitives/collections and keep critical sections small.

Never mutate non-thread-safe collections from parallel workers.

### Async Does Not Mean Parallel
Keep these concepts distinct:

- **Async** improves scalability/efficiency while waiting for I/O.
- **Concurrency** allows multiple operations to make progress.
- **Parallelism** uses multiple execution resources for CPU work.

Choose intentionally based on workload.

### Measure Before Complex Optimization
Do not introduce parallelism merely because a loop exists.

Parallel execution has scheduling, synchronization, allocation, and debugging costs.

Use profiling, benchmarks, telemetry, or clear workload evidence when choosing a more complex parallel design.


## LINQ
Use LINQ when it improves clarity. Be conscious of repeated enumeration, hidden database queries, premature materialization, and expensive nested operations.

Prefer straightforward code over clever LINQ when the latter obscures complexity or behavior.

## Collections
Choose collections by access pattern:
- `List<T>` for ordered iteration;
- `Dictionary<TKey,TValue>` for keyed lookup;
- `HashSet<T>` for membership;
- `Queue<T>` for FIFO processing.

Do not repeatedly scan a list when an appropriate lookup structure materially reduces complexity.

## MongoDB
Model around actual access patterns.

Prefer:
- atomic updates;
- appropriate projections;
- bounded document growth;
- intentional indexes;
- safe concurrency;
- server-side filtering/aggregation where beneficial.

Avoid read-modify-write races and unbounded arrays.

Create indexes only for demonstrated query patterns. Consider index cost as well as read benefit.

Use MongoDB transactions only when the consistency requirement justifies them and the deployed topology supports them.

## Messaging and Distributed Work
Assume asynchronous messages may be delivered more than once. Consumers must be idempotent where required.

Do not assume exactly-once distributed processing.

Where durable state and message publication must remain consistent, use the established Transactional Outbox pattern when appropriate.

Use stable transaction/job identifiers and correlation IDs.

## Credits and Financial Boundaries
Credit rules belong in authoritative application/domain services and must be concurrency-safe.

Stripe represents real financial/payment events. Digital Warehouse's credit ledger represents application credits, including purchased and promotional credits. Do not manufacture payment transactions solely for application-level auditability.

## Public APIs
Public contracts are stable product interfaces.

Do not expose internal entities, MongoDB documents, persistence models, or infrastructure DTOs directly.

Use explicit contracts and an Anti-Corruption Layer/mapping boundary where appropriate.

External APIs must enforce the same authoritative authorization, account, dataset, field, and credit rules as the web application.

## HTTP and Errors
Use established HTTP semantics and the solution's Problem Details/error contract.

Customer-facing errors should provide:
- correct HTTP status;
- stable machine-readable application code;
- human-readable explanation;
- structured details when useful;
- trace/correlation ID;
- retry guidance when appropriate.

Never expose stack traces, internal exception types, connection details, or sensitive service information.

## Exceptions
Use exceptions for exceptional conditions, not normal control flow. Translate exceptions at appropriate application/API boundaries.

## Validation
Validate at the correct boundary. Transport/schema validation belongs at API boundaries; domain invariants belong in authoritative domain/application logic.

Do not trust client-side validation.

## Security
Treat all external input as untrusted.

Enforce server-side:
- authentication;
- authorization;
- ownership;
- account/company isolation;
- dataset access;
- field capabilities;
- credit rules;
- file validation.

Follow least privilege. Avoid resource-enumeration leaks across customers. Protect against injection, insecure direct object references, unsafe file uploads, and broken authorization.

Never log passwords, secrets, access tokens, payment credentials, or unnecessary personal data.

## File Processing
For large files, favor streaming or bounded batches rather than loading the entire file into memory.

Use mature parsers for structured/delimited files. Do not parse delimited data with naive `Split()` logic when quoting/escaping is possible.

Validate file size, structure, encoding assumptions, and resource consumption.

## Logging
Use structured logging.

Prefer parameterized structured logs rather than interpolated/unstructured log strings.

Do not log secrets or unnecessary personal data.

## Observability
Important operations should support traceability across API, application services, outbox, messaging, workers, and downstream services.

Use the repository's existing telemetry/correlation infrastructure.

## Performance
Prioritize:
1. correct data structures/algorithms;
2. database access patterns;
3. network calls;
4. memory behavior;
5. repeated computation.

Avoid N+1 access. Use projections when full documents are unnecessary. Batch operations when appropriate.

Do not sacrifice maintainability for insignificant micro-optimizations.

## Testing

### Unit Tests
Test focused domain/application behavior and edge cases.

### Integration Tests
Test MongoDB, API, messaging, persistence, and infrastructure boundaries.

### Contract Tests
Test public API schemas, status codes, error codes, idempotency, and compatibility.

Test success, validation failures, authorization failures, important edge cases, concurrency-sensitive behavior, and idempotency where relevant.

Avoid tests that merely mirror implementation details.

## Refactoring
Improve nearby code when it directly supports the requested change, tests provide confidence, and risk is controlled.

Do not perform broad unrelated refactors during feature work.

Before replacing existing code, understand why it exists and what depends on it. Preserve working patterns when they remain appropriate.

## Dependencies
Before adding a NuGet package, determine whether the .NET platform or existing solution already provides the capability. Evaluate maintenance, licensing, security, and operational impact.

## Naming and Conventions
Use established .NET naming conventions and the repository's domain vocabulary.

Names should communicate intent. Avoid vague names such as `Helper`, `Manager`, `Util`, or `Processor` when a more specific domain name exists.

## Definition of Done — Release Gate
Work is NOT complete until every applicable item below is satisfied. If an item cannot be satisfied, report it explicitly as a blocker or approved exception; do not silently waive it.

Before considering work complete:
- build succeeds;
- relevant tests pass;
- new behavior has appropriate tests;
- business rules have one authoritative implementation;
- security/authorization is enforced server-side;
- error handling follows established contracts;
- logging does not expose sensitive data;
- complexity has not unnecessarily increased;
- obvious O(n²), N+1, or repeated-work patterns have been reviewed;
- I/O-bound paths use asynchronous APIs where appropriate and avoid sync-over-async;
- CPU-intensive paths have been reviewed for appropriate bounded parallelism where it provides meaningful benefit;
- object-oriented boundaries, encapsulation, cohesion, and invariants have been considered for domain changes;
- applicable design patterns were explicitly considered for non-trivial changes, and significant pattern choices are identified in the completion summary;
- new custom abstractions do not unnecessarily reinvent an established pattern;
- public contracts remain compatible unless a breaking change was explicitly approved;
- documentation is updated where behavior or public contracts changed;
- touched code is at least as understandable as before.
