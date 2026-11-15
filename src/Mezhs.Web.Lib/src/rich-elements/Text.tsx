import type { RichProps } from "./Element";
import { read, tone } from "./Style";
export const textComponent = ({ children, ...props }: RichProps) => {
    const weight = read(props, "weight");
    const size = read(props, "size");
    return <span className={tone(props).trim()} style={{
      fontWeight: read(props, "strong") === "true" || weight === "bold" ? 700 : weight === "medium" ? 500 : undefined,
      fontSize: ({ "2xs": ".72em", xs: ".8em", sm: ".9em", lg: "1.15em" } as Record<string, string>)[size],
      textAlign: read(props, "textAlign") === "center" ? "center" : undefined,
      fontVariantNumeric: read(props, "tabularNums") === "true" ? "tabular-nums" : undefined,
    }}>{children}</span>;
  };
