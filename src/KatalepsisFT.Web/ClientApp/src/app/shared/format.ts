import { Pipe, PipeTransform } from '@angular/core';
import { DynamicDecimalPipe } from './dynamic-decimal.pipe';

const MINUS = '−';

/** 205_400_000 → "$205.4M"; 950_000 → "$950K"; negative values get a minus sign in front. */
export function usd(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined) {
    return '–';
  }

  const abs = Math.abs(value);
  const sign = value < 0 ? MINUS : '';
  if (abs >= 1e9) {
    return `${sign}$${(abs / 1e9).toFixed(2)}B`;
  }

  if (abs >= 1e6) {
    return `${sign}$${(abs / 1e6).toFixed(digits)}M`;
  }

  if (abs >= 1e3) {
    return `${sign}$${(abs / 1e3).toFixed(0)}K`;
  }

  return `${sign}$${abs.toFixed(0)}`;
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

export const FORMAT_PIPES = [UsdPipe, PctPipe, SharePipe, TonePipe, LabelPipe, PricePipe] as const;
