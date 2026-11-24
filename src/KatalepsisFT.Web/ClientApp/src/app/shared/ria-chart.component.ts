// Copied from Katalepsis (Rest/ClientApp/src/app/ria/shared/ria-chart.component.ts, commit 9847e4f). Unchanged.
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  afterRenderEffect,
  input,
  viewChild,
} from '@angular/core';
import {
  Chart,
  ChartConfiguration,
  ChartDataset,
  registerables,
} from 'chart.js';

Chart.register(...registerables);

export interface RiaChartSeries {
  label: string;
  data: (number | null)[];
  color: string;
  fill?: boolean;
  dash?: number[];
  width?: number;
}

export type RiaYFormat = 'usd' | 'multiple' | 'percent';

function formatY(value: number, format: RiaYFormat): string {
  switch (format) {
    case 'usd':
      return value.toLocaleString('en-US', {
        style: 'currency',
        currency: 'USD',
        notation: 'compact',
        maximumFractionDigits: 1,
      });
    case 'percent':
      return `${(value * 100).toFixed(0)}%`;
    default:
      return `${+value.toFixed(2)}x`;
  }
}

function shortDay(iso: string): string {
  const [year, month, day]: string[] = iso.slice(0, 10).split('-');
  return day ? `${month}-${day}-${year.slice(2)}` : iso;
}

/** Small Chart.js line chart over a category axis of ISO dates (USD, multiple or percent on the y axis). */
@Component({
  selector: 'app-ria-chart',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="chart-box" [style.height.px]="height()">
      <canvas
        #canvas
        role="img"
        [attr.aria-label]="ariaLabel()"
        [attr.data-testid]="testId()"></canvas>
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .chart-box {
        position: relative;
        width: 100%;
      }
    `,
  ],
})
export class RiaChartComponent implements OnDestroy {
  public readonly labels = input.required<string[]>();
  public readonly series = input.required<RiaChartSeries[]>();
  public readonly yFormat = input<RiaYFormat>('usd');
  public readonly log = input<boolean>(false);
  public readonly height = input<number>(280);
  public readonly ariaLabel = input<string>('Chart');
  public readonly testId = input<string>('ria-chart');

  private readonly canvas =
    viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private chart: Chart | null = null;

  constructor() {
    afterRenderEffect(onCleanup => {
      const config: ChartConfiguration = this.buildConfig();
      this.chart?.destroy();
      this.chart = new Chart(this.canvas().nativeElement, config);
      onCleanup(() => {
        this.chart?.destroy();
        this.chart = null;
      });
    });
  }

  ngOnDestroy(): void {
    this.chart?.destroy();
    this.chart = null;
  }

  private buildConfig(): ChartConfiguration {
    const labels: string[] = this.labels();
    const format: RiaYFormat = this.yFormat();
    const datasets: ChartDataset[] = this.series().map(
      (s: RiaChartSeries): ChartDataset =>
        ({
          type: 'line',
          label: s.label,
          data: s.data,
          borderColor: s.color,
          backgroundColor: softFill(s),
          borderWidth: s.width ?? 1.8,
          borderDash: s.dash ?? [],
          // A line that never touches zero is filled down to the bottom of the chart; one that crosses it, to zero.
          fill: s.fill === true ? (aboveZero(s.data) ? 'start' : 'origin') : false,
          pointRadius: 0,
          pointHoverRadius: 3,
          tension: 0,
          spanGaps: false,
        }) as ChartDataset
    );
    return {
      type: 'line',
      data: { labels, datasets },
      options: {
        animation: false,
        responsive: true,
        maintainAspectRatio: false,
        interaction: { mode: 'index', intersect: false },
        scales: {
          x: {
            grid: { display: false },
            ticks: {
              maxTicksLimit: 9,
              autoSkipPadding: 24,
              maxRotation: 0,
              callback: (_v: unknown, index: number): string =>
                shortDay(labels[index] ?? ''),
            },
          },
          y: {
            type: this.log() ? 'logarithmic' : 'linear',
            border: { display: false },
            grid: { color: '#eef0f4' },
            ticks: {
              callback: (v: string | number): string => formatY(+v, format),
            },
          },
        },
        plugins: {
          legend: {
            display: datasets.length > 1,
            position: 'bottom',
            // A short line in each series' own colour and dash, instead of a filled box.
            labels: { usePointStyle: true, pointStyle: 'line', boxWidth: 28, padding: 18, color: '#475467', font: { family: 'Inter Variable, Inter, system-ui, sans-serif', size: 12 } },
          },
          tooltip: {
            callbacks: {
              title: (items): string => shortDay(items[0]?.label ?? ''),
              label: (item): string =>
                ` ${item.dataset.label}: ${formatY(item.parsed.y ?? 0, format)}`,
            },
          },
        },
      },
    };
  }
}

/** Inline SVG sparkline (no chart.js) for the model cards. */
@Component({
  selector: 'app-ria-sparkline',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (path(); as d) {
      <svg
        viewBox="0 0 200 44"
        preserveAspectRatio="none"
        class="spark"
        role="img"
        aria-label="Hypothetical growth of 1 unit">
        <path [attr.d]="area(d)" fill="#52c41a22" />
        <path
          [attr.d]="d"
          fill="none"
          stroke="#389e0d"
          stroke-width="1.6"
          vector-effect="non-scaling-stroke" />
      </svg>
    } @else {
      <div class="spark empty muted small">No backtest yet</div>
    }
  `,
  styles: [
    `
      .spark {
        display: block;
        width: 100%;
        height: 44px;
      }
      .empty {
        line-height: 44px;
        opacity: 0.6;
      }
    `,
  ],
})
export class RiaSparklineComponent {
  public readonly values = input.required<number[]>();

  protected path(): string | null {
    const values: number[] = this.values().filter(v => Number.isFinite(v));
    if (values.length < 2) {
      return null;
    }
    const min: number = Math.min(...values);
    const max: number = Math.max(...values);
    const span: number = max - min || 1;
    return values
      .map((v: number, i: number) => {
        const x: number = (i / (values.length - 1)) * 200;
        const y: number = 40 - ((v - min) / span) * 36;
        return `${i === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`;
      })
      .join(' ');
  }

  protected area(line: string): string {
    return `${line} L200,44 L0,44 Z`;
  }
}

function aboveZero(data: readonly (number | null)[]): boolean {
  return data.every((value) => value === null || value > 0);
}

/** The colour under a filled line: for a line above zero it fades out towards the bottom of the chart. */
function softFill(series: RiaChartSeries): string | ((context: { chart: Chart }) => CanvasGradient | string) {
  const flat = `${series.color}33`;
  if (series.fill !== true || !aboveZero(series.data)) {
    return flat;
  }

  return ({ chart }) => {
    const area = chart.chartArea;
    if (!area) {
      return flat;
    }

    const gradient = chart.ctx.createLinearGradient(0, area.top, 0, area.bottom);
    gradient.addColorStop(0, `${series.color}38`);
    gradient.addColorStop(1, `${series.color}00`);
    return gradient;
  };
}
