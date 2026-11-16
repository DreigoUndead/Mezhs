import type { RichProps } from "./Element";
import { read, layout } from "./Style";
export const gridComponent = ({ children, ...props }: RichProps) => {
    const columns = read(props, "columns");
    return <div className="rich-grid" style={{
      ...layout(props),
      gridTemplateColumns: `repeat(${/^\d+$/.test(columns) ? Math.min(6, Math.max(1, Number(columns))) : 1},minmax(0,1fr))`,
    }}>{children}</div>;
  };
