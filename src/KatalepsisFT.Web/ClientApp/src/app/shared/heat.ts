/** Diverging fill for a gain or loss: indigo up, orange down, on a neutral ground. */
export function heat(value: number, max: number): Record<string, string> {
  const t = Math.max(-1, Math.min(1, value / max));
  const to = t >= 0 ? [99, 102, 205] : [217, 100, 43];
  const rgb = [243, 244, 247].map((c, i) => Math.round(c + (to[i] - c) * Math.abs(t)));
  return { background: `rgb(${rgb.join(',')})`, color: Math.abs(t) > 0.62 ? '#fff' : '#101828' };
}
