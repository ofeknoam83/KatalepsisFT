import { Pipe, PipeTransform } from '@angular/core';
import { DynamicDecimalPipe } from './dynamic-decimal.pipe';

const MINUS = '−';

/**
 * A dollar amount in full, never shortened to K or M: 205_400_312.4 → "$205,400,312.40". Negative values get a
 * minus sign in front. The second argument is kept for callers written when amounts were shortened; it has no effect.
 */
export function usd(value: number | null | undefined, _digits = 1): string {
  if (value === null || value === undefined) {
    return '–';
  }

  const text = Math.abs(value).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  // A value that rounds to nothing carries no sign.
  return `${value < 0 && text !== '0.00' ? MINUS : ''}$${text}`;
}

/**
 * A dollar amount short enough to read at a glance, for the big figure on a tile: 210_351_674.56 → "$210.35M".
 * The exact amount is always shown next to it. Under a thousand dollars it is the exact amount.
 */
export function compactUsd(value: number | null | undefined): string {
  if (value === null || value === undefined) {
    return '–';
  }

  const size = Math.abs(value);
  const sign = value < 0 && size >= 0.005 ? MINUS : '';
  for (const [limit, letter] of [[1e12, 'T'], [1e9, 'B'], [1e6, 'M'], [1e3, 'K']] as const) {
    // 999,995 would round to "1000.00K"; it is shown in the next unit up instead.
    if (size >= limit * 0.999995) {
      return `${sign}$${(size / limit).toFixed(2)}${letter}`;
    }
  }

  return usd(value);
}

/** A dollar amount in full without the currency sign, for dense tables: 1_250_000.4 → "1,250,000". */
export function wholeUsd(value: number): string {
  return Math.round(Math.abs(value)).toLocaleString('en-US');
}

/** 0.0512 → "+5.1%". With signed = false: "5.1%". */
export function pct(value: number | null | undefined, digits = 1, signed = true): string {
  if (value === null || value === undefined || !isFinite(value)) {
    return '–';
  }

  const text = `${Math.abs(value * 100).toFixed(digits)}%`;
  if (value < 0) {
    return MINUS + text;
  }

  return signed && value > 0 ? `+${text}` : text;
}

/** value ÷ base as a share; "–" when there is no base. */
export function share(value: number, base: number, digits = 1): string {
  return base > 0 ? pct(value / base, digits, false) : '–';
}

export function tone(value: number | null | undefined): 'up' | 'down' | 'flat' {
  if (value === null || value === undefined || Math.abs(value) < 1e-9) {
    return 'flat';
  }

  return value > 0 ? 'up' : 'down';
}

/** "MarketNeutral" → "Market Neutral". Enum values arrive from the API by name. */
export function label(value: string | null | undefined): string {
  return (value ?? '').replace(/([a-z])([A-Z])/g, '$1 $2');
}

/** A price: thousands separators from 1,000 up, and the Katalepsis dynamic-decimal rule below that. */
@Pipe({ name: 'price' })
export class PricePipe implements PipeTransform {
  private readonly small = new DynamicDecimalPipe();

  transform(value: number | null | undefined): string {
    if (value === null || value === undefined) {
      return '–';
    }

    return Math.abs(value) >= 1000 ? '$' + value.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : '$' + this.small.transform(value);
  }
}

@Pipe({ name: 'usd' })
export class UsdPipe implements PipeTransform {
  transform(value: number | null | undefined, digits = 1): string {
    return usd(value, digits);
  }
}

@Pipe({ name: 'compact' })
export class CompactPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return compactUsd(value);
  }
}

@Pipe({ name: 'pct' })
export class PctPipe implements PipeTransform {
  transform(value: number | null | undefined, digits = 1, signed = true): string {
    return pct(value, digits, signed);
  }
}

@Pipe({ name: 'share' })
export class SharePipe implements PipeTransform {
  transform(value: number, base: number, digits = 1): string {
    return share(value, base, digits);
  }
}

@Pipe({ name: 'tone' })
export class TonePipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return tone(value);
  }
}

@Pipe({ name: 'label' })
export class LabelPipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return label(value);
  }
}

export const FORMAT_PIPES = [UsdPipe, CompactPipe, PctPipe, SharePipe, TonePipe, LabelPipe, PricePipe] as const;
