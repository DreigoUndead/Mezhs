import type { CSSProperties } from "react";
import type { RichProps } from "./Element";
export const read = (props: RichProps, key: string) =>
  typeof props["data-mezhs-" + key] === "string" ? props["data-mezhs-" + key] as string : "";

export function layout(props: RichProps): CSSProperties {
  const gap = read(props, "gap");
  const align = read(props, "align");
  const justify = read(props, "justify");
  const positions: Record<string, CSSProperties["alignItems"]> = { start: "flex-start", center: "center", end: "flex-end" };
  const justifications: Record<string, CSSProperties["justifyContent"]> = { start: "flex-start", center: "center", end: "flex-end", between: "space-between", around: "space-around" };
  return {
    gap: /^\d+(?:\.\d+)?$/.test(gap) ? Math.min(12, Number(gap)) * 4 : undefined,
    alignItems: positions[align],
    justifyContent: justifications[justify],
  };
}

export function boundedSpacing(value: unknown): number | undefined {
  const number = typeof value === "number" ? value : typeof value === "string" && /^\d+$/.test(value) ? Number(value) : NaN;
  return Number.isFinite(number) && number >= 0 && number <= 12 ? number * 4 : undefined;
}

export function structured(props: RichProps, key: string): Record<string, unknown> | null {
  const value = read(props, key);
  if (!value.startsWith("json:")) return null;
  try {
    const data: unknown = JSON.parse(decodeURIComponent(value.slice(5)));
    return data && typeof data === "object" && !Array.isArray(data) ? data as Record<string, unknown> : null;
  } catch {
    return null;
  }
}

export function surface(props: RichProps): CSSProperties {
  const padding = read(props, "padding");
  const paddingObject = structured(props, "padding");
  const border = structured(props, "border");
  const left = border?.left && typeof border.left === "object" ? border.left as Record<string, unknown> : null;
  const width = read(props, "width");
  const flex = read(props, "flex");
  const radius = read(props, "radius");
  const leftSize = boundedSpacing(left?.size);
  return {
    ...layout(props),
    padding: boundedSpacing(padding),
    paddingLeft: boundedSpacing(paddingObject?.left),
    borderRadius: ({ sm: 5, md: 9, lg: 14, xl: 18, full: 999 } as Record<string, number>)[radius],
    background: read(props, "background") === "surface-secondary" ? "#eeebe5" : undefined,
    width: /^(?:100|[1-9]?\d)%$/.test(width) ||
      /^\d{1,3}(?:\.\d+)?px$/.test(width) && parseFloat(width) <= 500 ? width : undefined,
    flex: /^(?:1|2|3)$/.test(flex) ? Number(flex) : undefined,
    borderLeft: leftSize !== undefined && leftSize > 0
      ? (leftSize / 4) + "px solid " + (left?.color === "strong" ? "#504e49" : "#d5d0c6")
      : undefined,
  };
}

export function tone(props: RichProps) {
  const color = read(props, "color");
  return /^(success|danger|warning|secondary|muted)$/.test(color) ? " rich-tone-" + color : "";
}

