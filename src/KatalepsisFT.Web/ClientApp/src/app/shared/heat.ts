/** Diverging fill for a gain or loss: blue up, red down, on a neutral ground. */
export function heat(value: number, max: number): Record<string, string> {
  const t = Math.max(-1, Math.min(1, value / max));
  const to = t >= 0 ? [42, 120, 214] : [208, 59, 59];
  const rgb = [240, 239, 236].map((c, i) => Math.round(c + (to[i] - c) * Math.abs(t)));
  return { background: `rgb(${rgb.join(',')})`, color: Math.abs(t) > 0.5 ? '#fff' : '#1f1f1f' };
}
