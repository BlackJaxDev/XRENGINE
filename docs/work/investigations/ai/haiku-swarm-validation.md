# Haiku Swarm Validation

Date: 2026-10-09
Validation: [Local Agent Broker](../../testing/ai/local-agent-broker.md)

## Scope

Enable `claude-haiku-5-5` beside `gpt-6-luna` for code swarms at `max` effort.
Use the root model for every planning, proposal, and review request. Preserve
the existing budget, review, and application rules.

## Findings

The runner used a fixed Luna model in provider requests, completion checks,
aggregate results, and node snapshots. These paths now use the selected model.
Admission accepts only Luna or Haiku at `max` effort.

The first live check used synthetic source with one integer constant and a
4,096-token limit per step. Planning completed with Haiku. The leaf reached
`max_tokens`, and the swarm returned `BudgetExceeded`. It returned no approved
changes and left the synthetic file unchanged. The node snapshot still showed
Luna as its requested model. The node constructor now receives the model from
its parent, including before a provider request starts.

The second check used the normal 8,192-token limit per step and an explicit
instruction to use the supplied snapshot hash without recomputing it. The
whole run reserved at most 24,576 output tokens for one planning request,
one leaf request, and one review request. Automatic application was disabled.

## Result

The second run completed all three requests. The root approved one replacement
from `Value = 1` to `Value = 2`. Both nodes reported requested and actual model
`claude-haiku-5-5`. The aggregate reported the same exact model. No source file
was changed. The provider reported 16,262 output tokens across the three
requests, below the reserved limit. This confirms that even a small task can
use substantial reasoning output at `max` effort.

The broker regression suite passed 111 tests, including both supported swarm
models, exact model propagation, substitution rejection, and unsupported
model/effort admission.
