# dmon-home-supervision Specification

## Purpose

How the macOS host runs the processes it depends on and keeps them running.

`dmon-home` is not useful alone: it fronts a set of long-lived local services. This capability
is the supervision model for them — adopt a healthy process before spawning a competing one,
health-check each with a bounded timeout, detect crashes and retry with backoff, honour declared
dependency order on the way up and the way down, and kill spawned children by process group so
nothing is orphaned.

Adoption-before-spawn is the decision worth knowing about: some children are expensive enough
to start (a multi-gigabyte model load) that racing a second copy is materially worse than
attaching to the one already there.

The child model is written for the host's full eventual inventory even though only some children
are enabled today, so adding one is configuration rather than a change to the model.

## Requirements

### Requirement: Children are adopted before they are spawned

For each supervised child the host SHALL health-check the child's known endpoint first and adopt an already-running process when the endpoint answers. The host SHALL spawn a new process only when the endpoint does not answer. Adoption exists so a child that is expensive to start — notably a multi-gigabyte model runtime — survives a restart of the host.

#### Scenario: A live child is adopted

- **WHEN** the host starts and a child's endpoint already answers its health check
- **THEN** the host adopts that process and does not spawn a second one

#### Scenario: A dead child is spawned

- **WHEN** the host starts and a child's endpoint does not answer its health check
- **THEN** the host spawns the child

#### Scenario: An adopted child outlives the host

- **WHEN** the host exits after adopting a child it did not spawn
- **THEN** the adopted child is left running

### Requirement: Each child is health-checked with a bounded timeout

Every supervised child SHALL declare a health check and a timeout. The host SHALL treat a check that does not complete within its timeout as a failure rather than waiting indefinitely, and SHALL surface each child's current health in the UI.

#### Scenario: A hung health check fails

- **WHEN** a child's health check does not complete within its configured timeout
- **THEN** the host records the check as failed and does not block startup of other children

#### Scenario: Health is visible

- **WHEN** any supervised child's health changes
- **THEN** the host's UI reflects that child's new health state

### Requirement: Crashes are detected and retried with exponential backoff

The host SHALL detect unexpected child exits and restart the child with exponentially increasing delay between attempts, so a persistently failing child does not spin. The host SHALL surface repeated failure rather than retrying silently.

#### Scenario: A crashed child is restarted

- **WHEN** a supervised child exits unexpectedly
- **THEN** the host restarts it

#### Scenario: Repeated crashes back off

- **WHEN** a supervised child exits unexpectedly several times in succession
- **THEN** the delay before each successive restart attempt increases
- **AND** the host surfaces the repeated failure in its UI

### Requirement: Startup and shutdown follow declared dependency order

Children SHALL declare their startup ordering, and the host SHALL start them in that order and shut them down in the reverse order, requesting graceful termination before escalating.

#### Scenario: Shutdown reverses startup order

- **WHEN** the host shuts down
- **THEN** children are terminated in the reverse of their declared startup order
- **AND** each is asked to terminate gracefully before being killed

### Requirement: Spawned children are killed by process group on exit

The host SHALL place each spawned child in its own process group and SHALL kill that group on exit, so no descendant survives the host. Orphaned model runtimes holding unified memory cause the next launch to fail allocation with no visible cause.

#### Scenario: No spawned descendant survives

- **WHEN** the host exits after spawning a child that itself spawned descendants
- **THEN** the child and its descendants are terminated

#### Scenario: Adoption is exempt

- **WHEN** the host exits and a child was adopted rather than spawned
- **THEN** that child is not killed

### Requirement: The child model accommodates the full inventory

The supervision model SHALL express every child the host will eventually own — the network gateway, the `Dcal` and `Dmail` services, the mlx reasoner, the mlx triage head, and the speech sidecar — plus read-only monitors for Tailscale, calendar sync, mail and egress. A child SHALL be described by configuration (transport, endpoint, health check, ordering, adoption policy) rather than by a bespoke type per child. Children not started in this change SHALL be expressible without modifying the model.

#### Scenario: Adding a child requires no model change

- **WHEN** a new child is introduced with its transport, endpoint, health check and ordering
- **THEN** it is supervised without changes to the supervision model itself

#### Scenario: Monitors are distinguished from supervised children

- **WHEN** the inventory is inspected
- **THEN** read-only monitors are represented as health sources that are never spawned, adopted or killed
