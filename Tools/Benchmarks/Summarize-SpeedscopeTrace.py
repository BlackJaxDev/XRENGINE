"""Summarize a dotnet-trace Speedscope export for each thread.

The harness writes the export with `Measure-PhysicsChainScale.ps1 -CpuTraceSeconds`.
The sample profiler suspends threads at safe points, so it over-attributes time to
lock entry (`Monitor.Enter_Slowpath`) and to large struct copies
(`BulkMoveWithWriteBarrier`). Use these reports to find call paths; confirm costs
with stage timers or phase timings.

Examples:
  python Tools/Benchmarks/Summarize-SpeedscopeTrace.py trace.speedscope.json --threads 4
  python Tools/Benchmarks/Summarize-SpeedscopeTrace.py trace.speedscope.json \
      --thread "(33120)" --focus XRWindow.RenderCallback --depth 8
  python Tools/Benchmarks/Summarize-SpeedscopeTrace.py trace.speedscope.json \
      --callers Monitor.Enter_Slowpath --caller-depth 3
"""

import argparse
import json
from collections import defaultdict


def stacks_of(profile):
    """Yields (stack, weight) pairs for a sampled or evented Speedscope profile."""
    if profile.get("type") == "sampled":
        for stack, weight in zip(profile["samples"], profile["weights"]):
            yield tuple(stack), weight
        return
    stack = []
    last = profile.get("startValue", 0)
    for event in profile.get("events", []):
        at = event["at"]
        if stack and at > last:
            yield tuple(stack), at - last
        last = at
        if event["type"] == "O":
            stack.append(event["frame"])
        elif stack and stack[-1] == event["frame"]:
            stack.pop()
        elif event["frame"] in stack:
            index = len(stack) - 1 - stack[::-1].index(event["frame"])
            del stack[index:]


def short_name(name):
    """Removes the module prefix and the parameter list of a frame name."""
    return name.split("!", 1)[-1].split("(")[0]


def report_thread(frames, name, total, samples, args):
    print("=" * 100)
    print(f"THREAD {name}  total={total:.1f}")
    inclusive = defaultdict(float)
    exclusive = defaultdict(float)
    for stack, weight in samples:
        for frame in set(stack):
            inclusive[frame] += weight
        if stack:
            exclusive[stack[-1]] += weight
    if args.top > 0:
        print("-- inclusive")
        for frame, weight in sorted(inclusive.items(), key=lambda kv: -kv[1])[:args.top]:
            print(f"{100 * weight / total:6.2f}%  {frames[frame][:160]}")
    if args.self_top > 0:
        print("-- self")
        for frame, weight in sorted(exclusive.items(), key=lambda kv: -kv[1])[:args.self_top]:
            print(f"{100 * weight / total:6.2f}%  {frames[frame][:160]}")
    if args.focus:
        report_callee_tree(frames, total, samples, args)
    if args.callers:
        report_callers(frames, total, samples, args)


def report_callee_tree(frames, total, samples, args):
    tree = defaultdict(float)
    root = 0.0
    for stack, weight in samples:
        position = next((k for k, f in enumerate(stack) if args.focus in frames[f]), None)
        if position is None:
            continue
        root += weight
        below = stack[position + 1: position + 1 + args.depth]
        for depth in range(len(below)):
            tree[tuple(below[: depth + 1])] += weight
    print(f"-- callee tree under '{args.focus}' ({100 * root / total:.2f}% of thread)")

    def walk(prefix, level):
        children = [(k, w) for k, w in tree.items() if len(k) == level + 1 and k[:level] == prefix]
        for key, weight in sorted(children, key=lambda kv: -kv[1]):
            percent = 100 * weight / total
            if percent < args.min_percent:
                continue
            print(f"{'  ' * level}{percent:6.2f}%  {short_name(frames[key[-1]])[:150]}")
            if level + 1 < args.depth:
                walk(key, level + 1)

    walk((), 0)


def report_callers(frames, total, samples, args):
    callers = defaultdict(float)
    hit = 0.0
    for stack, weight in samples:
        position = next((k for k, f in enumerate(stack) if args.callers in frames[f]), None)
        if position is None:
            continue
        hit += weight
        chain = " <- ".join(short_name(frames[stack[j]])[-110:]
                            for j in range(position - 1, max(-1, position - 1 - args.caller_depth), -1))
        callers[chain] += weight
    print(f"-- callers of '{args.callers}' ({100 * hit / total:.2f}% of thread)")
    for chain, weight in sorted(callers.items(), key=lambda kv: -kv[1])[:25]:
        print(f"{100 * weight / total:6.2f}%  {chain}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("trace", help="Speedscope JSON file from dotnet-trace")
    parser.add_argument("--threads", type=int, default=6, help="busiest threads to report (by event count)")
    parser.add_argument("--thread", default=None, help="report only threads whose name contains this text")
    parser.add_argument("--top", type=int, default=30, help="inclusive frames to list")
    parser.add_argument("--self-top", type=int, default=15, help="self frames to list")
    parser.add_argument("--focus", default=None, help="print the callee tree under frames that contain this text")
    parser.add_argument("--depth", type=int, default=6, help="callee tree depth")
    parser.add_argument("--min-percent", type=float, default=0.5, help="hide callee tree nodes below this share")
    parser.add_argument("--callers", default=None, help="aggregate the callers of frames that contain this text")
    parser.add_argument("--caller-depth", type=int, default=3, help="caller frames to show for --callers")
    args = parser.parse_args()

    with open(args.trace, "r", encoding="utf-8") as handle:
        data = json.load(handle)
    frames = [frame["name"] for frame in data["shared"]["frames"]]
    profiles = data["profiles"]
    if args.thread:
        profiles = [p for p in profiles if args.thread.lower() in p.get("name", "").lower()]
    # Evented exports give every thread the same time span, so rank threads by event count.
    profiles.sort(key=lambda p: -len(p.get("events", p.get("samples", []))))
    for profile in profiles[: args.threads]:
        samples = list(stacks_of(profile))
        total = sum(weight for _, weight in samples)
        if total > 0:
            report_thread(frames, profile.get("name", "?"), total, samples, args)


if __name__ == "__main__":
    main()
