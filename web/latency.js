// Rolling latency statistics for the status overlay (pure, testable in Node).

/** Nearest-rank percentile of an unsorted array (p in 0..100); 0 for an empty array. */
export function percentile(values, p) {
  if (!values.length) return 0;
  const sorted = Array.from(values).sort((a, b) => a - b);
  const rank = Math.max(1, Math.ceil((p / 100) * sorted.length));
  return sorted[Math.min(rank, sorted.length) - 1];
}

/** Keeps the last `size` round-trip times (ms). */
export function createLatencyStats(size = 100) {
  const buf = [];
  return {
    add(ms) {
      buf.push(ms);
      if (buf.length > size) buf.shift();
    },
    median: () => percentile(buf, 50),
    p95: () => percentile(buf, 95),
    count: () => buf.length,
    clear: () => {
      buf.length = 0;
    },
  };
}

/** Text for the status strip, e.g. "connected · 8 ms (p95 14)". */
export function formatStatus(base, stats, show) {
  if (!show || stats.count() === 0) return base;
  return `${base} · ${Math.round(stats.median())} ms (p95 ${Math.round(stats.p95())})`;
}
