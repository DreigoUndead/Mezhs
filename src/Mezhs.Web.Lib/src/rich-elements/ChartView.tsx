import type { ReactNode } from "react";

// Render a bounded subset of declarative chart DATA; nothing supplied by a model is executed.
type ChartSeries = { dataKey: string; label: string; valueFormat?: string; valuePrefix?: string; valueSuffix?: string };
type ChartDatum = Record<string, unknown>;

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value)
    ? value as Record<string, unknown> : null;
}

function parseChart(value: unknown) {
  const chart = record(value);
  if (!chart || chart.chartType !== "bar" || typeof chart.xKey !== "string" ||
      !Array.isArray(chart.series) || !Array.isArray(chart.data) ||
      chart.series.length === 0 || chart.series.length > 4 ||
      chart.data.length === 0 || chart.data.length > 40) return null;
  const series: ChartSeries[] = [];
  for (const item of chart.series) {
    const entry = record(item);
    if (!entry || typeof entry.dataKey !== "string" || typeof entry.label !== "string") return null;
    series.push({
      dataKey: entry.dataKey,
      label: entry.label.slice(0, 120),
      valueFormat: typeof entry.valueFormat === "string" ? entry.valueFormat : undefined,
      valuePrefix: typeof entry.valuePrefix === "string" ? entry.valuePrefix.slice(0, 16) : undefined,
      valueSuffix: typeof entry.valueSuffix === "string" ? entry.valueSuffix.slice(0, 16) : undefined,
    });
  }
  const rows: Array<{ label: string; values: number[] }> = [];
  for (const item of chart.data) {
    const entry = record(item);
    if (!entry) return null;
    const label = entry[chart.xKey];
    if (typeof label !== "string" && typeof label !== "number") return null;
    const values = series.map((item) => entry[item.dataKey]);
    if (values.some(v => typeof v !== "number" || !Number.isFinite(v) || v < 0 || v > 1e15)) return null;
    rows.push({ label: String(label).slice(0, 120), values: values as number[] });
  }
  const meta = record(chart.meta);
  const footer = typeof meta?.footer === "string" ? meta.footer.slice(0, 350) : "";
  const min = typeof chart.yAxisMin === "number" && Number.isFinite(chart.yAxisMin) &&
    chart.yAxisMin >= 0 ? chart.yAxisMin : 0;
  return { series, rows, footer, min };
}

function formatted(value: number, series: ChartSeries) {
  const style = series.valueFormat === "compact" ? "compact" : "standard";
  const digits = series.valueFormat === "integer" ? 0 : (style === "compact" ? 1 : 2);
  return (series.valuePrefix ?? "") + new Intl.NumberFormat(undefined, {
    notation: style, maximumFractionDigits: digits,
  }).format(value) + (series.valueSuffix ?? "");
}

export function RichChart({ content }: { content: unknown; children?: ReactNode }) {
  const data = parseChart(content);
  if (!data) return <div className="rich-chart-unsupported">Unsupported chart data</div>;
  const max = Math.max(data.min + 1, ...data.rows.flatMap(row => row.values));
  const range = max - data.min;
  return (
    <figure className="rich-chart" aria-label="Bar chart">
      {data.series.length > 1 && <div className="rich-chart-legend">
        {data.series.map((item, i) => <span key={item.dataKey}>
          <i className={`rich-chart-swatch rich-chart-series-${i}`} />{item.label}
        </span>)}
      </div>}
      <div className="rich-chart-rows">
        {data.rows.map((row, index) => (
          <div className="rich-chart-row" key={index}>
            <div className="rich-chart-category">{row.label}</div>
            <div className="rich-chart-measures">
              {row.values.map((value, i) => (
                <div className="rich-chart-measure" key={i}>
                  <div className="rich-chart-track">
                    <div className={`rich-chart-bar rich-chart-series-${i}`}
                      style={{ width: `${Math.max(0, Math.min(100, (value - data.min) / range * 100))}%` }} />
                  </div>
                  <span className="rich-chart-value" title={data.series[i].label}>
                    {formatted(value, data.series[i])}
                  </span>
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>
      {data.footer && <figcaption>{data.footer}</figcaption>}
    </figure>
  );
}
