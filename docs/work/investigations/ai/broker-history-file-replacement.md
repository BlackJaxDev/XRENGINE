# Broker History File Replacement

Date: 2026-10-09
Validation: [Local Agent Broker](../../testing/ai/local-agent-broker.md)

## Problem

A live Haiku request returned the exact requested model and completed its
provider stream. The broker then reported an internal access-denied failure.
The provider attempt already contained a completed response and token usage.

## Findings

`BrokerHistoryPublisher.PublishNow` allowed a history write exception to reach
`AgentRunRegistry.ExecuteAsync`. The registry then replaced the completed result
with a host failure. `BrokerHistoryStore.LoadRecords` used `File.ReadAllText`,
which does not permit delete sharing on Windows. A tray reader could therefore
block the writer's atomic file replacement. Sandbox restrictions also produced
access errors during validation, but the live failure recurred outside the
sandbox.

The first sandbox run also failed during the tray mutex lookup. That lookup
was outside the startup error handler even though tray startup is supplemental.

## Changes

History readers now permit file replacement through `FileShare.Delete`.
A failed immediate history write reports a diagnostic and queues a later write
while the publisher is active. It leaves the MCP run result intact. Tray mutex
access failures now use the same nonfatal path as other tray startup failures.
An existing tray process keeps its old reader until it restarts; the broker
still preserves the provider result if that reader holds a file open.

## Validation

The repeated Haiku text request completed. A separate two-turn Haiku repository
read also completed with one tool call. The three other Claude models completed
their text checks. All responses reported the exact requested model ID.
