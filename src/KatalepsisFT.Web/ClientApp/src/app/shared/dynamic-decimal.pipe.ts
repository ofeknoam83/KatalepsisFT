// Copied from Katalepsis (Rest/ClientApp/src/app/common/pipes/dynamic-decimal.pipe.ts, commit 9847e4f).
// Changed: the pipe is standalone, and the lodash isString check is a typeof check so that lodash is not needed.
import { Pipe, PipeTransform } from '@angular/core';

@Pipe({
  name: 'dynamicDecimal',
})
export class DynamicDecimalPipe implements PipeTransform {
  public transform(value: number, val1: number = 10, val2: number = 1, decimalPlaces: number = 8): string {
    // Check if value is a number
    if (isNaN(value) || !value) {
      return '0';
    }

    if (typeof value === 'string') {
      value = +value;
    }

    const absValue: number = Math.abs(value);
    if (absValue >= val1) {
      decimalPlaces = 2;
    } else if (absValue >= val2) {
      decimalPlaces = 4;
    }

    return this.removeTrailingZeros(value.toFixed(decimalPlaces));
  }

  private removeTrailingZeros(value: string): string {
    if (value.indexOf('.') === -1) {
      return value;
    }

    const decimalIndex: number = value.indexOf('.');
    // Changed: starts at the decimal point, so that "1.0000" becomes "1" instead of staying as it is.
    let lastNonZeroIndex: number = decimalIndex;

    for (let i: number = value.length - 1; i > decimalIndex; i--) {
      if (value[i] !== '0') {
        lastNonZeroIndex = i;
        break;
      }
    }

    // Check if there are any non-zero digits after the decimal point
    if (lastNonZeroIndex > decimalIndex) {
      return value.substring(0, lastNonZeroIndex + 1);
    } else {
      // If no non-zero digits after the decimal point, remove the decimal point and trailing zeros
      return value.substring(0, decimalIndex);
    }
  }
}
